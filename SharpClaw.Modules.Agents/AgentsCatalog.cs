using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.Agents;

public sealed class AgentsCatalog
{
    public const string ModuleId = AgentsModule.ModuleIdValue;
    public const string AgentsStorage = "agents";
    public const string SkillsStorage = "skills";
    public const string MemoryStorage = "memory";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    private readonly ModuleDocumentStore<AgentRecord> _agents;
    private readonly ModuleDocumentStore<SkillRecord> _skills;
    private readonly ModuleDocumentStore<MemoryRecord> _memory;
    private readonly IAgentAccessPolicy _access;

    public AgentsCatalog(IModuleStorageGateway gateway, IAgentAccessPolicy access)
    {
        _access = access;
        _agents = new(gateway, ModuleId, AgentsStorage, $"{ModuleId}:{AgentsStorage}", JsonOptions);
        _skills = new(gateway, ModuleId, SkillsStorage, $"{ModuleId}:{SkillsStorage}", JsonOptions);
        _memory = new(gateway, ModuleId, MemoryStorage, $"{ModuleId}:{MemoryStorage}", JsonOptions);
    }

    public Task<AgentRecord?> GetAgentAsync(Guid id, CancellationToken ct = default) =>
        _agents.GetAsync(Key(id), ct);

    public Task<SkillRecord?> GetSkillAsync(Guid id, CancellationToken ct = default) =>
        _skills.GetAsync(Key(id), ct);

    public Task<IReadOnlyList<AgentRecord>> ListAgentsAsync(CancellationToken ct = default) =>
        _agents.ListAsync(ct);

    public Task<IReadOnlyList<SkillRecord>> ListSkillsAsync(CancellationToken ct = default) =>
        _skills.ListAsync(ct);

    public async Task<AgentRecord> CreateAgentAsync(
        RequestPrincipal caller,
        AgentsCreateAction action,
        CancellationToken ct = default)
    {
        await RequireAsync(caller, "create_sub_agents", null, ct);
        if (string.IsNullOrWhiteSpace(action.Name) || action.ModelId == Guid.Empty)
            throw new ArgumentException("An agent requires a name and model id.");
        var now = DateTimeOffset.UtcNow;
        var agent = new AgentRecord(
            Guid.NewGuid(), action.Name.Trim(), action.ModelId, action.ProviderKey.Trim(),
            action.ModelName, action.SystemPrompt, caller.SubjectId, now, now);
        await _agents.UpsertAsync(Key(agent.Id), agent, new
        {
            name = agent.Name,
            providerKey = agent.ProviderKey,
            updatedAt = agent.UpdatedAt,
        }, ct);
        return agent;
    }

    public async Task<AgentRecord?> UpdateAgentAsync(
        RequestPrincipal caller,
        AgentsUpdateAction action,
        CancellationToken ct = default)
    {
        var existing = await GetAgentAsync(action.AgentId, ct);
        if (existing is null)
            return null;
        await RequireAsync(caller, "manage_agents", existing.Id, ct);
        var updated = existing with
        {
            Name = string.IsNullOrWhiteSpace(action.Name) ? existing.Name : action.Name.Trim(),
            ModelId = action.ModelId.GetValueOrDefault(existing.ModelId),
            ProviderKey = string.IsNullOrWhiteSpace(action.ProviderKey)
                ? existing.ProviderKey
                : action.ProviderKey.Trim(),
            SystemPrompt = action.SystemPrompt ?? existing.SystemPrompt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await _agents.UpsertAsync(Key(updated.Id), updated, new
        {
            name = updated.Name,
            providerKey = updated.ProviderKey,
            updatedAt = updated.UpdatedAt,
        }, ct);
        return updated;
    }

    public async Task<SkillRecord> SaveSkillAsync(
        RequestPrincipal caller,
        SkillRecord skill,
        CancellationToken ct = default)
    {
        await RequireAsync(caller, "manage_skills", null, ct);
        if (string.IsNullOrWhiteSpace(skill.Name) || string.IsNullOrWhiteSpace(skill.SkillText))
            throw new ArgumentException("A skill requires a name and skill text.");
        var now = DateTimeOffset.UtcNow;
        var stored = skill with
        {
            Id = skill.Id == Guid.Empty ? Guid.NewGuid() : skill.Id,
            CreatedAt = skill.CreatedAt == default ? now : skill.CreatedAt,
            UpdatedAt = now,
        };
        await _skills.UpsertAsync(Key(stored.Id), stored, new
        {
            name = stored.Name,
            updatedAt = stored.UpdatedAt,
        }, ct);
        return stored;
    }

    public async Task<string> AccessSkillAsync(
        RequestPrincipal caller,
        Guid skillId,
        CancellationToken ct = default)
    {
        var skill = await GetSkillAsync(skillId, ct)
            ?? throw new InvalidOperationException($"Skill '{skillId}' was not found.");
        var accessDecision = await _access.EvaluateAgentAsync(caller, "access_skills", null, ct);
        if (!accessDecision.Allowed && !IsAdministrator(caller))
            throw new UnauthorizedAccessException("The caller cannot access this skill.");
        if (skill.AllowedAgentIds.Count > 0
            && !IsAdministrator(caller)
            && (!Guid.TryParse(caller.SubjectId, out var agentId)
                || !skill.AllowedAgentIds.Contains(agentId)))
            throw new UnauthorizedAccessException("The caller cannot access this skill.");
        return $"Skill: {skill.Name}\n\n{skill.SkillText}";
    }

    public async Task<MemoryRecord> WriteMemoryAsync(
        RequestPrincipal caller,
        AgentsWriteMemoryAction action,
        CancellationToken ct = default)
    {
        await RequireAsync(caller, "write_memory", action.AgentId, ct);
        if (string.IsNullOrWhiteSpace(action.Key))
            throw new ArgumentException("Memory requires a key.");
        var key = $"{action.AgentId:N}:{action.Key.Trim()}";
        var existing = await _memory.GetAsync(key, ct);
        var now = DateTimeOffset.UtcNow;
        var memory = existing is null
            ? new MemoryRecord(Guid.NewGuid(), action.AgentId, action.Key.Trim(), action.Content,
                action.Tags, now, now)
            : existing with { Content = action.Content, Tags = action.Tags, UpdatedAt = now };
        await _memory.UpsertAsync(key, memory, new
        {
            agentId = action.AgentId.ToString("N"),
            memoryKey = memory.Key,
            updatedAt = memory.UpdatedAt,
        }, ct);
        return memory;
    }

    public async Task<IReadOnlyList<MemoryRecord>> SearchMemoryAsync(
        RequestPrincipal caller,
        Guid agentId,
        string? query,
        CancellationToken ct = default)
    {
        await RequireAsync(caller, "read_memory", agentId, ct);
        var records = await _memory.Query()
            .WhereIndex("agentId").EqualTo(agentId.ToString("N"))
            .OrderByIndexDescending("updatedAt")
            .ToListAsync(ct);
        return string.IsNullOrWhiteSpace(query)
            ? records
            : records.Where(item => item.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Content.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Tags.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
    }

    private async Task RequireAsync(
        RequestPrincipal caller,
        string capability,
        Guid? targetAgentId,
        CancellationToken ct)
    {
        var decision = await _access.EvaluateAgentAsync(caller, capability, targetAgentId, ct);
        if (!decision.Allowed)
            throw new UnauthorizedAccessException(decision.Message);
    }

    private static bool IsAdministrator(RequestPrincipal caller) =>
        caller.Roles?.Any(role => role.Equals("admin", StringComparison.OrdinalIgnoreCase)
            || role.Equals("administrator", StringComparison.OrdinalIgnoreCase)) == true;

    private static string Key(Guid id) => id.ToString("N");
}
