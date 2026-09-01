using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.Agents;

public sealed class AgentChatProfileResolver(
    AgentsCatalog catalog,
    HostPermissionActionEntry permission) : IChatProfileResolver
{
    public async ValueTask<ChatProfile> ResolveAsync(
        ChatTurnContext turn,
        ChatOperationContext context,
        CancellationToken ct)
    {
        var subjectId = context.Caller.SubjectId;
        if (!Guid.TryParse(subjectId, out var agentId))
            throw new InvalidOperationException("An agent caller is required for profile resolution.");
        using var authorization = catalog.PushAuthorization(
            new ChatOperationAuthorization(context, permission));
        var decision = await permission.EvaluateAgentAsync(
            context,
            "read_agent_profile",
            agentId,
            ct);
        if (!decision.Allowed)
            throw new UnauthorizedAccessException($"{decision.Code}: {decision.Message}");
        var agent = await catalog.GetAgentAsync(agentId, ct)
            ?? throw new InvalidOperationException($"Agent '{agentId}' was not found.");
        return new ChatProfile(
            agent.ProviderKey,
            agent.ModelId,
            agent.ModelName,
            agent.SystemPrompt);
    }
}
