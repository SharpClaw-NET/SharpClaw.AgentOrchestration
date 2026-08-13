using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.AgentOrchestration.Contracts;

/// <summary>Runs package-owned operations through the host action graph.</summary>
public interface IModuleActionPipeline
{
    ValueTask<TResult> RunRequiredAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        Func<TAction, CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken ct = default);
}

/// <summary>Host-backed action pipeline adapter used by all public module entry points.</summary>
public sealed class ModuleActionPipeline(
    IActionDispatcher dispatcher,
    ActionPipelineSnapshot snapshot) : IModuleActionPipeline
{
    public ValueTask<TResult> RunRequiredAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        Func<TAction, CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken ct = default) =>
        dispatcher.RunRequiredAsync(descriptor, action, terminal, snapshot, ct);
}
