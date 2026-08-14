using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.Context;

public interface IContextActionExecutor
{
    Task<ContextThreadRecord> CreateThreadAsync(
        RequestPrincipal caller,
        ContextCreateThreadAction action,
        CancellationToken ct = default);

    Task<IReadOnlyList<ContextMessageRecord>> ReadHistoryAsync(
        RequestPrincipal caller,
        ContextReadHistoryAction action,
        CancellationToken ct = default);

    Task<bool> CommitExchangeAsync(
        RequestPrincipal caller,
        ContextCommitExchangeAction action,
        CancellationToken ct = default);

    ValueTask CommitExchangeAsync(
        ChatExchange exchange,
        CancellationToken ct = default);
}

public sealed class ContextActionExecutor(ContextStore store) : IContextActionExecutor
{
    public async Task<ContextThreadRecord> CreateThreadAsync(
        RequestPrincipal caller,
        ContextCreateThreadAction action,
        CancellationToken ct = default)
    {
        if (!caller.IsAuthenticated
            || !Guid.TryParse(caller.SubjectId, out var agentId)
            || agentId == Guid.Empty)
            throw new UnauthorizedAccessException("An agent caller is required.");
        if (action.ChannelId == Guid.Empty)
            throw new ArgumentException("A channel id is required.", nameof(action));
        _ = agentId;
        return await store.CreateThreadAsync(
            caller,
            action.ChannelId,
            action.Name,
            action.ContextId,
            ct: ct);
    }

    public async Task<IReadOnlyList<ContextMessageRecord>> ReadHistoryAsync(
        RequestPrincipal caller,
        ContextReadHistoryAction action,
        CancellationToken ct = default)
    {
        var thread = await store.FindAccessibleThreadAsync(
            caller, action.ChannelId, action.ThreadId, ct)
            ?? throw new UnauthorizedAccessException("The thread is missing or inaccessible.");
        return await store.ReadMessagesAsync(thread.ThreadId, action.MaxMessages, ct);
    }

    public Task<bool> CommitExchangeAsync(
        RequestPrincipal caller,
        ContextCommitExchangeAction action,
        CancellationToken ct = default) =>
        store.CommitExchangeAsync(caller, action, ct);

    public ValueTask CommitExchangeAsync(
        ChatExchange exchange,
        CancellationToken ct = default) =>
        store.CommitExchangeAsync(exchange, ct);
}

public sealed record ContextApiAction(
    string Operation,
    JsonElement Payload,
    RequestPrincipal Caller);

public sealed class ContextApiActionExecutor(ContextStore store)
{
    public async ValueTask<JsonElement> ExecuteAsync(
        ContextApiAction action,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action.Operation);
        return action.Operation switch
        {
            ContextApiOperations.ListChannels => await JsonAsync(await store.ListChannelsAsync(action.Caller, ct)),
            ContextApiOperations.GetChannel => await JsonAsync(await store.GetChannelForCallerAsync(action.Caller, GuidValue(action.Payload, "channelId"), ct)),
            ContextApiOperations.CreateChannel => await JsonAsync(await store.CreateChannelAsync(action.Caller, action.Payload, ct)),
            ContextApiOperations.UpdateChannel => await JsonAsync(await store.UpdateChannelAsync(action.Caller, action.Payload, ct)),
            ContextApiOperations.DeleteChannel => await JsonAsync(await store.DeleteChannelAsync(action.Caller, GuidValue(action.Payload, "channelId"), ct)),
            ContextApiOperations.AssignChannel => await JsonAsync(await store.AssignChannelAsync(action.Caller, action.Payload, ct)),
            ContextApiOperations.UnassignChannel => await JsonAsync(await store.UnassignChannelAsync(action.Caller, action.Payload, ct)),
            ContextApiOperations.OptInChannel => await JsonAsync(await store.SetChannelOptInAsync(action.Caller, action.Payload, true, ct)),
            ContextApiOperations.OptOutChannel => await JsonAsync(await store.SetChannelOptInAsync(action.Caller, action.Payload, false, ct)),
            ContextApiOperations.ChannelPermissions => await JsonAsync(await store.GetChannelPermissionsAsync(action.Caller, GuidValue(action.Payload, "channelId"), ct)),
            ContextApiOperations.SynchronizeChannel => await JsonAsync(await store.SynchronizeChannelAsync(action.Caller, GuidValue(action.Payload, "channelId"), ct)),
            ContextApiOperations.ListContexts => await JsonAsync(await store.ListContextsAsync(action.Caller, ct)),
            ContextApiOperations.GetContext => await JsonAsync(await store.GetContextForCallerAsync(action.Caller, GuidValue(action.Payload, "contextId"), ct)),
            ContextApiOperations.CreateContext => await JsonAsync(await store.CreateContextAsync(action.Caller, action.Payload, ct)),
            ContextApiOperations.UpdateContext => await JsonAsync(await store.UpdateContextAsync(action.Caller, action.Payload, ct)),
            ContextApiOperations.DeleteContext => await JsonAsync(await store.DeleteContextAsync(action.Caller, GuidValue(action.Payload, "contextId"), ct)),
            ContextApiOperations.AssignContext => await JsonAsync(await store.AssignContextAsync(action.Caller, action.Payload, ct)),
            ContextApiOperations.UnassignContext => await JsonAsync(await store.UnassignContextAsync(action.Caller, action.Payload, ct)),
            ContextApiOperations.ActivateContext => await JsonAsync(await store.SetContextEnabledAsync(action.Caller, action.Payload, true, ct)),
            ContextApiOperations.DeactivateContext => await JsonAsync(await store.SetContextEnabledAsync(action.Caller, action.Payload, false, ct)),
            ContextApiOperations.SynchronizeContext => await JsonAsync(await store.SynchronizeContextAsync(action.Caller, GuidValue(action.Payload, "contextId"), ct)),
            ContextApiOperations.ContextPermissions => await JsonAsync(await store.GetContextPermissionsAsync(action.Caller, GuidValue(action.Payload, "contextId"), ct)),
            ContextApiOperations.ListThreads => await JsonAsync(await store.ListAccessibleThreadsAsync(
                action.Caller,
                GuidValue(action.Payload, "channelId"),
                ct)),
            ContextApiOperations.GetThread => await JsonAsync(await store.GetThreadForCallerAsync(action.Caller, GuidValue(action.Payload, "threadId"), ct)),
            ContextApiOperations.CreateThread => await JsonAsync(await store.CreateThreadFromPayloadAsync(action.Caller, action.Payload, ct)),
            ContextApiOperations.UpdateThread => await JsonAsync(await store.UpdateThreadAsync(action.Caller, action.Payload, ct)),
            ContextApiOperations.DeleteThread => await JsonAsync(await store.DeleteThreadAsync(action.Caller, GuidValue(action.Payload, "threadId"), ct)),
            ContextApiOperations.ReadHistory => await JsonAsync(await ReadHistoryAsync(action, ct)),
            ContextApiOperations.CommitExchange => await JsonAsync(await CommitExchangeAsync(action, ct)),
            _ => throw new ArgumentException($"Unknown Context operation '{action.Operation}'.", nameof(action)),
        };
    }

    private async Task<IReadOnlyList<ContextMessageRecord>> ReadHistoryAsync(
        ContextApiAction action,
        CancellationToken ct)
    {
        var channelId = GuidValue(action.Payload, "channelId");
        var threadId = GuidValue(action.Payload, "threadId");
        var thread = await store.FindAccessibleThreadAsync(action.Caller, channelId, threadId, ct)
            ?? throw new UnauthorizedAccessException("The thread is missing or inaccessible.");
        var maxMessages = action.Payload.TryGetProperty("maxMessages", out var max)
            && max.TryGetInt32(out var requested)
            ? requested
            : 50;
        return await store.ReadMessagesAsync(thread.ThreadId, maxMessages, ct);
    }

    private Task<bool> CommitExchangeAsync(
        ContextApiAction action,
        CancellationToken ct)
    {
        var threadId = GuidValue(action.Payload, "threadId");
        return store.CommitExchangeAsync(
            action.Caller,
            new ContextCommitExchangeAction(
                threadId,
                StringValue(action.Payload, "userMessage") ?? string.Empty,
                StringValue(action.Payload, "assistantMessage") ?? string.Empty),
            ct);
    }

    private static ValueTask<JsonElement> JsonAsync<T>(T value) =>
        ValueTask.FromResult(JsonSerializer.SerializeToElement(value));

    private static Guid GuidValue(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && Guid.TryParse(value.GetString(), out var id)
            ? id
            : throw new ArgumentException($"{name} is required.");

    private static string? StringValue(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public interface IContextActionGateway
{
    ValueTask<JsonElement> ExecuteAsync(
        RequestPrincipal caller,
        string operation,
        JsonElement payload,
        CancellationToken ct = default);
}

public sealed class ContextActionGateway(
    ContextApiActionExecutor executor) : IContextActionGateway
{
    public ValueTask<JsonElement> ExecuteAsync(
        RequestPrincipal caller,
        string operation,
        JsonElement payload,
        CancellationToken ct = default)
    {
        var action = new ContextApiAction(operation, payload, caller);
        return executor.ExecuteAsync(action, ct);
    }
}

public static class ContextApiOperations
{
    public const string ListChannels = "channel.list";
    public const string GetChannel = "channel.get";
    public const string CreateChannel = "channel.create";
    public const string UpdateChannel = "channel.update";
    public const string DeleteChannel = "channel.delete";
    public const string AssignChannel = "channel.assign";
    public const string UnassignChannel = "channel.unassign";
    public const string OptInChannel = "channel.opt-in";
    public const string OptOutChannel = "channel.opt-out";
    public const string ChannelPermissions = "channel.permissions";
    public const string SynchronizeChannel = "channel.synchronize";
    public const string ListContexts = "channel-context.list";
    public const string GetContext = "channel-context.get";
    public const string CreateContext = "channel-context.create";
    public const string UpdateContext = "channel-context.update";
    public const string DeleteContext = "channel-context.delete";
    public const string AssignContext = "channel-context.assign";
    public const string UnassignContext = "channel-context.unassign";
    public const string ActivateContext = "channel-context.activate";
    public const string DeactivateContext = "channel-context.deactivate";
    public const string SynchronizeContext = "channel-context.synchronize";
    public const string ContextPermissions = "channel-context.permissions";
    public const string ListThreads = "thread.list";
    public const string GetThread = "thread.get";
    public const string CreateThread = "thread.create";
    public const string UpdateThread = "thread.update";
    public const string DeleteThread = "thread.delete";
    public const string ReadHistory = "thread.read-history";
    public const string CommitExchange = "conversation.commit";
}
