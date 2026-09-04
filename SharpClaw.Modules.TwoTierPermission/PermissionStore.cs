using System.Text.Json;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.TwoTierPermission;

public sealed class PermissionPolicyStore
{
    public const string SourceId = TwoTierPermissionModule.ModuleIdValue;
    public const string PoliciesStorage = "policies";
    public const string GrantsStorage = "grants";
    public const string ApprovalsStorage = "approvals";
    public const string RolesStorage = "roles";
    public const string PermissionSetsStorage = "permission_sets";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    private readonly ScopedDocumentStore<PermissionPolicyRecord> _policies;
    private readonly ScopedDocumentStore<PermissionGrantRecord> _grants;
    private readonly ScopedDocumentStore<PermissionApprovalRecord> _approvals;
    private readonly ScopedDocumentStore<PermissionRoleRecord> _roles;
    private readonly ScopedDocumentStore<PermissionSetRecord> _permissionSets;

    public PermissionPolicyStore(IScopedStorageGateway gateway)
    {
        _policies = new(gateway, SourceId, PoliciesStorage, $"{SourceId}:{PoliciesStorage}", JsonOptions);
        _grants = new(gateway, SourceId, GrantsStorage, $"{SourceId}:{GrantsStorage}", JsonOptions);
        _approvals = new(gateway, SourceId, ApprovalsStorage, $"{SourceId}:{ApprovalsStorage}", JsonOptions);
        _roles = new(gateway, SourceId, RolesStorage, $"{SourceId}:{RolesStorage}", JsonOptions);
        _permissionSets = new(gateway, SourceId, PermissionSetsStorage, $"{SourceId}:{PermissionSetsStorage}", JsonOptions);
    }

    public async Task<PermissionPolicyRecord?> GetAsync(
        string subjectId,
        CancellationToken ct = default)
    {
        var policy = await _policies.GetAsync(Key(subjectId), ct);
        var roles = await _roles.ListAsync(ct);
        var assignedRoles = roles
            .Where(role => role.AssignedSubjectIds.Any(item => item.Equals(subjectId, StringComparison.Ordinal)))
            .ToArray();
        var sets = await _permissionSets.ListAsync(ct);
        var assignedSets = sets
            .Where(set => set.AssignedSubjectIds.Any(item => item.Equals(subjectId, StringComparison.Ordinal)))
            .ToArray();
        var setRoleIds = assignedSets.SelectMany(set => set.RoleIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var setRoles = roles.Where(role => setRoleIds.Contains(role.RoleId)).ToArray();
        var allRoles = assignedRoles.Concat(setRoles).ToArray();
        if (policy is null && allRoles.Length == 0 && assignedSets.Length == 0)
            return null;

        var capabilities = (policy?.Capabilities ?? [])
            .Concat(allRoles.SelectMany(role => role.Capabilities))
            .Concat(assignedSets.SelectMany(set => set.Capabilities))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var roleNames = (policy?.Roles ?? [])
            .Concat(allRoles.Select(role => role.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var clearance = allRoles
            .Select(role => role.Clearance)
            .Append(policy?.Clearance ?? PermissionClearance.Unset)
            .Max();
        return (policy ?? new PermissionPolicyRecord(
            subjectId,
            [],
            [],
            [],
            PermissionClearance.Unset,
            true,
            [],
            null,
            DateTimeOffset.UtcNow)) with
        {
            Roles = roleNames,
            Capabilities = capabilities,
            Clearance = clearance,
        };
    }

    public Task<IReadOnlyList<PermissionPolicyRecord>> ListAsync(
        CancellationToken ct = default) =>
        _policies.ListAsync(ct);

    public Task<IReadOnlyList<PermissionRoleRecord>> ListRolesAsync(
        CancellationToken ct = default) =>
        _roles.ListAsync(ct);

    public Task<PermissionRoleRecord?> GetRoleAsync(
        string roleId,
        CancellationToken ct = default) =>
        _roles.GetAsync(roleId.Trim(), ct);

    public Task SaveRoleAsync(
        PermissionRoleRecord role,
        CancellationToken ct = default) =>
        _roles.UpsertAsync(role.RoleId.Trim(), role, new
        {
            name = role.Name,
            clearance = role.Clearance.ToString(),
            updatedAt = role.UpdatedAt,
        }, ct);

    public Task DeleteRoleAsync(
        string roleId,
        CancellationToken ct = default) =>
        _roles.DeleteAsync(roleId.Trim(), ct);

    public Task<IReadOnlyList<PermissionSetRecord>> ListPermissionSetsAsync(
        CancellationToken ct = default) =>
        _permissionSets.ListAsync(ct);

    public Task<PermissionSetRecord?> GetPermissionSetAsync(
        string permissionSetId,
        CancellationToken ct = default) =>
        _permissionSets.GetAsync(permissionSetId.Trim(), ct);

    public Task SavePermissionSetAsync(
        PermissionSetRecord permissionSet,
        CancellationToken ct = default) =>
        _permissionSets.UpsertAsync(permissionSet.PermissionSetId.Trim(), permissionSet, new
        {
            name = permissionSet.Name,
            updatedAt = permissionSet.UpdatedAt,
        }, ct);

    public Task DeletePermissionSetAsync(
        string permissionSetId,
        CancellationToken ct = default) =>
        _permissionSets.DeleteAsync(permissionSetId.Trim(), ct);

    public async Task<PermissionRoleRecord> AssignRoleAsync(
        string roleId,
        string subjectId,
        bool assign,
        CancellationToken ct = default)
    {
        var role = await GetRoleAsync(roleId, ct)
            ?? throw new InvalidOperationException("The permission role was not found.");
        var subjects = role.AssignedSubjectIds
            .Where(item => !item.Equals(subjectId, StringComparison.Ordinal))
            .Concat(assign ? [subjectId] : [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var updated = role with { AssignedSubjectIds = subjects, UpdatedAt = DateTimeOffset.UtcNow };
        await SaveRoleAsync(updated, ct);
        return updated;
    }

    public async Task<PermissionSetRecord> AssignPermissionSetAsync(
        string permissionSetId,
        string subjectId,
        bool assign,
        CancellationToken ct = default)
    {
        var permissionSet = await GetPermissionSetAsync(permissionSetId, ct)
            ?? throw new InvalidOperationException("The permission set was not found.");
        var subjects = permissionSet.AssignedSubjectIds
            .Where(item => !item.Equals(subjectId, StringComparison.Ordinal))
            .Concat(assign ? [subjectId] : [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var updated = permissionSet with { AssignedSubjectIds = subjects, UpdatedAt = DateTimeOffset.UtcNow };
        await SavePermissionSetAsync(updated, ct);
        return updated;
    }

    public Task<IReadOnlyList<PermissionGrantRecord>> ListGrantsAsync(
        string subjectId,
        string capability,
        CancellationToken ct = default) =>
        _grants.Query()
            .WhereIndex("subjectId").EqualTo(subjectId)
            .WhereIndex("capability").EqualTo(capability.Trim())
            .ToListAsync(ct);

    public async Task<PermissionGrantRecord?> GetGrantAsync(
        string subjectId,
        string capability,
        string scope,
        CancellationToken ct = default)
    {
        var grants = await ListGrantsAsync(subjectId, capability.Trim(), ct);
        var normalizedScope = NormalizeScope(scope);
        return grants.FirstOrDefault(grant =>
            grant.Scope.Equals(normalizedScope, StringComparison.OrdinalIgnoreCase));
    }

    public Task<IReadOnlyList<PermissionApprovalRecord>> ListApprovalsAsync(
        string subjectId,
        string capability,
        CancellationToken ct = default) =>
        _approvals.Query()
            .WhereIndex("subjectId").EqualTo(subjectId)
            .WhereIndex("capability").EqualTo(capability)
            .ToListAsync(ct);

    public Task SaveAsync(
        PermissionPolicyRecord policy,
        CancellationToken ct = default) =>
        _policies.UpsertAsync(Key(policy.SubjectId), policy, new
        {
            subjectId = policy.SubjectId,
            clearance = policy.Clearance.ToString(),
            updatedAt = policy.UpdatedAt,
        }, ct);

    public Task DeleteAsync(
        string subjectId,
        CancellationToken ct = default) =>
        _policies.DeleteAsync(Key(subjectId), ct);

    public async Task GrantAsync(
        RequestPrincipal caller,
        PermissionGrantAction grant,
        CancellationToken ct = default)
    {
        var scope = NormalizeScope(grant.Scope);
        var record = new PermissionGrantRecord(
            $"{grant.SubjectId}:{grant.Capability.Trim()}:{scope}",
            grant.SubjectId,
            grant.Capability.Trim(),
            scope,
            grant.Clearance,
            caller.SubjectId,
            DateTimeOffset.UtcNow,
            grant.ExpiresAt)
        {
            RequireSourceOptIn = grant.RequireSourceOptIn,
        };
        await _grants.UpsertAsync(record.GrantId, record, new
        {
            subjectId = record.SubjectId,
            capability = record.Capability,
            scope = record.Scope,
        }, ct);
    }

    public async Task RevokeAsync(
        string subjectId,
        string capability,
        string scope,
        CancellationToken ct = default)
    {
        var normalizedScope = NormalizeScope(scope);
        await _grants.DeleteAsync($"{subjectId}:{capability.Trim()}:{normalizedScope}", ct);
    }

    public Task SaveApprovalAsync(
        PermissionApprovalRecord approval,
        CancellationToken ct = default) =>
        _approvals.UpsertAsync(approval.ApprovalId, approval, new
        {
            subjectId = approval.SubjectId,
            capability = approval.Capability,
            scope = approval.Scope,
        }, ct);

    public async Task ApproveAsync(
        RequestPrincipal caller,
        PermissionApproveAction action,
        CancellationToken ct = default)
    {
        if (!caller.IsAuthenticated)
            throw new UnauthorizedAccessException("Authentication is required.");
        if (string.IsNullOrWhiteSpace(action.SubjectId)
            || string.IsNullOrWhiteSpace(action.Capability))
            throw new ArgumentException("An approval requires a subject and capability.");
        var approval = new PermissionApprovalRecord(
            $"{action.SubjectId}:{action.Capability}:{action.Scope}:{caller.SubjectId}",
            action.SubjectId,
            action.Capability,
            action.Scope,
            caller.SubjectId,
            DateTimeOffset.UtcNow,
            action.ExpiresAt);
        await SaveApprovalAsync(approval, ct);
    }

    public async Task<bool> HasValidApprovalAsync(
        PermissionGrantRecord grant,
        CancellationToken ct = default)
    {
        var approvals = await ListApprovalsAsync(grant.SubjectId, grant.Capability, ct);
        return approvals.Any(approval =>
            approval.Scope.Equals(grant.Scope, StringComparison.OrdinalIgnoreCase)
            && (approval.ExpiresAt is null || approval.ExpiresAt > DateTimeOffset.UtcNow)
            && !approval.ApprovedBy.Equals(grant.GrantedBy, StringComparison.Ordinal));
    }

    private static string Key(string subjectId) => subjectId.Trim();

    private static string NormalizeScope(string? scope) =>
        string.IsNullOrWhiteSpace(scope) ? "global" : scope.Trim();
}
