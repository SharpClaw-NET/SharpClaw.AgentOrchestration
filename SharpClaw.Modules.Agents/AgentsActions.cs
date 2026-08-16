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
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null);

    Task<AgentRecord?> UpdateAsync(
        RequestPrincipal caller,
        AgentsUpdateAction action,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null);

    Task<MemoryRecord> WriteMemoryAsync(
        RequestPrincipal caller,
        AgentsWriteMemoryAction action,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null);

    Task<SkillRecord> SaveSkillAsync(
        RequestPrincipal caller,
        AgentsSaveSkillAction action,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null);

    Task<string> AccessSkillAsync(
        RequestPrincipal caller,
        AgentsAccessSkillAction action,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null);

    Task<IReadOnlyList<MemoryRecord>> SearchMemoryAsync(
        RequestPrincipal caller,
        AgentsSearchMemoryAction action,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null);
}

public sealed class AgentsActionExecutor(
    AgentsCatalog catalog) : IAgentsActionExecutor
{
    public Task<AgentRecord> CreateAsync(
        RequestPrincipal caller,
        AgentsCreateAction action,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null) =>
        catalog.CreateAgentAsync(caller, action, ct, hostContext);

    public Task<AgentRecord?> UpdateAsync(
        RequestPrincipal caller,
        AgentsUpdateAction action,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null) =>
        catalog.UpdateAgentAsync(caller, action, ct, hostContext);

    public Task<MemoryRecord> WriteMemoryAsync(
        RequestPrincipal caller,
        AgentsWriteMemoryAction action,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null) =>
        catalog.WriteMemoryAsync(caller, action, ct, hostContext);

    public Task<SkillRecord> SaveSkillAsync(
        RequestPrincipal caller,
        AgentsSaveSkillAction action,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null) =>
        catalog.SaveSkillAsync(caller, action.Skill, ct, hostContext);

    public Task<string> AccessSkillAsync(
        RequestPrincipal caller,
        AgentsAccessSkillAction action,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null) =>
        catalog.AccessSkillAsync(caller, action.SkillId, ct, hostContext);

    public Task<IReadOnlyList<MemoryRecord>> SearchMemoryAsync(
        RequestPrincipal caller,
        AgentsSearchMemoryAction action,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null) =>
        catalog.SearchMemoryAsync(caller, action.AgentId, action.Query, ct, hostContext);
}

public sealed class AgentsApiActionExecutor(
    AgentsCatalog catalog,
    HostPermissionActionEntry permission)
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
            AgentsApiOperations.ListAgents => Json(await ListAgentsAsync(action.Caller, ct, action.HostActionContext)),
            AgentsApiOperations.GetAgent => Json(await GetAgentAsync(action, ct)),
            AgentsApiOperations.CreateAgent => Json(await catalog.CreateAgentAsync(action.Caller, Deserialize<AgentsCreateAction>(action.Payload), ct, action.HostActionContext)),
            AgentsApiOperations.UpdateAgent => Json(await catalog.UpdateAgentAsync(action.Caller, Deserialize<AgentsUpdateAction>(action.Payload), ct, action.HostActionContext)),
            AgentsApiOperations.DeleteAgent => Json(await catalog.DeleteAgentAsync(action.Caller, GuidValue(action.Payload, "agentId"), ct, action.HostActionContext)),
            AgentsApiOperations.AssignRole => Json(await catalog.AssignRoleAsync(
                action.Caller,
                GuidValue(action.Payload, "agentId"),
                StringValue(action.Payload, "role"),
                BoolValue(action.Payload, "assign"),
                ct,
                action.HostActionContext)),
            AgentsApiOperations.SynchronizeAgent => Json(await catalog.SynchronizeAsync(action.Caller, GuidValue(action.Payload, "agentId"), ct, action.HostActionContext)),
            AgentsApiOperations.GetCost => Json(await catalog.GetCostAsync(action.Caller, GuidValue(action.Payload, "agentId"), ct, action.HostActionContext)),
            AgentsApiOperations.ListSkills => Json(await ListSkillsAsync(action.Caller, ct, action.HostActionContext)),
            AgentsApiOperations.GetSkill => Json(await catalog.GetSkillForCallerAsync(action.Caller, GuidValue(action.Payload, "skillId"), ct, action.HostActionContext)),
            AgentsApiOperations.SaveSkill => Json(await catalog.SaveSkillAsync(action.Caller, Deserialize<AgentsSaveSkillAction>(action.Payload).Skill, ct, action.HostActionContext)),
            AgentsApiOperations.DeleteSkill => Json(await catalog.DeleteSkillAsync(action.Caller, GuidValue(action.Payload, "skillId"), ct, action.HostActionContext)),
            AgentsApiOperations.AccessSkill => Json(await catalog.AccessSkillAsync(action.Caller, GuidValue(action.Payload, "skillId"), ct, action.HostActionContext)),
            AgentsApiOperations.WriteMemory => Json(await catalog.WriteMemoryAsync(action.Caller, Deserialize<AgentsWriteMemoryAction>(action.Payload), ct, action.HostActionContext)),
            AgentsApiOperations.SearchMemory => Json(await catalog.SearchMemoryAsync(
                action.Caller,
                GuidValue(action.Payload, "agentId"),
                StringValue(action.Payload, "query"),
                ct,
                action.HostActionContext)),
            _ => throw new ArgumentException($"Unknown Agents operation '{action.Operation}'.", nameof(action)),
        };
    }

    private async Task<IReadOnlyList<AgentRecord>> ListAgentsAsync(
        RequestPrincipal caller,
        CancellationToken ct,
        HostActionEntryRequestContext hostContext)
    {
        await RequireAccessAsync(caller, "manage_agents", null, ct, hostContext);
        return await catalog.ListAgentsAsync(ct);
    }

    private async Task<IReadOnlyList<SkillRecord>> ListSkillsAsync(
        RequestPrincipal caller,
        CancellationToken ct,
        HostActionEntryRequestContext hostContext)
    {
        await RequireAccessAsync(caller, "manage_skills", null, ct, hostContext);
        return await catalog.ListSkillsAsync(ct);
    }

    private async Task<AgentRecord> GetAgentAsync(AgentsApiAction action, CancellationToken ct)
    {
        var agentId = GuidValue(action.Payload, "agentId");
        if (!action.Caller.IsAuthenticated)
            throw new UnauthorizedAccessException("Authentication is required.");
        if (!IsAdministrator(action.Caller)
            && !string.Equals(action.Caller.SubjectId, agentId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            await RequireAccessAsync(action.Caller, "read_agents", agentId, ct, action.HostActionContext);
        return await catalog.GetAgentAsync(agentId, ct)
            ?? throw new InvalidOperationException("The agent was not found.");
    }

    private async Task RequireAccessAsync(
        RequestPrincipal caller,
        string capability,
        Guid? targetAgentId,
        CancellationToken ct,
        HostActionEntryRequestContext hostContext)
    {
        if (IsAdministrator(caller))
            return;
        if (!caller.IsAuthenticated)
            throw new UnauthorizedAccessException("Authentication is required.");
        var decision = await permission.EvaluateAgentAsync(hostContext, capability, targetAgentId, ct);
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
        HostActionEntryRequestContext hostContext,
        string operation,
        JsonElement payload,
        CancellationToken ct = default);
}

public sealed class AgentsActionGateway(
    HostModuleActionEntry entry) : IAgentsActionGateway
{
    public ValueTask<JsonElement> ExecuteAsync(
        HostActionEntryRequestContext hostContext,
        string operation,
        JsonElement payload,
        CancellationToken ct = default)
    {
        var action = new AgentsApiAction(operation, payload, hostContext);
        return entry.InvokeAsync(AgentsModule.ApiDescriptor, action, hostContext, ct);
    }
}
