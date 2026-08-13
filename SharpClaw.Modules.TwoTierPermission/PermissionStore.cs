using System.Text.Json;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.TwoTierPermission;

public sealed class PermissionPolicyStore
{
    public const string ModuleId = TwoTierPermissionModule.ModuleIdValue;
    public const string PoliciesStorage = "policies";
    public const string GrantsStorage = "grants";
    public const string ApprovalsStorage = "approvals";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    private readonly ModuleDocumentStore<PermissionPolicyRecord> _policies;
    private readonly ModuleDocumentStore<PermissionGrantRecord> _grants;
    private readonly ModuleDocumentStore<PermissionApprovalRecord> _approvals;

    public PermissionPolicyStore(IModuleStorageGateway gateway)
    {
        _policies = new(gateway, ModuleId, PoliciesStorage, $"{ModuleId}:{PoliciesStorage}", JsonOptions);
        _grants = new(gateway, ModuleId, GrantsStorage, $"{ModuleId}:{GrantsStorage}", JsonOptions);
        _approvals = new(gateway, ModuleId, ApprovalsStorage, $"{ModuleId}:{ApprovalsStorage}", JsonOptions);
    }

    public Task<PermissionPolicyRecord?> GetAsync(
        string subjectId,
        CancellationToken ct = default) =>
        _policies.GetAsync(Key(subjectId), ct);

    public Task<IReadOnlyList<PermissionPolicyRecord>> ListAsync(
        CancellationToken ct = default) =>
        _policies.ListAsync(ct);

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
