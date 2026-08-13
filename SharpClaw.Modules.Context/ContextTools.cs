using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.Context;

public sealed class ContextToolHandler(
    ContextStore store) : IToolHandler
{
    public async ValueTask<ToolResult> InvokeAsync(
        ToolInvocation invocation,
        CancellationToken ct)
    {
        if (!invocation.Caller.IsAuthenticated
            || !Guid.TryParse(invocation.Caller.SubjectId, out var agentId)
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

        var threads = await store.ListAccessibleThreadsAsync(
            invocation.Caller, channelId, ct);
        var payload = threads.Select(thread => new
        {
            threadId = thread.ThreadId,
            threadName = thread.ThreadName,
            channelId = thread.ChannelId,
            channelTitle = thread.ChannelTitle,
            updatedAt = thread.UpdatedAt,
        });
        return new ToolResult(JsonSerializer.Serialize(payload));
    }

    private async Task<ToolResult> ReadAsync(ToolInvocation invocation, CancellationToken ct)
    {
        if (!TryGuid(invocation.Arguments, "channelId", out var channelId))
            return ToolResult.Error("channelId is required.");
        if (!TryGuid(invocation.Arguments, "threadId", out var threadId))
            return ToolResult.Error("threadId is required.");

        var thread = await store.FindAccessibleThreadAsync(
            invocation.Caller, channelId, threadId, ct);
        if (thread is null)
            return ToolResult.Error("The thread is missing or inaccessible.");

        var maxMessages = invocation.Arguments.TryGetProperty("maxMessages", out var max)
            && max.TryGetInt32(out var requested)
            ? Math.Clamp(requested, 1, 200)
            : 50;
        var messages = await store.ReadMessagesAsync(threadId, maxMessages, ct);
        if (messages.Count == 0)
            return ToolResult.Text("The thread exists but has no messages.");
        return new ToolResult(JsonSerializer.Serialize(messages.Select(message => new
        {
            role = message.Role,
            content = message.Content,
            sender = message.Sender,
            timestamp = message.CreatedAt,
        })));
    }

    private static bool TryGuid(JsonElement arguments, string name, out Guid value)
    {
        value = Guid.Empty;
        return arguments.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
        && Guid.TryParse(property.GetString(), out value);
    }
}
