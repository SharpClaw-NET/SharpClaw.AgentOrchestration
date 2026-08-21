using SharpClaw.Contracts.Modules;

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
    RequestPrincipal Principal,
    Guid ChannelId,
    Guid? OwnerAgentId,
    IReadOnlyList<Guid> AllowedAgentIds,
    Guid? DefaultContextAgentId,
    IReadOnlyList<Guid> ContextAllowedAgentIds,
    bool SourceChannelOptedIn,
    Guid? ContextId = null,
    string Capability = ContextAccessCapabilities.ReadCrossThreadHistory);

public sealed record ContextAccessDecision(
    bool Allowed,
    string Code,
    string Message)
{
    public static ContextAccessDecision Allow(string code = "allowed") =>
        new(true, code, "Access allowed.");

    public static ContextAccessDecision Deny(string code, string message) =>
        new(false, code, message);
}

public enum PermissionClearance
{
    Unset = 0,
    ApprovedBySameLevelUser = 1,
    ApprovedByWhitelistedUser = 2,
    ApprovedByPermittedAgent = 3,
    ApprovedByWhitelistedAgent = 4,
    Independent = 5,
    Restricted = 6,
}

public sealed record PermissionDecision(
    bool Allowed,
    string Code,
    string Message,
    int Tier,
    PermissionClearance Clearance)
{
    public static PermissionDecision Deny(
        string code,
        string message,
        int tier,
        PermissionClearance clearance = PermissionClearance.Restricted) =>
        new(false, code, message, tier, clearance);

    public static PermissionDecision Allow(
        string code,
        int tier,
        PermissionClearance clearance) =>
        new(true, code, "Permission granted.", tier, clearance);
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

    public static readonly ActionDescriptor<PermissionContextAccessAction, PermissionDecision> ContextAccess =
        new(new("permission.context-access"), 1, "permission.authorization",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Observe,
            true, false, RepeatPolicy, null, TimeSpan.FromSeconds(10))
        {
            InputSchema = new JsonSchemaReference(
                "sharpclaw.kernel.action.input.permission.context-access",
                1,
                "EF52C526C7B77C146B2D16A61B3BB1728BC4F8500763C8EF1A21FC65B981283B"),
            ResultSchema = new JsonSchemaReference(
                "sharpclaw.kernel.action.result.permission.context-access",
                1,
                "D929C5308B9236D3BA1B1423189D989070FFCFC31428FD3E03327E2E518DDC1B"),
            SafePoints = SafePoints,
        };

    public static readonly ActionDescriptor<PermissionAgentAccessAction, PermissionDecision> AgentAccess =
        new(new("permission.agent-access"), 1, "permission.authorization",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Observe,
            true, false, RepeatPolicy, null, TimeSpan.FromSeconds(10))
        {
            InputSchema = new JsonSchemaReference(
                "sharpclaw.kernel.action.input.permission.agent-access",
                1,
                "DE19359B0A13BDF1384C226E960AF700899FC7A08A6065CCB9C91AB7AD48D9F4"),
            ResultSchema = new JsonSchemaReference(
                "sharpclaw.kernel.action.result.permission.agent-access",
                1,
                "E2C4F31D2F6A8637E1AF2BA13B276A313BCC451E78E87EEE6B06F468D01C4287"),
            SafePoints = SafePoints,
        };
}

public interface IPermissionActionEntry
{
    ValueTask<ContextAccessDecision> EvaluateContextAsync(
        HostActionEntryRequestContext hostContext,
        ContextAccessRequest request,
        CancellationToken ct = default);

    ValueTask<ContextAccessDecision> EvaluateAgentAsync(
        HostActionEntryRequestContext hostContext,
        string capability,
        Guid? targetAgentId,
        CancellationToken ct = default);

    ValueTask<ContextAccessDecision> EvaluateContextAsync<TParentAction>(
        ActionContext<TParentAction> parentContext,
        ContextAccessRequest request,
        CancellationToken ct = default);

    ValueTask<ContextAccessDecision> EvaluateAgentAsync<TParentAction>(
        ActionContext<TParentAction> parentContext,
        string capability,
        Guid? targetAgentId,
        CancellationToken ct = default);
}

public interface IModuleActionAuthorization
{
    RequestPrincipal Caller { get; }

    ValueTask<ContextAccessDecision> EvaluateContextAsync(
        ContextAccessRequest request,
        CancellationToken ct = default);

    ValueTask<ContextAccessDecision> EvaluateAgentAsync(
        string capability,
        Guid? targetAgentId,
        CancellationToken ct = default);
}

public sealed class ModuleActionAuthorization<TAction>(
    ActionContext<TAction> context,
    HostPermissionActionEntry permission) : IModuleActionAuthorization
{
    public RequestPrincipal Caller => context.Caller;

    public ValueTask<ContextAccessDecision> EvaluateContextAsync(
        ContextAccessRequest request,
        CancellationToken ct = default) =>
        permission.EvaluateContextAsync(context, request, ct);

    public ValueTask<ContextAccessDecision> EvaluateAgentAsync(
        string capability,
        Guid? targetAgentId,
        CancellationToken ct = default) =>
        permission.EvaluateAgentAsync(context, capability, targetAgentId, ct);
}

public sealed class HostPermissionActionEntry(IHostActionEntry host) : IPermissionActionEntry
{
    public async ValueTask<ContextAccessDecision> EvaluateContextAsync(
        HostActionEntryRequestContext hostContext,
        ContextAccessRequest request,
        CancellationToken ct = default)
    {
        var action = new PermissionContextAccessAction(
            request with { Principal = hostContext.Caller });
        var decision = await InvokeAsync(
            PermissionActionDescriptors.ContextAccess,
            action,
            hostContext,
            ct);
        return ToContextDecision(decision);
    }

    public async ValueTask<ContextAccessDecision> EvaluateAgentAsync(
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
        return ToContextDecision(decision);
    }

    public async ValueTask<ContextAccessDecision> EvaluateContextAsync<TParentAction>(
        ActionContext<TParentAction> parentContext,
        ContextAccessRequest request,
        CancellationToken ct = default)
    {
        var action = new PermissionContextAccessAction(
            request with { Principal = parentContext.Caller });
        var nested = new HostActionEntryNestedRequest<
            TParentAction,
            PermissionContextAccessAction,
            PermissionDecision>(
            PermissionActionDescriptors.ContextAccess.Key,
            PermissionActionDescriptors.ContextAccess.Version,
            action,
            parentContext);
        var hostEntry = parentContext.HostActionEntry
            ?? throw new InvalidOperationException(
                "The parent action context has no host action entry.");
        var decision = await hostEntry.InvokeNestedAsync(
            nested,
            CreateUnavailableTerminal<PermissionContextAccessAction>(),
            ct);
        return ToContextDecision(RequireResult(
            PermissionActionDescriptors.ContextAccess.Key.Value,
            decision,
            ct));
    }

    public async ValueTask<ContextAccessDecision> EvaluateAgentAsync<TParentAction>(
        ActionContext<TParentAction> parentContext,
        string capability,
        Guid? targetAgentId,
        CancellationToken ct = default)
    {
        var action = new PermissionAgentAccessAction(
            capability,
            targetAgentId);
        var nested = new HostActionEntryNestedRequest<
            TParentAction,
            PermissionAgentAccessAction,
            PermissionDecision>(
            PermissionActionDescriptors.AgentAccess.Key,
            PermissionActionDescriptors.AgentAccess.Version,
            action,
            parentContext);
        var hostEntry = parentContext.HostActionEntry
            ?? throw new InvalidOperationException(
                "The parent action context has no host action entry.");
        var decision = await hostEntry.InvokeNestedAsync(
            nested,
            CreateUnavailableTerminal<PermissionAgentAccessAction>(),
            ct);
        return ToContextDecision(RequireResult(
            PermissionActionDescriptors.AgentAccess.Key.Value,
            decision,
            ct));
    }

    private async ValueTask<PermissionDecision> InvokeAsync<TAction>(
        ActionDescriptor<TAction, PermissionDecision> descriptor,
        TAction action,
        HostActionEntryRequestContext hostContext,
        CancellationToken ct)
    {
        var request = new HostActionEntryRequest<TAction, PermissionDecision>(
            descriptor,
            action,
            hostContext);
        var outcome = await host.InvokeAsync(
            request,
            CreateUnavailableTerminal<TAction>(),
            ct);
        return RequireResult(descriptor.Key.Value, outcome, ct);
    }

    private static IHostActionEntryTerminal<TAction, PermissionDecision>
        CreateUnavailableTerminal<TAction>() =>
        new DelegateHostActionEntryTerminal<TAction, PermissionDecision>(
            (_, _) => ValueTask.FromException<PermissionDecision>(
                new InvalidOperationException(
                    "The host must provide the Permission action terminal.")));

    private static PermissionDecision RequireResult(
        string actionKey,
        IActionOutcome<PermissionDecision> outcome,
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

    private static ContextAccessDecision ToContextDecision(PermissionDecision decision) =>
        decision.Allowed
            ? ContextAccessDecision.Allow(decision.Code)
            : ContextAccessDecision.Deny(decision.Code, decision.Message);

    private static string FormatFailure(string actionKey, ExecutionError? error) =>
        error is null
            ? $"The {actionKey} permission action failed without an error."
            : $"The {actionKey} permission action failed: {error.Code}: {error.Message}";

    private static string FormatUncertainty(string actionKey, ActionUncertainty? uncertainty) =>
        uncertainty is null
            ? $"The {actionKey} permission action has uncertain execution."
            : $"The {actionKey} permission action has uncertain execution: {uncertainty.Code}: {uncertainty.Message}";
}
