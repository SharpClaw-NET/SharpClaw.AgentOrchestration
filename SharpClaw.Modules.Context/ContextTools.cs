using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.Context;

public sealed class ContextToolHandler(
    IContextActionGateway gateway) : IToolHandler
{
    public async ValueTask<ToolResult> InvokeAsync(
        ToolInvocation invocation,
        CancellationToken ct)
    {
        var caller = invocation.HostActionContext.Caller;
        if (!caller.IsAuthenticated
            || !Guid.TryParse(caller.SubjectId, out var agentId)
            || agentId == Guid.Empty)
            return ToolResult.Error("The caller subject must be an agent GUID.");

        return invocation.ToolName switch
        {
            ContextModule.ListThreadsTool => await ListAsync(invocation, ct),
            ContextModule.ReadHistoryTool => await ReadAsync(invocation, ct),
            _ => ToolResult.Error($"Unknown Context tool '{invocation.ToolName}'."),
        };
    }

    private async Task<ToolResult> ListAsync(ToolInvocation invocation, CancellationToken ct)
    {
        if (!TryGuid(invocation.Arguments, "channelId", out var channelId))
            return ToolResult.Error("channelId is required.");

        using var payload = JsonDocument.Parse($$"""{"channelId":"{{channelId:D}}"}""");
        var result = await gateway.ExecuteAsync(
            invocation.HostActionContext,
            ContextApiOperations.ListThreads,
            payload.RootElement,
            ct);
        return new ToolResult(result.GetRawText());
    }

    private async Task<ToolResult> ReadAsync(ToolInvocation invocation, CancellationToken ct)
    {
        if (!TryGuid(invocation.Arguments, "channelId", out var channelId))
            return ToolResult.Error("channelId is required.");
        if (!TryGuid(invocation.Arguments, "threadId", out var threadId))
            return ToolResult.Error("threadId is required.");

        var maxMessages = invocation.Arguments.TryGetProperty("maxMessages", out var max)
            && max.TryGetInt32(out var requested)
            ? Math.Clamp(requested, 1, 200)
            : 50;
        using var payload = JsonDocument.Parse($$"""{"channelId":"{{channelId:D}}","threadId":"{{threadId:D}}","maxMessages":{{maxMessages}}}""");
        try
        {
            var result = await gateway.ExecuteAsync(
                invocation.HostActionContext,
                ContextApiOperations.ReadHistory,
                payload.RootElement,
                ct);
            return new ToolResult(result.GetRawText());
        }
        catch (InvalidOperationException exception)
        {
            return ToolResult.Error(exception.Message);
        }
    }

    private static bool TryGuid(JsonElement arguments, string name, out Guid value)
    {
        value = Guid.Empty;
        return arguments.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
        && Guid.TryParse(property.GetString(), out value);
    }
}
