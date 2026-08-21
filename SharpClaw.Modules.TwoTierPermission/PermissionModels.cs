using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.TwoTierPermission;

public sealed record PermissionPolicyRecord(
    string SubjectId,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> HardDeniedCapabilities,
    PermissionClearance Clearance,
    bool RequireSourceOptIn,
    IReadOnlyList<string> DelegatedBy,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset UpdatedAt)
{
    public IReadOnlyList<string> WhitelistedUserIds { get; init; } = [];

    public IReadOnlyList<string> PermittedAgentIds { get; init; } = [];

    public IReadOnlyList<string> WhitelistedAgentIds { get; init; } = [];
}

public sealed record PermissionGrantRecord(
    string GrantId,
    string SubjectId,
    string Capability,
    string Scope,
    PermissionClearance Clearance,
    string GrantedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt)
{
    public bool RequireSourceOptIn { get; init; } = true;
}

public sealed record PermissionApprovalRecord(
    string ApprovalId,
    string SubjectId,
    string Capability,
    string Scope,
    string ApprovedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt);

public sealed record PermissionRoleRecord(
    string RoleId,
    string Name,
    string? Description,
    IReadOnlyList<string> Capabilities,
    PermissionClearance Clearance,
    IReadOnlyList<string> AssignedSubjectIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PermissionSetRecord(
    string PermissionSetId,
    string Name,
    string? Description,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> RoleIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public IReadOnlyList<string> AssignedSubjectIds { get; init; } = [];
}

public sealed record PermissionApiAction(
    string Operation,
    JsonElement Payload);

public sealed record PermissionEvaluateAction(
    string SubjectId,
    string Capability,
    string Scope,
    bool SourceChannelOptedIn);

public sealed record PermissionGrantAction(
    string SubjectId,
    string Capability,
    string Scope,
    PermissionClearance Clearance,
    bool RequireSourceOptIn = true,
    DateTimeOffset? ExpiresAt = null);

public sealed record PermissionRevokeAction(
    string SubjectId,
    string Capability,
    string Scope);

public sealed record PermissionApproveAction(
    string SubjectId,
    string Capability,
    string Scope,
    DateTimeOffset? ExpiresAt = null);

public sealed record PermissionChangedEvent(
    string SubjectId,
    string Capability,
    string Scope,
    string Change,
    DateTimeOffset ChangedAt);

public static class PermissionApiOperations
{
    public const string Evaluate = "permission.evaluate";
    public const string ListPolicies = "policy.list";
    public const string GetPolicy = "policy.get";
    public const string SavePolicy = "policy.save";
    public const string DeletePolicy = "policy.delete";
    public const string Grant = "permission.grant";
    public const string Revoke = "permission.revoke";
    public const string Approve = "permission.approve";
    public const string ListRoles = "role.list";
    public const string GetRole = "role.get";
    public const string SaveRole = "role.save";
    public const string DeleteRole = "role.delete";
    public const string AssignRole = "role.assign";
    public const string ListPermissionSets = "permission-set.list";
    public const string GetPermissionSet = "permission-set.get";
    public const string SavePermissionSet = "permission-set.save";
    public const string DeletePermissionSet = "permission-set.delete";
    public const string AssignPermissionSet = "permission-set.assign";
}
