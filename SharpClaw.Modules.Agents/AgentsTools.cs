using System.Text.Json;
using System.Text.Json.Serialization;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.Agents;

public sealed class AgentsToolHandler(IAgentsActionGateway gateway) : IToolHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async ValueTask<ToolResult> InvokeAsync(
        ToolInvocation invocation,
        CancellationToken ct)
    {
        try
        {
            var operation = invocation.ToolName switch
            {
                AgentsModule.CreateTool => AgentsApiOperations.CreateAgent,
                AgentsModule.UpdateTool => AgentsApiOperations.UpdateAgent,
                AgentsModule.AccessSkillTool => AgentsApiOperations.AccessSkill,
                AgentsModule.WriteMemoryTool => AgentsApiOperations.WriteMemory,
                AgentsModule.SearchMemoryTool => AgentsApiOperations.SearchMemory,
                _ => null,
            };
            if (operation is null)
                return ToolResult.Error($"Unknown Agents tool '{invocation.ToolName}'.");
            var payload = BuildPayload(operation, invocation.Arguments);
            var result = await gateway.ExecuteAsync(invocation.Caller, operation, payload, ct);
            return new ToolResult(result.GetRawText());
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

    private static JsonElement BuildPayload(string operation, JsonElement arguments) =>
        operation switch
        {
            AgentsApiOperations.CreateAgent => JsonSerializer.SerializeToElement(
                new AgentsCreateAction(
                    RequiredStringValue(arguments, "name"),
                    GuidValue(arguments, "modelId"),
                    StringValue(arguments, "providerKey") ?? "default",
                    StringValue(arguments, "modelName"),
                    StringValue(arguments, "systemPrompt")),
                JsonOptions),
            AgentsApiOperations.UpdateAgent => JsonSerializer.SerializeToElement(
                new AgentsUpdateAction(
                    GuidValue(arguments, "agentId"),
                    StringValue(arguments, "name"),
                    GuidNullableValue(arguments, "modelId"),
                    StringValue(arguments, "providerKey"),
                    StringValue(arguments, "systemPrompt")),
                JsonOptions),
            AgentsApiOperations.AccessSkill => JsonSerializer.SerializeToElement(
                new { skillId = GuidValue(arguments, "skillId") }, JsonOptions),
            AgentsApiOperations.WriteMemory => JsonSerializer.SerializeToElement(
                new AgentsWriteMemoryAction(
                    GuidValue(arguments, "agentId"),
                    RequiredStringValue(arguments, "key"),
                    RequiredStringValue(arguments, "content"),
                    StringList(arguments, "tags")),
                JsonOptions),
            AgentsApiOperations.SearchMemory => JsonSerializer.SerializeToElement(
                new AgentsSearchMemoryAction(
                    GuidValue(arguments, "agentId"),
                    StringValue(arguments, "query")),
                JsonOptions),
            _ => throw new ArgumentException("The Agents operation is not supported.", nameof(operation)),
        };

    private static Guid GuidValue(JsonElement root, string name) =>
        Guid.TryParse(StringValue(root, name), out var value)
            ? value
            : throw new ArgumentException($"{name} is required.");

    private static Guid? GuidNullableValue(JsonElement root, string name) =>
        Guid.TryParse(StringValue(root, name), out var value) ? value : null;

    private static string? StringValue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string RequiredStringValue(JsonElement root, string name) =>
        StringValue(root, name)
        ?? throw new ArgumentException($"{name} is required.");

    private static IReadOnlyList<string> StringList(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToArray()
            : [];
}
