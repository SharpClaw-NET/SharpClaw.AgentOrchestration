using System.Text.Json;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Modules.Agents;

public sealed record AgentRecord(
    Guid Id,
    string Name,
    Guid ModelId,
    string ProviderKey,
    string? ModelName,
    string? SystemPrompt,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public IReadOnlyList<string> Roles { get; init; } = [];
}

public sealed record SkillRecord(
    Guid Id,
    string Name,
    string? Description,
    string SkillText,
    IReadOnlyList<Guid> AllowedAgentIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MemoryRecord(
    Guid Id,
    Guid AgentId,
    string Key,
    string Content,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AgentCostRecord(
    Guid AgentId,
    decimal TotalCost,
    long InputTokens,
    long OutputTokens,
    DateTimeOffset UpdatedAt);

public sealed record AgentSynchronizationRecord(
    Guid AgentId,
    string Status,
    DateTimeOffset SynchronizedAt,
    string? ProviderKey = null);

public sealed record AgentsApiAction(
    string Operation,
    JsonElement Payload);

public sealed record AgentsCreateAction(
    string Name,
    Guid ModelId,
    string ProviderKey,
    string? ModelName,
    string? SystemPrompt);

public sealed record AgentsUpdateAction(
    Guid AgentId,
    string? Name,
    Guid? ModelId,
    string? ProviderKey,
    string? SystemPrompt);

public sealed record AgentsWriteMemoryAction(
    Guid AgentId,
    string Key,
    string Content,
    IReadOnlyList<string> Tags);

public sealed record AgentsSaveSkillAction(SkillRecord Skill);

public sealed record AgentsAccessSkillAction(Guid SkillId);

public sealed record AgentsSearchMemoryAction(Guid AgentId, string? Query);

public sealed record AgentChangedEvent(
    Guid AgentId,
    string Change,
    DateTimeOffset ChangedAt);

public sealed record SkillChangedEvent(
    Guid SkillId,
    string Change,
    DateTimeOffset ChangedAt);

public sealed record MemoryChangedEvent(
    Guid AgentId,
    string Key,
    DateTimeOffset ChangedAt);

public static class AgentsApiOperations
{
    public const string ImportAgentJobs = AgentsModule.ImportAgentJobsAction;
    public const string ListAgents = "agent.list";
    public const string GetAgent = "agent.get";
    public const string CreateAgent = "agent.create";
    public const string UpdateAgent = "agent.update";
    public const string DeleteAgent = "agent.delete";
    public const string AssignRole = "agent.role.assign";
    public const string SynchronizeAgent = "agent.synchronize";
    public const string GetCost = "agent.cost";
    public const string ListSkills = "skill.list";
    public const string GetSkill = "skill.get";
    public const string SaveSkill = "skill.save";
    public const string DeleteSkill = "skill.delete";
    public const string AccessSkill = "skill.access";
    public const string WriteMemory = "memory.write";
    public const string SearchMemory = "memory.search";
}
