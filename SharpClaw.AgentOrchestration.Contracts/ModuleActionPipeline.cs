using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.AgentOrchestration.Contracts;

/// <summary>Runs package-owned operations through the host action graph.</summary>
public interface IModuleActionPipeline
{
    ValueTask<TResult> RunRequiredAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        ActionContext<TAction> context,
        Func<TAction, CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken ct = default);
}

/// <summary>Host-backed action pipeline adapter for an existing action context.</summary>
public sealed class ModuleActionPipeline(
    IActionDispatcher dispatcher) : IModuleActionPipeline
{
    public ValueTask<TResult> RunRequiredAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        ActionContext<TAction> context,
        Func<TAction, CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken ct = default) =>
        dispatcher.RunRequiredAsync(descriptor, context.Action, terminal, context.Snapshot, ct);
}

/// <summary>Invokes a typed module action through the host-owned action entry.</summary>
public sealed class HostModuleActionEntry(IHostActionEntry host)
{
    public async ValueTask<TResult> InvokeAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        RequestPrincipal caller,
        CancellationToken ct = default)
    {
        var request = new HostActionEntryRequest<TAction, TResult>(
            descriptor,
            action,
            caller,
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.Add(descriptor.DefaultTimeout));
        var outcome = await host.InvokeAsync(request, ct);
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
