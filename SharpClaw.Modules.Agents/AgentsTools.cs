using System.Text.Json;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.Agents;

public sealed class AgentsToolHandler(AgentsCatalog catalog) : IToolHandler
{
    public async ValueTask<ToolResult> InvokeAsync(
        ToolInvocation invocation,
        CancellationToken ct)
    {
        try
        {
            return invocation.ToolName switch
            {
                AgentsModule.CreateTool => await CreateAsync(invocation, ct),
                AgentsModule.UpdateTool => await UpdateAsync(invocation, ct),
                AgentsModule.AccessSkillTool => await AccessSkillAsync(invocation, ct),
                AgentsModule.WriteMemoryTool => await WriteMemoryAsync(invocation, ct),
                AgentsModule.SearchMemoryTool => await SearchMemoryAsync(invocation, ct),
                _ => ToolResult.Error($"Unknown Agents tool '{invocation.ToolName}'."),
            };
        }
        catch (UnauthorizedAccessException exception)
        {
            return ToolResult.Error(exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ToolResult.Error(exception.Message);
        }
    }

    private async Task<ToolResult> CreateAsync(ToolInvocation invocation, CancellationToken ct)
    {
        var name = StringValue(invocation.Arguments, "name");
        var providerKey = StringValue(invocation.Arguments, "providerKey") ?? "default";
        var modelName = StringValue(invocation.Arguments, "modelName");
        var systemPrompt = StringValue(invocation.Arguments, "systemPrompt");
        if (name is null || !Guid.TryParse(StringValue(invocation.Arguments, "modelId"), out var modelId))
            return ToolResult.Error("name and modelId are required.");
        var agent = await catalog.CreateAgentAsync(invocation.Caller,
            new(name, modelId, providerKey, modelName, systemPrompt), ct);
        return new ToolResult(JsonSerializer.Serialize(agent));
    }

    private async Task<ToolResult> UpdateAsync(ToolInvocation invocation, CancellationToken ct)
    {
        if (!Guid.TryParse(StringValue(invocation.Arguments, "agentId"), out var agentId))
            return ToolResult.Error("agentId is required.");
        var modelId = Guid.TryParse(StringValue(invocation.Arguments, "modelId"), out var parsed)
            ? parsed
            : (Guid?)null;
        var agent = await catalog.UpdateAgentAsync(invocation.Caller,
            new(agentId, StringValue(invocation.Arguments, "name"), modelId,
                StringValue(invocation.Arguments, "providerKey"),
                StringValue(invocation.Arguments, "systemPrompt")), ct);
        return agent is null
            ? ToolResult.Error("The agent was not found.")
            : new ToolResult(JsonSerializer.Serialize(agent));
    }

    private async Task<ToolResult> AccessSkillAsync(ToolInvocation invocation, CancellationToken ct)
    {
        if (!Guid.TryParse(StringValue(invocation.Arguments, "skillId"), out var skillId))
            return ToolResult.Error("skillId is required.");
        return ToolResult.Text(await catalog.AccessSkillAsync(invocation.Caller, skillId, ct));
    }

    private async Task<ToolResult> WriteMemoryAsync(ToolInvocation invocation, CancellationToken ct)
    {
        if (!Guid.TryParse(StringValue(invocation.Arguments, "agentId"), out var agentId))
            return ToolResult.Error("agentId is required.");
        var key = StringValue(invocation.Arguments, "key");
        var content = StringValue(invocation.Arguments, "content");
        if (key is null || content is null)
            return ToolResult.Error("key and content are required.");
        var memory = await catalog.WriteMemoryAsync(invocation.Caller,
            new(agentId, key, content, StringList(invocation.Arguments, "tags")), ct);
        return new ToolResult(JsonSerializer.Serialize(memory));
    }

    private async Task<ToolResult> SearchMemoryAsync(ToolInvocation invocation, CancellationToken ct)
    {
        if (!Guid.TryParse(StringValue(invocation.Arguments, "agentId"), out var agentId))
            return ToolResult.Error("agentId is required.");
        var memory = await catalog.SearchMemoryAsync(
            invocation.Caller, agentId, StringValue(invocation.Arguments, "query"), ct);
        return new ToolResult(JsonSerializer.Serialize(memory));
    }

    private static string? StringValue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> StringList(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!).ToArray()
            : [];
}
