using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.TwoTierPermission;

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
