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
    public const string CostsStorage = "costs";
    public const string SynchronizationStorage = "synchronization";
    public const string AgentJobsStorage = "agent_jobs";
    public const string AgentJobImportsStorage = "agent_job_imports";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private static readonly SemaphoreSlim AgentJobImportGate = new(1, 1);

    private readonly ModuleDocumentStore<AgentRecord> _agents;
    private readonly ModuleDocumentStore<SkillRecord> _skills;
    private readonly ModuleDocumentStore<MemoryRecord> _memory;
    private readonly ModuleDocumentStore<AgentCostRecord> _costs;
    private readonly ModuleDocumentStore<AgentSynchronizationRecord> _synchronization;
    private readonly ModuleDocumentStore<AgentJob> _agentJobs;
    private readonly ModuleDocumentStore<AgentJobImportState> _agentJobImports;
    private readonly IAgentAccessPolicy _access;

    public AgentsCatalog(IModuleStorageGateway gateway, IAgentAccessPolicy access)
    {
        _access = access;
        _agents = new(gateway, ModuleId, AgentsStorage, $"{ModuleId}:{AgentsStorage}", JsonOptions);
        _skills = new(gateway, ModuleId, SkillsStorage, $"{ModuleId}:{SkillsStorage}", JsonOptions);
        _memory = new(gateway, ModuleId, MemoryStorage, $"{ModuleId}:{MemoryStorage}", JsonOptions);
        _costs = new(gateway, ModuleId, CostsStorage, $"{ModuleId}:{CostsStorage}", JsonOptions);
        _synchronization = new(gateway, ModuleId, SynchronizationStorage, $"{ModuleId}:{SynchronizationStorage}", JsonOptions);
        _agentJobs = new(gateway, ModuleId, AgentJobsStorage, $"{ModuleId}:{AgentJobsStorage}", JsonOptions);
        _agentJobImports = new(gateway, ModuleId, AgentJobImportsStorage, $"{ModuleId}:{AgentJobImportsStorage}", JsonOptions);
    }

    public Task<AgentRecord?> GetAgentAsync(Guid id, CancellationToken ct = default) =>
        _agents.GetAsync(Key(id), ct);

    public Task<SkillRecord?> GetSkillAsync(Guid id, CancellationToken ct = default) =>
        _skills.GetAsync(Key(id), ct);

    public Task<IReadOnlyList<AgentRecord>> ListAgentsAsync(CancellationToken ct = default) =>
        _agents.ListAsync(ct);

    public Task<IReadOnlyList<SkillRecord>> ListSkillsAsync(CancellationToken ct = default) =>
        _skills.ListAsync(ct);

    public Task<AgentJob?> GetAgentJobAsync(Guid id, CancellationToken ct = default) =>
        _agentJobs.GetAsync(Key(id), ct);

    public Task<IReadOnlyList<AgentJob>> ListAgentJobsAsync(CancellationToken ct = default) =>
        _agentJobs.ListAsync(ct);

    public Task<AgentJobImportState?> GetAgentJobImportStateAsync(
        string snapshotId,
        CancellationToken ct = default) =>
        _agentJobImports.GetAsync(AgentsJobImportIntegrity.ImportKey(snapshotId), ct);

    public async Task<IReadOnlyList<AgentJob>> ImportAgentJobsAsync(
        RequestPrincipal caller,
        CanonicalJobsImportSnapshot snapshot,
        CancellationToken ct = default)
    {
        await RequireAsync(caller, "manage_agent_jobs", null, ct);
        var plan = AgentsJobImportConverter.Prepare(snapshot);
        await AgentJobImportGate.WaitAsync(ct);
        try
        {
            var importKey = AgentsJobImportIntegrity.ImportKey(snapshot.SnapshotId);
            var state = await _agentJobImports.GetAsync(importKey, ct);
            if (state is not null)
                EnsureImportManifest(snapshot, state);
            else
            {
                state = new AgentJobImportState(
                    snapshot.SnapshotId,
                    snapshot.CapturedAt,
                    snapshot.ExpectedRecordCount,
                    snapshot.OrderedSourceIds.ToArray(),
                    snapshot.SourceHashes.ToArray(),
                    snapshot.AggregateHash,
                    0,
                    false);
                await _agentJobImports.UpsertAsync(
                    importKey,
                    state,
                    AgentJobImportStateIndexes(state),
                    ct);
            }

            if (state.Completed)
                await VerifyImportedJobsAsync(plan.Jobs, ct);
            else
            {
                foreach (var job in plan.Jobs)
                    await EnsureImportedJobAsync(job, ct);

                await VerifyImportedJobsAsync(plan.Jobs, ct);
                var completed = state with
                {
                    ImportedRecordCount = plan.Jobs.Count,
                    Completed = true,
                };
                await _agentJobImports.UpsertAsync(
                    importKey,
                    completed,
                    AgentJobImportStateIndexes(completed),
                    ct);
                state = await _agentJobImports.GetAsync(importKey, ct)
                    ?? throw new AgentJobImportException("The import completion marker was not persisted.");
                EnsureImportManifest(snapshot, state);
                if (!state.Completed || state.ImportedRecordCount != plan.Jobs.Count)
                    throw new AgentJobImportException("The import completion marker is incomplete.");
            }

            return plan.Jobs;
        }
        finally
        {
            AgentJobImportGate.Release();
        }
    }

    public async Task<AgentJob> RecordAgentJobAsync(
        RequestPrincipal caller,
        AgentJob job,
        CancellationToken ct = default)
    {
        await RequireAsync(caller, "manage_agent_jobs", job.AgentId, ct);
        ValidateAgentJob(job);
        var now = DateTimeOffset.UtcNow;
        var stored = job with
        {
            Id = job.Id == Guid.Empty ? Guid.NewGuid() : job.Id,
            CreatedAt = job.CreatedAt == default ? now : job.CreatedAt,
            UpdatedAt = now,
            ResultAuthority = string.IsNullOrWhiteSpace(job.ResultAuthority)
                ? AgentJob.CanonicalResultAuthority
                : job.ResultAuthority,
        };
        await _agentJobs.UpsertAsync(Key(stored.Id), stored, AgentJobIndexes(stored), ct);
        return stored;
    }

    public async Task<AgentJob> AttachCanonicalJobAsync(
        RequestPrincipal caller,
        Guid agentJobId,
        Guid canonicalJobId,
        CancellationToken ct = default)
    {
        if (canonicalJobId == Guid.Empty)
            throw new ArgumentException("A canonical job id is required.", nameof(canonicalJobId));
        var current = await GetAgentJobAsync(agentJobId, ct)
            ?? throw new InvalidOperationException("The Agent job was not found.");
        await RequireAsync(caller, "manage_agent_jobs", current.AgentId, ct);
        if (current.CanonicalJobId is { } existing && existing != canonicalJobId)
            throw new InvalidOperationException("The Agent job already references a different canonical job.");
        var updated = current with
        {
            CanonicalJobId = canonicalJobId,
            UpdatedAt = DateTimeOffset.UtcNow,
            ResultAuthority = AgentJob.CanonicalResultAuthority,
        };
        await _agentJobs.UpsertAsync(Key(updated.Id), updated, AgentJobIndexes(updated), ct);
        return updated;
    }

    public async Task<AgentJob> ProjectCanonicalCompletionAsync(
        RequestPrincipal caller,
        Guid agentJobId,
        Guid canonicalJobId,
        string status,
        string? resultJson,
        string? error,
        long inputTokens,
        long outputTokens,
        DateTimeOffset completedAt,
        CancellationToken ct = default)
    {
        if (canonicalJobId == Guid.Empty)
            throw new ArgumentException("A canonical job id is required.", nameof(canonicalJobId));
        if (string.IsNullOrWhiteSpace(status))
            throw new ArgumentException("A canonical job status is required.", nameof(status));
        if (inputTokens < 0 || outputTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(inputTokens), "Token counts cannot be negative.");
        var current = await GetAgentJobAsync(agentJobId, ct)
            ?? throw new InvalidOperationException("The Agent job was not found.");
        await RequireAsync(caller, "manage_agent_jobs", current.AgentId, ct);
        if (current.CanonicalJobId != canonicalJobId)
            throw new InvalidOperationException("The completion does not match the stored canonical job.");
        var updated = current with
        {
            Status = status.Trim(),
            ResultJson = resultJson,
            Error = error,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CompletedAt = completedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            ResultAuthority = AgentJob.CanonicalResultAuthority,
        };
        await _agentJobs.UpsertAsync(Key(updated.Id), updated, AgentJobIndexes(updated), ct);
        return updated;
    }

    public async Task<AgentRecord> DeleteAgentAsync(
        RequestPrincipal caller,
        Guid agentId,
        CancellationToken ct = default)
    {
        var agent = await GetAgentAsync(agentId, ct)
            ?? throw new InvalidOperationException("The agent was not found.");
        await RequireAsync(caller, "manage_agents", agentId, ct);
        await _agents.DeleteAsync(Key(agentId), ct);
        return agent;
    }

    public async Task<AgentRecord> AssignRoleAsync(
        RequestPrincipal caller,
        Guid agentId,
        string role,
        bool assign,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(role))
            throw new ArgumentException("A role is required.", nameof(role));
        var agent = await GetAgentAsync(agentId, ct)
            ?? throw new InvalidOperationException("The agent was not found.");
        await RequireAsync(caller, "manage_agents", agentId, ct);
        var roles = agent.Roles
            .Where(item => !item.Equals(role, StringComparison.OrdinalIgnoreCase))
            .Concat(assign ? [role.Trim()] : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var updated = agent with { Roles = roles, UpdatedAt = DateTimeOffset.UtcNow };
        await _agents.UpsertAsync(Key(updated.Id), updated, new
        {
            name = updated.Name,
            providerKey = updated.ProviderKey,
            updatedAt = updated.UpdatedAt,
        }, ct);
        return updated;
    }

    public async Task<AgentSynchronizationRecord> SynchronizeAsync(
        RequestPrincipal caller,
        Guid agentId,
        CancellationToken ct = default)
    {
        var agent = await GetAgentAsync(agentId, ct)
            ?? throw new InvalidOperationException("The agent was not found.");
        await RequireAsync(caller, "manage_agents", agentId, ct);
        var record = new AgentSynchronizationRecord(
            agentId,
            "synchronized",
            DateTimeOffset.UtcNow,
            agent.ProviderKey);
        await _synchronization.UpsertAsync(Key(agentId), record, new
        {
            agentId = agentId.ToString("N"),
            updatedAt = record.SynchronizedAt,
        }, ct);
        return record;
    }

    public async Task<AgentCostRecord> GetCostAsync(
        RequestPrincipal caller,
        Guid agentId,
        CancellationToken ct = default)
    {
        var agent = await GetAgentAsync(agentId, ct)
            ?? throw new InvalidOperationException("The agent was not found.");
        var access = await _access.EvaluateAgentAsync(caller, "read_agent_cost", agentId, ct);
        if (!access.Allowed && !IsAdministrator(caller))
            throw new UnauthorizedAccessException("The caller cannot read agent cost data.");
        return await _costs.GetAsync(Key(agent.Id), ct)
            ?? new AgentCostRecord(agent.Id, 0m, 0, 0, agent.UpdatedAt);
    }

    public async Task<SkillRecord> DeleteSkillAsync(
        RequestPrincipal caller,
        Guid skillId,
        CancellationToken ct = default)
    {
        var skill = await GetSkillAsync(skillId, ct)
            ?? throw new InvalidOperationException("The skill was not found.");
        await RequireAsync(caller, "manage_skills", null, ct);
        await _skills.DeleteAsync(Key(skillId), ct);
        return skill;
    }

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
        var skill = await GetSkillForCallerAsync(caller, skillId, ct);
        return $"Skill: {skill.Name}\n\n{skill.SkillText}";
    }

    public async Task<SkillRecord> GetSkillForCallerAsync(
        RequestPrincipal caller,
        Guid skillId,
        CancellationToken ct = default)
    {
        var skill = await GetSkillAsync(skillId, ct)
            ?? throw new InvalidOperationException($"Skill '{skillId}' was not found.");
        await RequireSkillAccessAsync(caller, skill, ct);
        return skill;
    }

    private async Task RequireSkillAccessAsync(
        RequestPrincipal caller,
        SkillRecord skill,
        CancellationToken ct)
    {
        var accessDecision = await _access.EvaluateAgentAsync(caller, "access_skills", null, ct);
        if (!accessDecision.Allowed && !IsAdministrator(caller))
            throw new UnauthorizedAccessException("The caller cannot access this skill.");
        if (skill.AllowedAgentIds.Count > 0
            && !IsAdministrator(caller)
            && (!Guid.TryParse(caller.SubjectId, out var agentId)
                || !skill.AllowedAgentIds.Contains(agentId)))
            throw new UnauthorizedAccessException("The caller cannot access this skill.");
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

    private static void EnsureImportManifest(
        CanonicalJobsImportSnapshot snapshot,
        AgentJobImportState state)
    {
        if (!AgentsJobImportIntegrity.ManifestMatches(snapshot, state))
            throw new AgentJobImportException(
                $"The import snapshot '{snapshot.SnapshotId}' conflicts with its stored manifest.");
        if (state.ImportedRecordCount < 0
            || state.ImportedRecordCount > state.ExpectedRecordCount
            || (state.Completed && state.ImportedRecordCount != state.ExpectedRecordCount))
            throw new AgentJobImportException(
                $"The import snapshot '{snapshot.SnapshotId}' has invalid completion state.");
    }

    private async Task EnsureImportedJobAsync(
        AgentJob job,
        CancellationToken ct)
    {
        ValidateAgentJob(job);
        var existing = await _agentJobs.GetAsync(Key(job.Id), ct);
        if (existing is not null)
        {
            if (!AgentsJobImportIntegrity.AreEquivalent(job, existing))
                throw new AgentJobImportException(
                    $"Source identity '{job.Id}' already contains different Agent job data.");
            return;
        }

        await _agentJobs.UpsertAsync(Key(job.Id), job, AgentJobIndexes(job), ct);
        var persisted = await _agentJobs.GetAsync(Key(job.Id), ct);
        if (persisted is null || !AgentsJobImportIntegrity.AreEquivalent(job, persisted))
            throw new AgentJobImportException(
                $"Source identity '{job.Id}' was not stored as the converted Agent job.");
    }

    private async Task VerifyImportedJobsAsync(
        IReadOnlyList<AgentJob> jobs,
        CancellationToken ct)
    {
        foreach (var job in jobs)
        {
            var persisted = await _agentJobs.GetAsync(Key(job.Id), ct);
            if (persisted is null)
                throw new AgentJobImportException(
                    $"Source identity '{job.Id}' is missing from the completed import.");
            if (!AgentsJobImportIntegrity.AreEquivalent(job, persisted))
                throw new AgentJobImportException(
                    $"Source identity '{job.Id}' changed during the import.");
        }
    }

    private static void ValidateAgentJob(AgentJob job)
    {
        if (job.AgentId == Guid.Empty)
            throw new ArgumentException("An Agent job requires an agent id.", nameof(job));
        if (string.IsNullOrWhiteSpace(job.CallerIdentity))
            throw new ArgumentException("An Agent job requires caller identity.", nameof(job));
        if (string.IsNullOrWhiteSpace(job.ActionIdentity))
            throw new ArgumentException("An Agent job requires action identity.", nameof(job));
        if (string.IsNullOrWhiteSpace(job.Resource))
            throw new ArgumentException("An Agent job requires a resource.", nameof(job));
        if (string.IsNullOrWhiteSpace(job.Status))
            throw new ArgumentException("An Agent job requires a status.", nameof(job));
        if (string.IsNullOrWhiteSpace(job.PermissionIdentity))
            throw new ArgumentException("An Agent job requires permission identity.", nameof(job));
        if (job.InputTokens < 0 || job.OutputTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(job), "Token counts cannot be negative.");
        if (job.ApprovalIdentities is null || job.ApprovalIdentities.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Approval identities cannot be empty.", nameof(job));
        if (!string.Equals(job.ResultAuthority, AgentJob.CanonicalResultAuthority, StringComparison.Ordinal))
            throw new ArgumentException("Agent job results must use canonical Jobs authority.", nameof(job));
        if (!string.Equals(job.HandlerKey, AgentJobHandlerKeys.Canonical, StringComparison.Ordinal))
            throw new ArgumentException("Agent job handler authority is not supported.", nameof(job));
        if (!string.Equals(job.PayloadCodec, AgentJobPayloadCodecs.JsonV1, StringComparison.Ordinal))
            throw new ArgumentException("Agent job payload codec is not supported.", nameof(job));
        if (!job.RecoveryMode.Equals(
                AgentJobRecoveryModes.CanonicalHandler,
                StringComparison.Ordinal)
            && !job.RecoveryMode.Equals(
                AgentJobRecoveryModes.CanonicalRecovery,
                StringComparison.Ordinal)
            && !job.RecoveryMode.Equals(
                AgentJobRecoveryModes.Terminal,
                StringComparison.Ordinal))
            throw new ArgumentException("Agent job recovery mode is not supported.", nameof(job));
    }

    private static object AgentJobIndexes(AgentJob job) => new
    {
        agentId = job.AgentId.ToString("N"),
        callerIdentity = job.CallerIdentity,
        actionIdentity = job.ActionIdentity,
        resource = job.Resource,
        canonicalJobId = job.CanonicalJobId?.ToString("N") ?? string.Empty,
        channelId = job.ChannelId?.ToString("N") ?? string.Empty,
        contextId = job.ContextId?.ToString("N") ?? string.Empty,
        permissionIdentity = job.PermissionIdentity,
        status = job.Status,
        handlerKey = job.HandlerKey,
        payloadCodec = job.PayloadCodec,
        recoveryMode = job.RecoveryMode,
        createdAt = job.CreatedAt,
        updatedAt = job.UpdatedAt,
    };

    private static object AgentJobImportStateIndexes(AgentJobImportState state) => new
    {
        snapshotId = state.SnapshotId,
        aggregateHash = state.AggregateHash,
        expectedRecordCount = state.ExpectedRecordCount,
        importedRecordCount = state.ImportedRecordCount,
        completed = state.Completed ? "true" : "false",
        capturedAt = state.CapturedAt,
    };
}
