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
            .WhereIndex("capability").EqualTo(capability)
            .ToListAsync(ct);

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
        var existing = await GetAsync(grant.SubjectId, ct);
        var capabilities = (existing?.Capabilities ?? [])
            .Append(grant.Capability)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var delegated = (existing?.DelegatedBy ?? [])
            .Append(caller.SubjectId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        await SaveAsync(new PermissionPolicyRecord(
            grant.SubjectId,
            existing?.Roles ?? [],
            capabilities,
            existing?.HardDeniedCapabilities ?? [],
            grant.Clearance,
            grant.RequireSourceOptIn,
            delegated,
            existing?.ExpiresAt,
            DateTimeOffset.UtcNow), ct);

        var record = new PermissionGrantRecord(
            $"{grant.SubjectId}:{grant.Capability}:{grant.Scope}",
            grant.SubjectId,
            grant.Capability,
            grant.Scope,
            grant.Clearance,
            caller.SubjectId,
            DateTimeOffset.UtcNow,
            null);
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
        var existing = await GetAsync(subjectId, ct);
        if (existing is not null)
        {
            var capabilities = existing.Capabilities
                .Where(item => !item.Equals(capability, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            await SaveAsync(existing with { Capabilities = capabilities, UpdatedAt = DateTimeOffset.UtcNow }, ct);
        }

        await _grants.DeleteAsync($"{subjectId}:{capability}:{scope}", ct);
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

    private static string Key(string subjectId) => subjectId.Trim();
}
