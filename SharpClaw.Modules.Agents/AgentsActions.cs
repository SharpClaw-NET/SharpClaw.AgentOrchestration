using SharpClaw.Contracts.Modules;

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
