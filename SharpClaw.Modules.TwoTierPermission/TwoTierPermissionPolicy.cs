using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.TwoTierPermission;

public sealed class TwoTierPermissionPolicy(PermissionPolicyStore store)
    : IContextAccessPolicy, IAgentAccessPolicy
{
    private const string ManagePermissionsCapability = "manage_permissions";
    private const string ApprovePermissionsCapability = "approve_permissions";

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

        var capability = NormalizeCapability(request.Capability);
        var policy = await store.GetAsync(request.Principal.SubjectId, ct);
        var isAdministrator = IsAdministrator(request.Principal, policy);
        var requestedScope = RequestedScope(request);
        var grant = await FindUsableGrantAsync(
            request.Principal.SubjectId,
            capability,
            requestedScope,
            request.ContextId,
            ct);
        var clearance = EffectiveClearance(
            policy?.Clearance ?? PermissionClearance.Unset,
            grant?.Clearance);

        if (policy?.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
            return PermissionDecision.Deny("policy_expired", "The permission policy has expired.", 1, clearance);

        if (policy?.HardDeniedCapabilities.Any(item => IsSameCapability(item, capability)) == true)
            return PermissionDecision.Deny("hard_denial", "A hard denial blocks this capability.", 1, clearance);

        if (!isAdministrator && !HasCapability(policy, capability) && grant is null)
            return PermissionDecision.Deny("capability_denied", "The caller lacks the required capability.", 1, clearance);

        if (!isAdministrator && !IsUsableClearance(clearance))
            return PermissionDecision.Deny("clearance_denied", "The caller has no usable clearance.", 1, clearance);

        var callerAgentId = ParseAgentId(request.Principal.SubjectId);
        var isAssigned = isAdministrator
            || request.OwnerAgentId == callerAgentId
            || request.AllowedAgentIds.Contains(callerAgentId)
            || request.DefaultContextAgentId == callerAgentId
            || request.ContextAllowedAgentIds.Contains(callerAgentId);
        if (!isAssigned)
            return PermissionDecision.Deny("scope_denied", "The caller is not assigned to the source channel or context.", 2, clearance);

        var requiresOptIn = grant?.RequireSourceOptIn
            ?? policy?.RequireSourceOptIn
            ?? true;
        if (IsCrossThreadCapability(capability)
            && !isAdministrator
            && clearance != PermissionClearance.Independent
            && requiresOptIn
            && !request.SourceChannelOptedIn)
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
            isAdministrator
                ? PermissionClearance.Independent
                : EffectiveClearance(clearance, grant?.Clearance));
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
        var capability = NormalizeCapability(action.Capability);
        var scopedGrant = await FindUsableGrantAsync(
            subject.SubjectId,
            capability,
            NormalizeScope(action.Scope),
            null,
            ct);
        var clearance = EffectiveClearance(
            policy?.Clearance ?? PermissionClearance.Unset,
            scopedGrant?.Clearance);
        if (policy?.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
            return PermissionDecision.Deny("policy_expired", "The permission policy has expired.", 1, clearance);
        if (policy?.HardDeniedCapabilities.Any(item => IsSameCapability(item, capability)) == true)
            return PermissionDecision.Deny("hard_denial", "A hard denial blocks this capability.", 1, clearance);
        if (!isAdministrator && !HasCapability(policy, capability) && scopedGrant is null)
            return PermissionDecision.Deny("capability_denied", "The caller lacks the required capability.", 1, clearance);
        if (!isAdministrator && !IsUsableClearance(clearance))
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
        if (policy?.HardDeniedCapabilities.Any(item => IsSameCapability(item, capability)) == true)
            return ContextAccessDecision.Deny("hard_denial", "A hard denial blocks this capability.");
        var normalizedCapability = NormalizeCapability(capability);
        var targetScope = targetAgentId is { } scopeAgentId
            ? $"agent:{scopeAgentId:N}"
            : "global";
        var grant = await FindUsableGrantAsync(
            principal.SubjectId,
            normalizedCapability,
            targetScope,
            targetAgentId,
            ct);
        var clearance = EffectiveClearance(
            policy?.Clearance ?? PermissionClearance.Unset,
            grant?.Clearance);
        if (!IsUsableClearance(clearance))
            return ContextAccessDecision.Deny("clearance_denied", "The caller has no usable clearance.");
        if (!HasCapability(policy, normalizedCapability) && grant is null)
            return ContextAccessDecision.Deny("capability_denied", "The caller lacks the required agent capability.");
        if (targetAgentId is { } changedAgentId
            && Guid.TryParse(principal.SubjectId, out var callerId)
            && changedAgentId != callerId
            && !HasCapability(policy, "manage_agents"))
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
        RequireAuthenticated(caller);
        ValidateGrantAction(action);
        var callerPolicy = await store.GetAsync(caller.SubjectId, ct);
        var targetPolicy = await store.GetAsync(action.SubjectId, ct);
        EnsureNotHardDenied(targetPolicy, action.Capability);

        if (!IsAdministrator(caller, callerPolicy))
        {
            EnsureDelegableClearance(callerPolicy, action.Clearance);
            await EnsureDelegationAuthorityAsync(
                caller,
                callerPolicy,
                action.Capability,
                action.Scope,
                ct);
        }

        await store.GrantAsync(caller, action with
        {
            Capability = NormalizeCapability(action.Capability),
            Scope = NormalizeScope(action.Scope),
        }, ct);
    }

    public async Task RevokeAsync(
        RequestPrincipal caller,
        PermissionRevokeAction action,
        CancellationToken ct = default)
    {
        RequireAuthenticated(caller);
        if (string.IsNullOrWhiteSpace(action.SubjectId)
            || string.IsNullOrWhiteSpace(action.Capability))
            throw new ArgumentException("A revocation requires a subject and capability.");

        var callerPolicy = await store.GetAsync(caller.SubjectId, ct);
        if (!IsAdministrator(caller, callerPolicy))
        {
            await EnsureDelegationAuthorityAsync(
                caller,
                callerPolicy,
                action.Capability,
                action.Scope,
                ct);
        }

        await store.RevokeAsync(
            action.SubjectId,
            NormalizeCapability(action.Capability),
            NormalizeScope(action.Scope),
            ct);
    }

    public async Task ApproveAsync(
        RequestPrincipal caller,
        PermissionApproveAction action,
        CancellationToken ct = default)
    {
        RequireAuthenticated(caller);
        var capability = NormalizeCapability(action.Capability);
        var scope = NormalizeScope(action.Scope);
        var grant = await store.GetGrantAsync(action.SubjectId, capability, scope, ct)
            ?? throw new InvalidOperationException("The permission grant was not found.");
        if (grant.Clearance == PermissionClearance.Independent)
            throw new UnauthorizedAccessException("Independent grants do not require approval.");
        if (!IsApprovalClearance(grant.Clearance))
            throw new UnauthorizedAccessException("This clearance cannot be approved.");
        if (grant.GrantedBy.Equals(caller.SubjectId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("A grant cannot approve itself.");

        var callerPolicy = await store.GetAsync(caller.SubjectId, ct);
        var targetPolicy = await store.GetAsync(action.SubjectId, ct);
        if (!IsAdministrator(caller, callerPolicy))
        {
            EnsureApprovalRoute(grant.Clearance, caller, callerPolicy, targetPolicy);
        }

        await store.ApproveAsync(caller, action with { Capability = capability, Scope = scope }, ct);
    }

    private async Task EnsureDelegationAuthorityAsync(
        RequestPrincipal caller,
        PermissionPolicyRecord? callerPolicy,
        string capability,
        string scope,
        CancellationToken ct)
    {
        if (callerPolicy is null
            || callerPolicy.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow
            || !IsUsableClearance(callerPolicy.Clearance)
            || !HasCapability(callerPolicy, ManagePermissionsCapability))
        {
            throw new UnauthorizedAccessException(
                "The caller lacks permission-management authority.");
        }

        if (!HasCapability(callerPolicy, capability)
            && await FindUsableGrantAsync(
                caller.SubjectId,
                capability,
                NormalizeScope(scope),
                null,
                ct) is null)
        {
            throw new UnauthorizedAccessException(
                "The caller cannot delegate a capability that the caller does not hold.");
        }
    }

    private async Task<PermissionGrantRecord?> FindUsableGrantAsync(
        string subjectId,
        string capability,
        string requestedScope,
        Guid? targetId,
        CancellationToken ct)
    {
        var capabilities = IsCrossThreadCapability(capability)
            ? new[] { capability, "CanReadCrossThreadHistory" }
            : new[] { capability };
        foreach (var capabilityName in capabilities.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var grants = await store.ListGrantsAsync(subjectId, capabilityName, ct);
            var usable = await SelectUsableGrantAsync(grants, requestedScope, targetId, ct);
            if (usable is not null)
                return usable;
        }

        return null;
    }

    private async Task<PermissionGrantRecord?> SelectUsableGrantAsync(
        IEnumerable<PermissionGrantRecord> grants,
        string requestedScope,
        Guid? targetId,
        CancellationToken ct)
    {
        foreach (var grant in grants.OrderByDescending(item => ScopeRank(item.Scope)))
        {
            if (grant.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
                continue;
            if (grant.Clearance == PermissionClearance.Restricted)
                continue;
            if (!ScopeMatches(grant.Scope, requestedScope, targetId))
                continue;
            if (grant.Clearance != PermissionClearance.Independent
                && !await store.HasValidApprovalAsync(grant, ct))
                continue;
            return grant;
        }

        return null;
    }

    private static void ValidateGrantAction(PermissionGrantAction action)
    {
        if (string.IsNullOrWhiteSpace(action.SubjectId)
            || string.IsNullOrWhiteSpace(action.Capability))
            throw new ArgumentException("A grant requires a subject and capability.");
    }

    private static void EnsureDelegableClearance(
        PermissionPolicyRecord? callerPolicy,
        PermissionClearance requested)
    {
        if (callerPolicy is null
            || !IsUsableClearance(callerPolicy.Clearance))
        {
            throw new UnauthorizedAccessException("The caller has no usable clearance.");
        }
        if (requested is PermissionClearance.Independent or PermissionClearance.Restricted)
            throw new UnauthorizedAccessException("Only an administrator can grant independent clearance.");
        if (requested > callerPolicy.Clearance)
            throw new UnauthorizedAccessException("The caller cannot grant a higher clearance than the caller holds.");
    }

    private static void EnsureNotHardDenied(
        PermissionPolicyRecord? targetPolicy,
        string capability)
    {
        if (targetPolicy?.HardDeniedCapabilities.Any(item => IsSameCapability(item, capability)) == true)
            throw new UnauthorizedAccessException("A hard denial blocks this capability.");
    }

    private static string RequestedScope(ContextAccessRequest request) =>
        request.ContextId is { } contextId
            ? $"context:{contextId:N}"
            : $"channel:{request.ChannelId:N}";

    private static string NormalizeCapability(string? capability) =>
        string.IsNullOrWhiteSpace(capability)
            ? ContextAccessCapabilities.ReadCrossThreadHistory
            : capability.Trim();

    private static string NormalizeScope(string? scope) =>
        string.IsNullOrWhiteSpace(scope) ? "global" : scope.Trim();

    private static bool HasCapability(PermissionPolicyRecord? policy, string capability) =>
        policy?.Capabilities.Any(item => IsSameCapability(item, capability)) == true;

    private static PermissionClearance EffectiveClearance(
        PermissionClearance policy,
        PermissionClearance? grant)
    {
        if (policy == PermissionClearance.Restricted || grant == PermissionClearance.Restricted)
            return PermissionClearance.Restricted;
        if (grant is not { } value || policy == PermissionClearance.Unset)
            return grant ?? policy;
        return value > policy ? value : policy;
    }

    private static bool IsUsableClearance(PermissionClearance clearance) =>
        clearance is not PermissionClearance.Unset and not PermissionClearance.Restricted;

    private static bool IsApprovalClearance(PermissionClearance clearance) =>
        clearance is PermissionClearance.ApprovedBySameLevelUser
            or PermissionClearance.ApprovedByWhitelistedUser
            or PermissionClearance.ApprovedByPermittedAgent
            or PermissionClearance.ApprovedByWhitelistedAgent;

    private static void EnsureApprovalRoute(
        PermissionClearance clearance,
        RequestPrincipal caller,
        PermissionPolicyRecord? callerPolicy,
        PermissionPolicyRecord? targetPolicy)
    {
        if (callerPolicy is null
            || !IsUsableClearance(callerPolicy.Clearance)
            || !HasCapability(callerPolicy, ApprovePermissionsCapability))
        {
            throw new UnauthorizedAccessException("The caller lacks approval authority.");
        }

        if (callerPolicy.Clearance < clearance)
            throw new UnauthorizedAccessException("The approver does not hold the required clearance.");

        var listed = clearance switch
        {
            PermissionClearance.ApprovedByWhitelistedUser => targetPolicy?.WhitelistedUserIds,
            PermissionClearance.ApprovedByPermittedAgent => targetPolicy?.PermittedAgentIds,
            PermissionClearance.ApprovedByWhitelistedAgent => targetPolicy?.WhitelistedAgentIds,
            PermissionClearance.ApprovedBySameLevelUser => null,
            _ => throw new UnauthorizedAccessException("This clearance cannot be approved."),
        };

        if (listed is not null
            && !listed.Any(subjectId => subjectId.Equals(caller.SubjectId, StringComparison.Ordinal)))
        {
            throw new UnauthorizedAccessException("The caller is not approved for this clearance route.");
        }
    }

    private static bool ScopeMatches(string? grantScope, string requestedScope, Guid? targetId)
    {
        var scope = NormalizeScope(grantScope);
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
        capability.Equals(ContextAccessCapabilities.ReadCrossThreadHistory, StringComparison.OrdinalIgnoreCase)
        || capability.Equals("CanReadCrossThreadHistory", StringComparison.OrdinalIgnoreCase);

    private static bool IsSameCapability(string left, string right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase)
        || IsCrossThreadCapability(left) && IsCrossThreadCapability(right);

    private static bool IsAdministrator(
        RequestPrincipal principal,
        PermissionPolicyRecord? policy) =>
        principal.Roles?.Any(IsAdministratorRole) == true
        || policy?.Roles.Any(IsAdministratorRole) == true;

    private static bool IsAdministratorRole(string role) =>
        role.Equals("admin", StringComparison.OrdinalIgnoreCase)
        || role.Equals("administrator", StringComparison.OrdinalIgnoreCase);

    private static void RequireAuthenticated(RequestPrincipal caller)
    {
        if (!caller.IsAuthenticated)
            throw new UnauthorizedAccessException("Authentication is required.");
    }

    private static Guid ParseAgentId(string subjectId) =>
        Guid.TryParse(subjectId, out var id) ? id : Guid.Empty;
}
