using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;

namespace SharpClaw.Modules.AgentOrchestration.Contracts;

public sealed record ContextModuleContract;

public sealed record PermissionModuleContract;

public sealed record AgentsModuleContract;

public static class ContextAccessCapabilities
{
    public const string ReadCrossThreadHistory = "read_cross_thread_history";
    public const string ReadHistory = "context_read";
    public const string CreateThread = "context_create";
    public const string CommitExchange = "context_write";
}

public sealed record ContextAccessRequest(
    Guid ChannelId,
    Guid? OwnerAgentId,
    IReadOnlyList<Guid> AllowedAgentIds,
    Guid? DefaultContextAgentId,
    IReadOnlyList<Guid> ContextAllowedAgentIds,
    bool SourceChannelOptedIn,
    Guid? ContextId = null,
    string Capability = ContextAccessCapabilities.ReadCrossThreadHistory);

public sealed record AccessDecision(
    bool Allowed,
    string Code,
    string Message)
{
    public static AccessDecision Allow(string code = "allowed") =>
        new(true, code, "Access allowed.");

    public static AccessDecision Deny(string code, string message) =>
        new(false, code, message);
}

public sealed record PermissionContextAccessAction(
    ContextAccessRequest Request);

public sealed record PermissionAgentAccessAction(
    string Capability,
    Guid? TargetAgentId);

public static class PermissionActionDescriptors
{
    private static readonly ActionRepeatPolicy RepeatPolicy =
        new(ActionRepeatKind.Idempotent, 3, TimeSpan.FromMilliseconds(50), "permission");

    private static readonly IReadOnlyList<ActionSafePoint> SafePoints =
    [
        ActionSafePoint.BeforeTerminal,
        ActionSafePoint.AfterTerminal,
    ];

    public static readonly ActionDescriptor<PermissionContextAccessAction, AccessDecision> ContextAccess =
        new(new("permission.context-access"), 1, "permission.authorization",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Observe,
            true, false, RepeatPolicy, null, TimeSpan.FromSeconds(10))
        {
            InputSchema = ModuleSchemaIdentity.ActionInput(
                new("permission.context-access"),
                1,
                typeof(PermissionContextAccessAction)),
            ResultSchema = ModuleSchemaIdentity.ActionResult(
                new("permission.context-access"),
                1,
                typeof(AccessDecision)),
            SafePoints = SafePoints,
        };

    public static readonly ActionDescriptor<PermissionAgentAccessAction, AccessDecision> AgentAccess =
        new(new("permission.agent-access"), 1, "permission.authorization",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Observe,
            true, false, RepeatPolicy, null, TimeSpan.FromSeconds(10))
        {
            InputSchema = ModuleSchemaIdentity.ActionInput(
                new("permission.agent-access"),
                1,
                typeof(PermissionAgentAccessAction)),
            ResultSchema = ModuleSchemaIdentity.ActionResult(
                new("permission.agent-access"),
                1,
                typeof(AccessDecision)),
            SafePoints = SafePoints,
        };
}

public interface IPermissionActionEntry
{
    ValueTask<AccessDecision> EvaluateContextAsync(
        HostActionEntryRequestContext hostContext,
        ContextAccessRequest request,
        CancellationToken ct = default);

    ValueTask<AccessDecision> EvaluateAgentAsync(
        HostActionEntryRequestContext hostContext,
        string capability,
        Guid? targetAgentId,
        CancellationToken ct = default);

    ValueTask<AccessDecision> EvaluateContextAsync<TParentAction>(
        ActionContext<TParentAction> parentContext,
        ContextAccessRequest request,
        CancellationToken ct = default);

    ValueTask<AccessDecision> EvaluateAgentAsync<TParentAction>(
        ActionContext<TParentAction> parentContext,
        string capability,
        Guid? targetAgentId,
        CancellationToken ct = default);
}

public interface IModuleActionAuthorization
{
    RequestPrincipal Caller { get; }

    ValueTask<AccessDecision> EvaluateContextAsync(
        ContextAccessRequest request,
        CancellationToken ct = default);

    ValueTask<AccessDecision> EvaluateAgentAsync(
        string capability,
        Guid? targetAgentId,
        CancellationToken ct = default);
}

public sealed class ModuleActionAuthorization<TAction>(
    ActionContext<TAction> context,
    HostPermissionActionEntry permission) : IModuleActionAuthorization
{
    public RequestPrincipal Caller => context.Caller;

    public ValueTask<AccessDecision> EvaluateContextAsync(
        ContextAccessRequest request,
        CancellationToken ct = default) =>
        permission.EvaluateContextAsync(context, request, ct);

    public ValueTask<AccessDecision> EvaluateAgentAsync(
        string capability,
        Guid? targetAgentId,
        CancellationToken ct = default) =>
        permission.EvaluateAgentAsync(context, capability, targetAgentId, ct);
}

public sealed class ChatOperationAuthorization(
    ChatOperationContext context,
    HostPermissionActionEntry permission) : IModuleActionAuthorization
{
    public RequestPrincipal Caller => context.Caller;

    public ValueTask<AccessDecision> EvaluateContextAsync(
        ContextAccessRequest request,
        CancellationToken ct = default) =>
        permission.EvaluateContextAsync(context, request, ct);

    public ValueTask<AccessDecision> EvaluateAgentAsync(
        string capability,
        Guid? targetAgentId,
        CancellationToken ct = default) =>
        permission.EvaluateAgentAsync(context, capability, targetAgentId, ct);
}

public sealed class HostPermissionActionEntry(IHostActionEntry host) : IPermissionActionEntry
{
    public async ValueTask<AccessDecision> EvaluateContextAsync(
        ChatOperationContext context,
        ContextAccessRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var hostEntry = context.HostActionEntry
            ?? throw new InvalidOperationException(
                "The chat operation has no host action entry.");
        var action = new PermissionContextAccessAction(request);
        var decision = await hostEntry.InvokeCrossSidecarAsync(
            new ModuleCrossSidecarActionEntryRequest<
                PermissionContextAccessAction,
                AccessDecision>(
                PermissionActionDescriptors.ContextAccess,
                action),
            ct);
        return RequireResult(
            PermissionActionDescriptors.ContextAccess.Key.Value,
            decision,
            ct);
    }

    public async ValueTask<AccessDecision> EvaluateAgentAsync(
        ChatOperationContext context,
        string capability,
        Guid? targetAgentId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var hostEntry = context.HostActionEntry
            ?? throw new InvalidOperationException(
                "The chat operation has no host action entry.");
        var decision = await hostEntry.InvokeCrossSidecarAsync(
            new ModuleCrossSidecarActionEntryRequest<
                PermissionAgentAccessAction,
                AccessDecision>(
                PermissionActionDescriptors.AgentAccess,
                new PermissionAgentAccessAction(capability, targetAgentId)),
            ct);
        return RequireResult(
            PermissionActionDescriptors.AgentAccess.Key.Value,
            decision,
            ct);
    }

    public async ValueTask<AccessDecision> EvaluateContextAsync(
        HostActionEntryRequestContext hostContext,
        ContextAccessRequest request,
        CancellationToken ct = default)
    {
        var action = new PermissionContextAccessAction(request);
        var decision = await InvokeAsync(
            PermissionActionDescriptors.ContextAccess,
            action,
            hostContext,
            ct);
        return decision;
    }

    public async ValueTask<AccessDecision> EvaluateAgentAsync(
        HostActionEntryRequestContext hostContext,
        string capability,
        Guid? targetAgentId,
        CancellationToken ct = default)
    {
        var action = new PermissionAgentAccessAction(
            capability,
            targetAgentId);
        var decision = await InvokeAsync(
            PermissionActionDescriptors.AgentAccess,
            action,
            hostContext,
            ct);
        return decision;
    }

    public async ValueTask<AccessDecision> EvaluateContextAsync<TParentAction>(
        ActionContext<TParentAction> parentContext,
        ContextAccessRequest request,
        CancellationToken ct = default)
    {
        var action = new PermissionContextAccessAction(request);
        var hostEntry = parentContext.HostActionEntry
            ?? throw new InvalidOperationException(
                "The parent action context has no host action entry.");
        var decision = await hostEntry.InvokeCrossSidecarAsync(
            new ModuleCrossSidecarActionEntryRequest<
                PermissionContextAccessAction,
                AccessDecision>(
                PermissionActionDescriptors.ContextAccess,
                action),
            ct);
        return RequireResult(
            PermissionActionDescriptors.ContextAccess.Key.Value,
            decision,
            ct);
    }

    public async ValueTask<AccessDecision> EvaluateAgentAsync<TParentAction>(
        ActionContext<TParentAction> parentContext,
        string capability,
        Guid? targetAgentId,
        CancellationToken ct = default)
    {
        var action = new PermissionAgentAccessAction(
            capability,
            targetAgentId);
        var hostEntry = parentContext.HostActionEntry
            ?? throw new InvalidOperationException(
                "The parent action context has no host action entry.");
        var decision = await hostEntry.InvokeCrossSidecarAsync(
            new ModuleCrossSidecarActionEntryRequest<
                PermissionAgentAccessAction,
                AccessDecision>(
                PermissionActionDescriptors.AgentAccess,
                action),
            ct);
        return RequireResult(
            PermissionActionDescriptors.AgentAccess.Key.Value,
            decision,
            ct);
    }

    private async ValueTask<AccessDecision> InvokeAsync<TAction>(
        ActionDescriptor<TAction, AccessDecision> descriptor,
        TAction action,
        HostActionEntryRequestContext hostContext,
        CancellationToken ct)
    {
        var outcome = await host.InvokeCrossSidecarAsync(
            new ModuleCrossSidecarActionEntryRequest<TAction, AccessDecision>(
                descriptor,
                action),
            ct);
        return RequireResult(descriptor.Key.Value, outcome, ct);
    }

    private static AccessDecision RequireResult(
        string actionKey,
        IActionOutcome<AccessDecision> outcome,
        CancellationToken ct) =>
        outcome.Kind switch
        {
            ActionOutcomeKind.Completed => outcome.Result
                ?? throw new InvalidOperationException(
                    $"The {actionKey} permission action completed without a decision."),
            ActionOutcomeKind.Cancelled => throw new OperationCanceledException(
                $"The {actionKey} permission action was cancelled.", ct),
            ActionOutcomeKind.Deferred => throw new InvalidOperationException(
                $"The {actionKey} permission action was deferred."),
            ActionOutcomeKind.Failed => throw new InvalidOperationException(
                FormatFailure(actionKey, outcome.Error)),
            ActionOutcomeKind.Uncertain => throw new InvalidOperationException(
                FormatUncertainty(actionKey, outcome.Uncertainty)),
            _ => throw new InvalidOperationException(
                $"The {actionKey} permission action returned an unknown outcome."),
        };

    private static string FormatFailure(string actionKey, ExecutionError? error) =>
        error is null
            ? $"The {actionKey} permission action failed without an error."
            : $"The {actionKey} permission action failed: {error.Code}: {error.Message}";

    private static string FormatUncertainty(string actionKey, ActionUncertainty? uncertainty) =>
        uncertainty is null
            ? $"The {actionKey} permission action has uncertain execution."
            : $"The {actionKey} permission action has uncertain execution: {uncertainty.Code}: {uncertainty.Message}";
}
