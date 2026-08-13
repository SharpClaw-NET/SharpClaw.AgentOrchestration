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
    DateTimeOffset UpdatedAt);

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
