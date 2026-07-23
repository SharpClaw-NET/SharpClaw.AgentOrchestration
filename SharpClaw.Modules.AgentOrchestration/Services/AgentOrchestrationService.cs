using System.Text.Json;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.AgentOrchestration.Services;

/// <summary>
/// Implements the agent, skill, and header operations exposed by the module.
/// </summary>
internal sealed class AgentOrchestrationService(
    SkillStore skills,
    IAgentManager agentManager)
{
    public async Task<string> CreateSubAgentAsync(
        JsonElement parameters, CancellationToken ct)
    {
        var name = parameters.TryGetProperty("name", out var nameProperty)
            ? nameProperty.GetString()
            : null;
        var modelIdText = parameters.TryGetProperty("modelId", out var modelProperty)
            ? modelProperty.GetString()
            : null;
        var systemPrompt = parameters.TryGetProperty("systemPrompt", out var promptProperty)
            ? promptProperty.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException(
                "create_sub_agent requires a 'name' parameter.");

        if (!Guid.TryParse(modelIdText, out var modelId))
            throw new InvalidOperationException(
                "create_sub_agent requires a valid 'modelId' GUID.");

        var (agentId, modelName, agentName) =
            await agentManager.CreateSubAgentAsync(name, modelId, systemPrompt, ct);

        return $"Created sub-agent '{agentName}' (id={agentId}, model={modelName}).";
    }

    public Task<string> ManageAgentAsync(
        Guid resourceId, JsonElement parameters, CancellationToken ct)
    {
        var newName = parameters.TryGetProperty("name", out var nameProperty)
            && nameProperty.GetString() is { Length: > 0 } name
            ? name
            : null;
        var systemPrompt = parameters.TryGetProperty("systemPrompt", out var promptProperty)
            ? promptProperty.GetString()
            : null;
        Guid? newModelId = parameters.TryGetProperty("modelId", out var modelProperty)
            && Guid.TryParse(modelProperty.GetString(), out var modelId)
            ? modelId
            : null;

        return agentManager.UpdateAgentAsync(
            resourceId, newName, systemPrompt, newModelId, ct);
    }

    public async Task<string> AccessSkillAsync(
        Guid resourceId, CancellationToken ct)
    {
        var skill = await skills.GetByIdAsync(resourceId, ct)
            ?? throw new InvalidOperationException(
                $"Skill {resourceId} not found.");

        return $"Skill: {skill.Name}\n\n{skill.SkillText}";
    }

    public async Task<string> EditAgentHeaderAsync(
        Guid resourceId, JsonElement parameters, CancellationToken ct)
    {
        if (!parameters.TryGetProperty("header", out var headerProperty))
        {
            return $"No changes applied to agent header (id={resourceId})."
                + " Pass \"header\": \"<text>\" to set or \"header\": \"\" to clear.";
        }

        var header = headerProperty.GetString();
        await agentManager.SetAgentHeaderAsync(resourceId, header, ct);

        return string.IsNullOrEmpty(header)
            ? $"Cleared custom chat header for agent (id={resourceId})."
            : $"Updated custom chat header for agent (id={resourceId}).";
    }

    public async Task<string> EditChannelHeaderAsync(
        Guid resourceId, JsonElement parameters, CancellationToken ct)
    {
        if (!parameters.TryGetProperty("header", out var headerProperty))
        {
            return $"No changes applied to channel header (id={resourceId})."
                + " Pass \"header\": \"<text>\" to set or \"header\": \"\" to clear.";
        }

        var header = headerProperty.GetString();
        await agentManager.SetChannelHeaderAsync(resourceId, header, ct);

        return string.IsNullOrEmpty(header)
            ? $"Cleared custom chat header for channel (id={resourceId})."
            : $"Updated custom chat header for channel (id={resourceId}).";
    }
}
