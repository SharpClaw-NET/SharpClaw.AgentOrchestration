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
        var grant = await FindCrossThreadGrantAsync(request, ct);
        var clearance = policy?.Clearance ?? PermissionClearance.Unset;

        if (policy?.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
            return PermissionDecision.Deny("policy_expired", "The permission policy has expired.", 1, clearance);

        if (policy?.HardDeniedCapabilities.Any(IsCrossThreadCapability) == true)
            return PermissionDecision.Deny("hard_denial", "A hard denial blocks this capability.", 1, clearance);

        if (!isAdministrator && !HasCrossThreadCapability(policy) && grant is null)
            return PermissionDecision.Deny("capability_denied", "The caller lacks the required capability.", 1, clearance);

        if (!isAdministrator && clearance is PermissionClearance.Unset or PermissionClearance.Denied)
        {
            clearance = grant?.Clearance ?? clearance;
            if (clearance is PermissionClearance.Unset or PermissionClearance.Denied)
                return PermissionDecision.Deny("clearance_denied", "The caller has no usable clearance.", 1, clearance);
        }

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
            isAdministrator ? PermissionClearance.Independent : EffectiveClearance(clearance, grant?.Clearance));
    }

    public async ValueTask<PermissionDecision> EvaluateCapabilityAsync(
        RequestPrincipal caller,
        PermissionEvaluateAction action,
        CancellationToken ct = default)
    {
        if (!caller.IsAuthenticated)
            return PermissionDecision.Deny("unauthenticated", "Authentication is required.", 1);

        var subject = caller with { SubjectId = action.SubjectId };
        var policy = await store.GetAsync(subject.SubjectId, ct);
        var isAdministrator = IsAdministrator(caller, null)
            || (string.Equals(caller.SubjectId, action.SubjectId, StringComparison.Ordinal)
                && IsAdministrator(subject, policy));
        var clearance = policy?.Clearance ?? PermissionClearance.Unset;
        if (policy?.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
            return PermissionDecision.Deny("policy_expired", "The permission policy has expired.", 1, clearance);
        if (policy?.HardDeniedCapabilities.Any(item =>
                item.Equals(action.Capability, StringComparison.OrdinalIgnoreCase)) == true)
            return PermissionDecision.Deny("hard_denial", "A hard denial blocks this capability.", 1, clearance);

        var grants = await store.ListGrantsAsync(subject.SubjectId, action.Capability, ct);
        var scopedGrant = grants
            .Where(item => item.ExpiresAt is null || item.ExpiresAt > DateTimeOffset.UtcNow)
            .Where(item => ScopeMatches(item.Scope, action.Scope, null))
            .OrderByDescending(item => ScopeRank(item.Scope))
            .FirstOrDefault();
        if (!isAdministrator && !HasCapability(policy, action.Capability) && scopedGrant is null)
            return PermissionDecision.Deny("capability_denied", "The caller lacks the required capability.", 1, clearance);
        if (!isAdministrator && clearance is PermissionClearance.Unset or PermissionClearance.Denied)
            clearance = scopedGrant?.Clearance ?? clearance;
        if (!isAdministrator && clearance is PermissionClearance.Unset or PermissionClearance.Denied)
            return PermissionDecision.Deny("clearance_denied", "The caller has no usable clearance.", 1, clearance);

        return PermissionDecision.Allow(
            isAdministrator ? "administrator" : "capability_granted",
            clearance == PermissionClearance.Independent ? 2 : 1,
            isAdministrator ? PermissionClearance.Independent : EffectiveClearance(clearance, scopedGrant?.Clearance));
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
        var grants = await store.ListGrantsAsync(principal.SubjectId, capability, ct);
        var targetScope = targetAgentId is { } scopeAgentId ? $"agent:{scopeAgentId:N}" : "global";
        var hasGrant = grants.Any(item =>
            (item.ExpiresAt is null || item.ExpiresAt > DateTimeOffset.UtcNow)
            && ScopeMatches(item.Scope, targetScope, targetAgentId));
        if (!HasCapability(policy, capability) && !hasGrant)
            return ContextAccessDecision.Deny("capability_denied", "The caller lacks the required agent capability.");
        if (targetAgentId is { } changedAgentId
            && Guid.TryParse(principal.SubjectId, out var callerId)
            && changedAgentId != callerId
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

    private async Task<PermissionGrantRecord?> FindCrossThreadGrantAsync(
        ContextAccessRequest request,
        CancellationToken ct)
    {
        var grants = await store.ListGrantsAsync(
            request.Principal.SubjectId,
            "read_cross_thread_history",
            ct);
        var legacyGrants = await store.ListGrantsAsync(
            request.Principal.SubjectId,
            "CanReadCrossThreadHistory",
            ct);
        return grants.Concat(legacyGrants)
            .Where(item => item.ExpiresAt is null || item.ExpiresAt > DateTimeOffset.UtcNow)
            .Where(item => ScopeMatches(
                item.Scope,
                request.ContextId is { } contextId
                    ? $"context:{contextId:N}"
                    : $"channel:{request.ChannelId:N}",
                request.ContextId))
            .OrderByDescending(item => ScopeRank(item.Scope))
            .FirstOrDefault();
    }

    private static bool HasCapability(PermissionPolicyRecord? policy, string capability) =>
        policy?.Capabilities.Any(item => item.Equals(capability, StringComparison.OrdinalIgnoreCase)) == true;

    private static PermissionClearance EffectiveClearance(
        PermissionClearance policy,
        PermissionClearance? grant) =>
        grant is { } value && value > policy ? value : policy;

    private static bool ScopeMatches(string? grantScope, string requestedScope, Guid? targetId)
    {
        var scope = string.IsNullOrWhiteSpace(grantScope) ? "global" : grantScope.Trim();
        if (scope.Equals("global", StringComparison.OrdinalIgnoreCase)
            || scope.Equals(requestedScope, StringComparison.OrdinalIgnoreCase))
            return true;
        return targetId is { } id
            && scope.Equals($"agent:{id:N}", StringComparison.OrdinalIgnoreCase);
    }

    private static int ScopeRank(string? scope) =>
        scope?.StartsWith("context:", StringComparison.OrdinalIgnoreCase) == true ? 3
        : scope?.StartsWith("channel:", StringComparison.OrdinalIgnoreCase) == true ? 2
        : scope?.StartsWith("agent:", StringComparison.OrdinalIgnoreCase) == true ? 2
        : 1;

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
