using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.Agents;

/// <summary>Module-owned Agent job definition and domain state.</summary>
public sealed record AgentJob(
    Guid Id,
    Guid AgentId,
    string CallerIdentity,
    string ActionIdentity,
    string Resource,
    string ScriptJson,
    string PayloadJson,
    string WorkingDirectory,
    string Status,
    string Clearance,
    long InputTokens,
    long OutputTokens,
    IReadOnlyList<string> ApprovalIdentities,
    Guid? ChannelId,
    Guid? ContextId,
    string PermissionIdentity,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    Guid? CanonicalJobId,
    string? ResultJson,
    string? Error,
    string ResultAuthority = "sharpclaw.jobs")
{
    public const string CanonicalResultAuthority = "sharpclaw.jobs";
}

public sealed record AgentsRecordJobAction(
    AgentJob Job,
    RequestPrincipal Caller);

public sealed record AgentsAttachCanonicalJobAction(
    Guid AgentJobId,
    Guid CanonicalJobId,
    RequestPrincipal Caller);

public sealed record AgentsCompleteJobAction(
    Guid AgentJobId,
    Guid CanonicalJobId,
    string Status,
    string? ResultJson,
    string? Error,
    long InputTokens,
    long OutputTokens,
    DateTimeOffset CompletedAt,
    RequestPrincipal Caller);

public interface IAgentsJobActionExecutor
{
    Task<AgentJob> RecordAsync(
        AgentsRecordJobAction action,
        CancellationToken ct = default);

    Task<AgentJob> AttachCanonicalJobAsync(
        AgentsAttachCanonicalJobAction action,
        CancellationToken ct = default);

    Task<AgentJob> CompleteAsync(
        AgentsCompleteJobAction action,
        CancellationToken ct = default);
}

public sealed class AgentsJobActionExecutor(
    AgentsCatalog catalog) : IAgentsJobActionExecutor
{
    public Task<AgentJob> RecordAsync(
        AgentsRecordJobAction action,
        CancellationToken ct = default) =>
        catalog.RecordAgentJobAsync(action.Caller, action.Job, ct);

    public Task<AgentJob> AttachCanonicalJobAsync(
        AgentsAttachCanonicalJobAction action,
        CancellationToken ct = default) =>
        catalog.AttachCanonicalJobAsync(action.Caller, action.AgentJobId, action.CanonicalJobId, ct);

    public Task<AgentJob> CompleteAsync(
        AgentsCompleteJobAction action,
        CancellationToken ct = default) =>
        catalog.ProjectCanonicalCompletionAsync(
            action.Caller,
            action.AgentJobId,
            action.CanonicalJobId,
            action.Status,
            action.ResultJson,
            action.Error,
            action.InputTokens,
            action.OutputTokens,
            action.CompletedAt,
            ct);
}
