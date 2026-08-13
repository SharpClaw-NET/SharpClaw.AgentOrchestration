using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.TwoTierPermission;

public sealed class TwoTierPermissionPolicy(PermissionPolicyStore store)
    : IContextAccessPolicy, IAgentAccessPolicy
{
    public async ValueTask<ContextAccessDecision> EvaluateAsync(
        ContextAccessRequest request,
        CancellationToken ct = default)
    {
        var decision = await EvaluateDetailedAsync(request, ct);
        return decision.Allowed
            ? ContextAccessDecision.Allow(decision.Code)
            : ContextAccessDecision.Deny(decision.Code, decision.Message);
    }

    public async ValueTask<PermissionDecision> EvaluateDetailedAsync(
        ContextAccessRequest request,
        CancellationToken ct = default)
    {
        if (!request.Principal.IsAuthenticated)
            return PermissionDecision.Deny("unauthenticated", "Authentication is required.", 1);

        var policy = await store.GetAsync(request.Principal.SubjectId, ct);
        var isAdministrator = IsAdministrator(request.Principal, policy);
        var clearance = policy?.Clearance ?? PermissionClearance.Unset;

        if (policy?.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
            return PermissionDecision.Deny("policy_expired", "The permission policy has expired.", 1, clearance);

        if (policy?.HardDeniedCapabilities.Any(IsCrossThreadCapability) == true)
            return PermissionDecision.Deny("hard_denial", "A hard denial blocks this capability.", 1, clearance);

        if (!isAdministrator && !HasCrossThreadCapability(policy))
            return PermissionDecision.Deny("capability_denied", "The caller lacks the required capability.", 1, clearance);

        if (!isAdministrator && clearance is PermissionClearance.Unset or PermissionClearance.Denied)
            return PermissionDecision.Deny("clearance_denied", "The caller has no usable clearance.", 1, clearance);

        var isAssigned = isAdministrator
            || request.OwnerAgentId == ParseAgentId(request.Principal.SubjectId)
            || request.AllowedAgentIds.Contains(ParseAgentId(request.Principal.SubjectId))
            || request.DefaultContextAgentId == ParseAgentId(request.Principal.SubjectId)
            || request.ContextAllowedAgentIds.Contains(ParseAgentId(request.Principal.SubjectId));
        if (!isAssigned)
            return PermissionDecision.Deny("scope_denied", "The caller is not assigned to the source channel or context.", 2, clearance);

        var requiresOptIn = policy?.RequireSourceOptIn ?? true;
        if (!isAdministrator && clearance != PermissionClearance.Independent
            && requiresOptIn && !request.SourceChannelOptedIn)
        {
            return PermissionDecision.Deny(
                "source_opt_in_required",
                "The source channel has not enabled cross-thread access.",
                2,
                clearance);
        }

        return PermissionDecision.Allow(
            isAdministrator ? "administrator" : "assigned_and_authorized",
            2,
            isAdministrator ? PermissionClearance.Independent : clearance);
    }

    public async ValueTask<ContextAccessDecision> EvaluateAgentAsync(
        RequestPrincipal principal,
        string capability,
        Guid? targetAgentId,
        CancellationToken ct = default)
    {
        if (!principal.IsAuthenticated)
            return ContextAccessDecision.Deny("unauthenticated", "Authentication is required.");

        var policy = await store.GetAsync(principal.SubjectId, ct);
        if (IsAdministrator(principal, policy))
            return ContextAccessDecision.Allow("administrator");
        if (policy?.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
            return ContextAccessDecision.Deny("policy_expired", "The permission policy has expired.");
        if (policy is null || policy.Clearance is PermissionClearance.Unset or PermissionClearance.Denied)
            return ContextAccessDecision.Deny("clearance_denied", "The caller has no usable clearance.");
        if (!policy.Capabilities.Any(item => item.Equals(capability, StringComparison.OrdinalIgnoreCase)))
            return ContextAccessDecision.Deny("capability_denied", "The caller lacks the required agent capability.");
        if (targetAgentId is { } target
            && Guid.TryParse(principal.SubjectId, out var callerId)
            && target != callerId
            && !policy.Capabilities.Any(item => item.Equals("manage_agents", StringComparison.OrdinalIgnoreCase)))
        {
            return ContextAccessDecision.Deny("scope_denied", "The caller cannot change another agent.");
        }
        return ContextAccessDecision.Allow("agent_capability");
    }

    public async Task GrantAsync(
        RequestPrincipal caller,
        PermissionGrantAction action,
        CancellationToken ct = default)
    {
        var callerPolicy = await store.GetAsync(caller.SubjectId, ct);
        if (!IsAdministrator(caller, callerPolicy)
            && callerPolicy?.Clearance != PermissionClearance.Independent)
        {
            throw new UnauthorizedAccessException("Only an administrator or independent caller can grant permission.");
        }

        if (action.Clearance == PermissionClearance.Independent
            && !IsAdministrator(caller, callerPolicy))
        {
            throw new UnauthorizedAccessException("Only an administrator can grant independent clearance.");
        }

        await store.GrantAsync(caller, action, ct);
    }

    public async Task RevokeAsync(
        RequestPrincipal caller,
        PermissionRevokeAction action,
        CancellationToken ct = default)
    {
        var callerPolicy = await store.GetAsync(caller.SubjectId, ct);
        if (!IsAdministrator(caller, callerPolicy)
            && callerPolicy?.Clearance != PermissionClearance.Independent)
        {
            throw new UnauthorizedAccessException("Only an administrator or independent caller can revoke permission.");
        }

        await store.RevokeAsync(action.SubjectId, action.Capability, action.Scope, ct);
    }

    private static bool HasCrossThreadCapability(PermissionPolicyRecord? policy) =>
        policy?.Capabilities.Any(IsCrossThreadCapability) == true;

    private static bool IsCrossThreadCapability(string capability) =>
        capability.Equals("read_cross_thread_history", StringComparison.OrdinalIgnoreCase)
        || capability.Equals("CanReadCrossThreadHistory", StringComparison.OrdinalIgnoreCase);

    private static bool IsAdministrator(
        RequestPrincipal principal,
        PermissionPolicyRecord? policy) =>
        principal.Roles?.Any(IsAdministratorRole) == true
        || policy?.Roles.Any(IsAdministratorRole) == true;

    private static bool IsAdministratorRole(string role) =>
        role.Equals("admin", StringComparison.OrdinalIgnoreCase)
        || role.Equals("administrator", StringComparison.OrdinalIgnoreCase);

    private static Guid ParseAgentId(string subjectId) =>
        Guid.TryParse(subjectId, out var id) ? id : Guid.Empty;
}
