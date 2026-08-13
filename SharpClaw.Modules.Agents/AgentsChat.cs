using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.Agents;

public sealed class AgentChatProfileResolver(AgentsCatalog catalog) : IChatProfileResolver
{
    public async ValueTask<ChatProfile> ResolveAsync(
        ChatTurnContext turn,
        CancellationToken ct)
    {
        var subjectId = turn.Input.Caller?.SubjectId;
        if (!Guid.TryParse(subjectId, out var agentId))
            throw new InvalidOperationException("An agent caller is required for profile resolution.");
        var agent = await catalog.GetAgentAsync(agentId, ct)
            ?? throw new InvalidOperationException($"Agent '{agentId}' was not found.");
        return new ChatProfile(
            agent.ProviderKey,
            agent.ModelId,
            agent.ModelName,
            agent.SystemPrompt);
    }
}
