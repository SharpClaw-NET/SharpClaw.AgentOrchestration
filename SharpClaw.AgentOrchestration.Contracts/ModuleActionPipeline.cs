using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.AgentOrchestration.Contracts;

/// <summary>Runs package-owned operations through the host action graph.</summary>
public interface IModuleActionPipeline
{
    ValueTask<TResult> RunRequiredAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        ActionContext<TAction> context,
        Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken ct = default);
}

/// <summary>Host-backed action pipeline adapter for an existing action context.</summary>
public sealed class ModuleActionPipeline(
    IActionDispatcher dispatcher) : IModuleActionPipeline
{
    public ValueTask<TResult> RunRequiredAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        ActionContext<TAction> context,
        Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken ct = default) =>
        dispatcher.RunRequiredAsync(descriptor, context.Action, terminal, context.Snapshot, ct);
}

/// <summary>Adapts one trusted module callback to the host terminal contract.</summary>
public sealed class DelegateHostActionEntryTerminal<TAction, TResult>(
    Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> callback) :
    IHostActionEntryTerminal<TAction, TResult>
{
    public Guid TerminalId { get; } = Guid.NewGuid();

    public ValueTask<TResult> InvokeAsync(
        ActionContext<TAction> context,
        CancellationToken ct) =>
        callback(context, ct);
}

/// <summary>Invokes a typed module action through the host-owned action entry.</summary>
public sealed class HostModuleActionEntry(IHostActionEntry host)
{
    public async ValueTask<TResult> InvokeAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        HostActionEntryRequestContext hostContext,
        Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        var request = new HostActionEntryRequest<TAction, TResult>(
            descriptor,
            action,
            hostContext);
        var outcome = await host.InvokeAsync(
            request,
            new DelegateHostActionEntryTerminal<TAction, TResult>(terminal),
            ct);
        return RequireResult(descriptor, outcome, ct);
    }

    public async ValueTask<TResult> InvokeNestedAsync<TParentAction, TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        ActionContext<TParentAction> parentContext,
        Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        var request = new HostActionEntryNestedRequest<TParentAction, TAction, TResult>(
            descriptor.Key,
            descriptor.Version,
            action,
            parentContext);
        var hostEntry = parentContext.HostActionEntry
            ?? throw new InvalidOperationException(
                "The parent action context has no host action entry.");
        var outcome = await hostEntry.InvokeNestedAsync(
            request,
            new DelegateHostActionEntryTerminal<TAction, TResult>(terminal),
            ct);
        return RequireResult(descriptor, outcome, ct);
    }

    private static TResult RequireResult<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        IActionOutcome<TResult> outcome,
        CancellationToken ct)
    {
        return outcome.Kind switch
        {
            ActionOutcomeKind.Completed => outcome.Result
                ?? throw new InvalidOperationException(
                    $"The {descriptor.Key.Value} action completed without a result."),
            ActionOutcomeKind.Cancelled => throw new OperationCanceledException(
                $"The {descriptor.Key.Value} action was cancelled.", ct),
            ActionOutcomeKind.Deferred => throw new InvalidOperationException(
                $"The {descriptor.Key.Value} action was deferred."),
            ActionOutcomeKind.Failed => throw new InvalidOperationException(
                FormatFailure(descriptor.Key.Value, outcome.Error)),
            ActionOutcomeKind.Uncertain => throw new InvalidOperationException(
                FormatUncertainty(descriptor.Key.Value, outcome.Uncertainty)),
            _ => throw new InvalidOperationException(
                $"The {descriptor.Key.Value} action returned an unknown outcome."),
        };
    }

    private static string FormatFailure(string actionKey, ExecutionError? error) =>
        error is null
            ? $"The {actionKey} action failed without an error."
            : $"The {actionKey} action failed: {error.Code}: {error.Message}";

    private static string FormatUncertainty(string actionKey, ActionUncertainty? uncertainty) =>
        uncertainty is null
            ? $"The {actionKey} action has uncertain execution."
            : $"The {actionKey} action has uncertain execution: {uncertainty.Code}: {uncertainty.Message}";
}
