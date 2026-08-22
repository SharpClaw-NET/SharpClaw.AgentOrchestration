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
        ActionContext<AgentsApiAction> actionContext,
        CancellationToken ct = default)
    {
        var action = actionContext.Action;
        var caller = actionContext.Caller;
        var authorization = new ModuleActionAuthorization<AgentsApiAction>(actionContext, permission);
        using var authorizationScope = catalog.PushAuthorization(authorization);
        return action.Operation switch
        {
            AgentsApiOperations.ListAgents => Json(await ListAgentsAsync(caller, ct, authorization)),
            AgentsApiOperations.GetAgent => Json(await GetAgentAsync(caller, action, ct, authorization)),
            AgentsApiOperations.CreateAgent => Json(await catalog.CreateAgentAsync(caller, Deserialize<AgentsCreateAction>(action.Payload), ct, null)),
            AgentsApiOperations.UpdateAgent => Json(await catalog.UpdateAgentAsync(caller, Deserialize<AgentsUpdateAction>(action.Payload), ct, null)),
            AgentsApiOperations.DeleteAgent => Json(await catalog.DeleteAgentAsync(caller, GuidValue(action.Payload, "agentId"), ct, null)),
            AgentsApiOperations.AssignRole => Json(await catalog.AssignRoleAsync(
                caller,
                GuidValue(action.Payload, "agentId"),
                StringValue(action.Payload, "role"),
                BoolValue(action.Payload, "assign"),
                ct,
                null)),
            AgentsApiOperations.SynchronizeAgent => Json(await catalog.SynchronizeAsync(caller, GuidValue(action.Payload, "agentId"), ct, null)),
            AgentsApiOperations.GetCost => Json(await catalog.GetCostAsync(caller, GuidValue(action.Payload, "agentId"), ct, null)),
            AgentsApiOperations.ListSkills => Json(await ListSkillsAsync(caller, ct, authorization)),
            AgentsApiOperations.GetSkill => Json(await catalog.GetSkillForCallerAsync(caller, GuidValue(action.Payload, "skillId"), ct, null)),
            AgentsApiOperations.SaveSkill => Json(await catalog.SaveSkillAsync(caller, Deserialize<AgentsSaveSkillAction>(action.Payload).Skill, ct, null)),
            AgentsApiOperations.DeleteSkill => Json(await catalog.DeleteSkillAsync(caller, GuidValue(action.Payload, "skillId"), ct, null)),
            AgentsApiOperations.AccessSkill => Json(await catalog.AccessSkillAsync(caller, GuidValue(action.Payload, "skillId"), ct, null)),
            AgentsApiOperations.WriteMemory => Json(await catalog.WriteMemoryAsync(caller, Deserialize<AgentsWriteMemoryAction>(action.Payload), ct, null)),
            AgentsApiOperations.SearchMemory => Json(await catalog.SearchMemoryAsync(
                caller,
                GuidValue(action.Payload, "agentId"),
                StringValue(action.Payload, "query"),
                ct,
                null)),
            AgentsApiOperations.ImportAgentJobs => Json(await catalog.ImportAgentJobsAsync(
                caller,
                Deserialize<CanonicalJobsImportSnapshot>(action.Payload),
                ct,
                null)),
            _ => throw new ArgumentException($"Unknown Agents operation '{action.Operation}'.", nameof(action)),
        };
    }

    private async Task<IReadOnlyList<AgentRecord>> ListAgentsAsync(
        RequestPrincipal caller,
        CancellationToken ct,
        IModuleActionAuthorization authorization)
    {
        await RequireAccessAsync(caller, "manage_agents", null, ct, authorization);
        return await catalog.ListAgentsAsync(ct);
    }

    private async Task<IReadOnlyList<SkillRecord>> ListSkillsAsync(
        RequestPrincipal caller,
        CancellationToken ct,
        IModuleActionAuthorization authorization)
    {
        await RequireAccessAsync(caller, "manage_skills", null, ct, authorization);
        return await catalog.ListSkillsAsync(ct);
    }

    private async Task<AgentRecord> GetAgentAsync(
        RequestPrincipal caller,
        AgentsApiAction action,
        CancellationToken ct,
        IModuleActionAuthorization authorization)
    {
        var agentId = GuidValue(action.Payload, "agentId");
        if (!caller.IsAuthenticated)
            throw new UnauthorizedAccessException("Authentication is required.");
        if (!IsAdministrator(caller)
            && !string.Equals(caller.SubjectId, agentId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            await RequireAccessAsync(caller, "read_agents", agentId, ct, authorization);
        return await catalog.GetAgentAsync(agentId, ct)
            ?? throw new InvalidOperationException("The agent was not found.");
    }

    private async Task RequireAccessAsync(
        RequestPrincipal caller,
        string capability,
        Guid? targetAgentId,
        CancellationToken ct,
        IModuleActionAuthorization authorization)
    {
        if (IsAdministrator(caller))
            return;
        if (!caller.IsAuthenticated)
            throw new UnauthorizedAccessException("Authentication is required.");
        var decision = await authorization.EvaluateAgentAsync(capability, targetAgentId, ct);
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
    HostModuleActionEntry entry,
    AgentsApiActionTerminal terminal) : IAgentsActionGateway
{
    public ValueTask<JsonElement> ExecuteAsync(
        HostActionEntryRequestContext hostContext,
        string operation,
        JsonElement payload,
        CancellationToken ct = default)
    {
        var action = new AgentsApiAction(operation, payload);
        return entry.InvokeAsync(
            AgentsModule.ApiDescriptor,
            action,
            hostContext,
            terminal,
            ct);
    }
}
