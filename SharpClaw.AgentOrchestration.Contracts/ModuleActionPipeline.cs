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
