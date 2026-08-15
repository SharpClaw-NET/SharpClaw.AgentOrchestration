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
    RequestPrincipal Caller,
    ContextAccessRequest Request);

public sealed record PermissionAgentAccessAction(
    RequestPrincipal Caller,
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
            SafePoints = SafePoints,
        };

    public static readonly ActionDescriptor<PermissionAgentAccessAction, PermissionDecision> AgentAccess =
        new(new("permission.agent-access"), 1, "permission.authorization",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Observe,
            true, false, RepeatPolicy, null, TimeSpan.FromSeconds(10))
        {
            SafePoints = SafePoints,
        };
}

public interface IPermissionActionEntry
{
    ValueTask<ContextAccessDecision> EvaluateContextAsync(
        RequestPrincipal caller,
        ContextAccessRequest request,
        CancellationToken ct = default);

    ValueTask<ContextAccessDecision> EvaluateAgentAsync(
        RequestPrincipal caller,
        string capability,
        Guid? targetAgentId,
        CancellationToken ct = default);
}

public sealed class HostPermissionActionEntry(IHostActionEntry host) : IPermissionActionEntry
{
    public async ValueTask<ContextAccessDecision> EvaluateContextAsync(
        RequestPrincipal caller,
        ContextAccessRequest request,
        CancellationToken ct = default)
    {
        var action = new PermissionContextAccessAction(
            caller,
            request with { Principal = caller });
        var decision = await InvokeAsync(PermissionActionDescriptors.ContextAccess, action, caller, ct);
        return ToContextDecision(decision);
    }

    public async ValueTask<ContextAccessDecision> EvaluateAgentAsync(
        RequestPrincipal caller,
        string capability,
        Guid? targetAgentId,
        CancellationToken ct = default)
    {
        var action = new PermissionAgentAccessAction(caller, capability, targetAgentId);
        var decision = await InvokeAsync(PermissionActionDescriptors.AgentAccess, action, caller, ct);
        return ToContextDecision(decision);
    }

    private async ValueTask<PermissionDecision> InvokeAsync<TAction>(
        ActionDescriptor<TAction, PermissionDecision> descriptor,
        TAction action,
        RequestPrincipal caller,
        CancellationToken ct)
    {
        var request = new HostActionEntryRequest<TAction, PermissionDecision>(
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
                    $"The {descriptor.Key.Value} permission action completed without a decision."),
            ActionOutcomeKind.Cancelled => throw new OperationCanceledException(
                $"The {descriptor.Key.Value} permission action was cancelled.", ct),
            ActionOutcomeKind.Deferred => throw new InvalidOperationException(
                $"The {descriptor.Key.Value} permission action was deferred."),
            ActionOutcomeKind.Failed => throw new InvalidOperationException(
                FormatFailure(descriptor.Key.Value, outcome.Error)),
            ActionOutcomeKind.Uncertain => throw new InvalidOperationException(
                FormatUncertainty(descriptor.Key.Value, outcome.Uncertainty)),
            _ => throw new InvalidOperationException(
                $"The {descriptor.Key.Value} permission action returned an unknown outcome."),
        };
    }

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
