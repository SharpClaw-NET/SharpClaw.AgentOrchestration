using System.Text.Json;
using System.Text.Json.Serialization;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.Agents;

public interface IAgentsActionExecutor
{
    Task<AgentRecord> CreateAsync(
        RequestPrincipal caller,
        AgentsCreateAction action,
        CancellationToken ct = default);

    Task<AgentRecord?> UpdateAsync(
        RequestPrincipal caller,
        AgentsUpdateAction action,
        CancellationToken ct = default);

    Task<MemoryRecord> WriteMemoryAsync(
        RequestPrincipal caller,
        AgentsWriteMemoryAction action,
        CancellationToken ct = default);

    Task<SkillRecord> SaveSkillAsync(
        RequestPrincipal caller,
        AgentsSaveSkillAction action,
        CancellationToken ct = default);

    Task<string> AccessSkillAsync(
        RequestPrincipal caller,
        AgentsAccessSkillAction action,
        CancellationToken ct = default);

    Task<IReadOnlyList<MemoryRecord>> SearchMemoryAsync(
        RequestPrincipal caller,
        AgentsSearchMemoryAction action,
        CancellationToken ct = default);
}

public sealed class AgentsActionExecutor(
    AgentsCatalog catalog) : IAgentsActionExecutor
{
    public Task<AgentRecord> CreateAsync(
        RequestPrincipal caller,
        AgentsCreateAction action,
        CancellationToken ct = default) =>
        catalog.CreateAgentAsync(caller, action, ct);

    public Task<AgentRecord?> UpdateAsync(
        RequestPrincipal caller,
        AgentsUpdateAction action,
        CancellationToken ct = default) =>
        catalog.UpdateAgentAsync(caller, action, ct);

    public Task<MemoryRecord> WriteMemoryAsync(
        RequestPrincipal caller,
        AgentsWriteMemoryAction action,
        CancellationToken ct = default) =>
        catalog.WriteMemoryAsync(caller, action, ct);

    public Task<SkillRecord> SaveSkillAsync(
        RequestPrincipal caller,
        AgentsSaveSkillAction action,
        CancellationToken ct = default) =>
        catalog.SaveSkillAsync(caller, action.Skill, ct);

    public Task<string> AccessSkillAsync(
        RequestPrincipal caller,
        AgentsAccessSkillAction action,
        CancellationToken ct = default) =>
        catalog.AccessSkillAsync(caller, action.SkillId, ct);

    public Task<IReadOnlyList<MemoryRecord>> SearchMemoryAsync(
        RequestPrincipal caller,
        AgentsSearchMemoryAction action,
        CancellationToken ct = default) =>
        catalog.SearchMemoryAsync(caller, action.AgentId, action.Query, ct);
}

public sealed class AgentsApiActionExecutor(
    AgentsCatalog catalog,
    IAgentAccessPolicy access)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async ValueTask<JsonElement> ExecuteAsync(
        AgentsApiAction action,
        CancellationToken ct = default)
    {
        return action.Operation switch
        {
            AgentsApiOperations.ListAgents => Json(await ListAgentsAsync(action.Caller, ct)),
            AgentsApiOperations.GetAgent => Json(await GetAgentAsync(action, ct)),
            AgentsApiOperations.CreateAgent => Json(await catalog.CreateAgentAsync(action.Caller, Deserialize<AgentsCreateAction>(action.Payload), ct)),
            AgentsApiOperations.UpdateAgent => Json(await catalog.UpdateAgentAsync(action.Caller, Deserialize<AgentsUpdateAction>(action.Payload), ct)),
            AgentsApiOperations.DeleteAgent => Json(await catalog.DeleteAgentAsync(action.Caller, GuidValue(action.Payload, "agentId"), ct)),
            AgentsApiOperations.AssignRole => Json(await catalog.AssignRoleAsync(
                action.Caller,
                GuidValue(action.Payload, "agentId"),
                StringValue(action.Payload, "role"),
                BoolValue(action.Payload, "assign"),
                ct)),
            AgentsApiOperations.SynchronizeAgent => Json(await catalog.SynchronizeAsync(action.Caller, GuidValue(action.Payload, "agentId"), ct)),
            AgentsApiOperations.GetCost => Json(await catalog.GetCostAsync(action.Caller, GuidValue(action.Payload, "agentId"), ct)),
            AgentsApiOperations.ListSkills => Json(await ListSkillsAsync(action.Caller, ct)),
            AgentsApiOperations.GetSkill => Json(await catalog.GetSkillForCallerAsync(action.Caller, GuidValue(action.Payload, "skillId"), ct)),
            AgentsApiOperations.SaveSkill => Json(await catalog.SaveSkillAsync(action.Caller, Deserialize<AgentsSaveSkillAction>(action.Payload).Skill, ct)),
            AgentsApiOperations.DeleteSkill => Json(await catalog.DeleteSkillAsync(action.Caller, GuidValue(action.Payload, "skillId"), ct)),
            AgentsApiOperations.AccessSkill => Json(await catalog.AccessSkillAsync(action.Caller, GuidValue(action.Payload, "skillId"), ct)),
            AgentsApiOperations.WriteMemory => Json(await catalog.WriteMemoryAsync(action.Caller, Deserialize<AgentsWriteMemoryAction>(action.Payload), ct)),
            AgentsApiOperations.SearchMemory => Json(await catalog.SearchMemoryAsync(
                action.Caller,
                GuidValue(action.Payload, "agentId"),
                StringValue(action.Payload, "query"),
                ct)),
            _ => throw new ArgumentException($"Unknown Agents operation '{action.Operation}'.", nameof(action)),
        };
    }

    private async Task<IReadOnlyList<AgentRecord>> ListAgentsAsync(
        RequestPrincipal caller,
        CancellationToken ct)
    {
        await RequireAccessAsync(caller, "manage_agents", null, ct);
        return await catalog.ListAgentsAsync(ct);
    }

    private async Task<IReadOnlyList<SkillRecord>> ListSkillsAsync(
        RequestPrincipal caller,
        CancellationToken ct)
    {
        await RequireAccessAsync(caller, "manage_skills", null, ct);
        return await catalog.ListSkillsAsync(ct);
    }

    private async Task<AgentRecord> GetAgentAsync(AgentsApiAction action, CancellationToken ct)
    {
        var agentId = GuidValue(action.Payload, "agentId");
        if (!action.Caller.IsAuthenticated)
            throw new UnauthorizedAccessException("Authentication is required.");
        if (!IsAdministrator(action.Caller)
            && !string.Equals(action.Caller.SubjectId, agentId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            await RequireAccessAsync(action.Caller, "read_agents", agentId, ct);
        return await catalog.GetAgentAsync(agentId, ct)
            ?? throw new InvalidOperationException("The agent was not found.");
    }

    private async Task RequireAccessAsync(
        RequestPrincipal caller,
        string capability,
        Guid? targetAgentId,
        CancellationToken ct)
    {
        if (IsAdministrator(caller))
            return;
        if (!caller.IsAuthenticated)
            throw new UnauthorizedAccessException("Authentication is required.");
        var decision = await access.EvaluateAgentAsync(caller, capability, targetAgentId, ct);
        if (!decision.Allowed)
            throw new UnauthorizedAccessException(decision.Message);
    }

    private static T Deserialize<T>(JsonElement payload) =>
        payload.Deserialize<T>(JsonOptions)
        ?? throw new ArgumentException($"The {typeof(T).Name} payload is invalid.");

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value, JsonOptions);

    private static Guid GuidValue(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && Guid.TryParse(value.GetString(), out var id)
            ? id
            : throw new ArgumentException($"{name} is required.");

    private static string StringValue(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new ArgumentException($"{name} is required.");

    private static bool BoolValue(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static bool IsAdministrator(RequestPrincipal caller) =>
        caller.Roles?.Any(role => role.Equals("admin", StringComparison.OrdinalIgnoreCase)
            || role.Equals("administrator", StringComparison.OrdinalIgnoreCase)) == true;

}

public interface IAgentsActionGateway
{
    ValueTask<JsonElement> ExecuteAsync(
        RequestPrincipal caller,
        string operation,
        JsonElement payload,
        CancellationToken ct = default);
}

public sealed class AgentsActionGateway(
    AgentsApiActionExecutor executor) : IAgentsActionGateway
{
    public ValueTask<JsonElement> ExecuteAsync(
        RequestPrincipal caller,
        string operation,
        JsonElement payload,
        CancellationToken ct = default)
    {
        var action = new AgentsApiAction(operation, payload, caller);
        return executor.ExecuteAsync(action, ct);
    }
}
