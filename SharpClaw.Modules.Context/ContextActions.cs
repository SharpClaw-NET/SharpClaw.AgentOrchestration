using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.Context;

public interface IContextActionExecutor
{
    Task<ContextThreadRecord> CreateThreadAsync(
        RequestPrincipal caller,
        ContextCreateThreadAction action,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null);

    Task<IReadOnlyList<ContextMessageRecord>> ReadHistoryAsync(
        RequestPrincipal caller,
        ContextReadHistoryAction action,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null);

    Task<bool> CommitExchangeAsync(
        RequestPrincipal caller,
        ContextCommitExchangeAction action,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null);

}

public sealed class ContextActionExecutor(ContextStore store) : IContextActionExecutor
{
    public async Task<ContextThreadRecord> CreateThreadAsync(
        RequestPrincipal caller,
        ContextCreateThreadAction action,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
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
            ct: ct,
            hostContext: hostContext);
    }

    public async Task<IReadOnlyList<ContextMessageRecord>> ReadHistoryAsync(
        RequestPrincipal caller,
        ContextReadHistoryAction action,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
    {
        var thread = await store.FindAccessibleThreadAsync(
            caller, action.ChannelId, action.ThreadId, ct, hostContext)
            ?? throw new UnauthorizedAccessException("The thread is missing or inaccessible.");
        return await store.ReadMessagesAsync(thread.ThreadId, action.MaxMessages, ct);
    }

    public Task<bool> CommitExchangeAsync(
        RequestPrincipal caller,
        ContextCommitExchangeAction action,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null) =>
        store.CommitExchangeAsync(caller, action, ct, hostContext);

}

public interface IContextSteeringActionExecutor
{
    Task<ContextSteeringRecord> RecordAsync(
        ActionContext<ContextRecordSteeringAction> actionContext,
        CancellationToken ct = default);

    Task<IReadOnlyList<ContextSteeringRecord>> ListAsync(
        ActionContext<ContextListSteeringAction> actionContext,
        CancellationToken ct = default);
}

public sealed class ContextSteeringActionExecutor(
    ContextStore store,
    HostPermissionActionEntry permission) : IContextSteeringActionExecutor
{
    public async Task<ContextSteeringRecord> RecordAsync(
        ActionContext<ContextRecordSteeringAction> actionContext,
        CancellationToken ct = default)
    {
        using var authorization = store.PushAuthorization(
            new ModuleActionAuthorization<ContextRecordSteeringAction>(actionContext, permission));
        return await store.RecordSteeringAsync(actionContext, ct);
    }

    public async Task<IReadOnlyList<ContextSteeringRecord>> ListAsync(
        ActionContext<ContextListSteeringAction> actionContext,
        CancellationToken ct = default)
    {
        using var authorization = store.PushAuthorization(
            new ModuleActionAuthorization<ContextListSteeringAction>(actionContext, permission));
        return await store.ListSteeringAsync(actionContext.Caller, actionContext.Action, ct);
    }
}

public interface IContextSteeringActionGateway
{
    ValueTask<ContextSteeringRecord> RecordAsync(
        HostActionEntryRequestContext hostContext,
        ContextRecordSteeringAction action,
        CancellationToken ct = default);

    ValueTask<IReadOnlyList<ContextSteeringRecord>> ListAsync(
        HostActionEntryRequestContext hostContext,
        ContextListSteeringAction action,
        CancellationToken ct = default);
}

public sealed class ContextSteeringActionGateway(
    HostModuleActionEntry entry,
    ContextSteeringRecordActionTerminal recordTerminal,
    ContextSteeringListActionTerminal listTerminal) : IContextSteeringActionGateway
{
    public ValueTask<ContextSteeringRecord> RecordAsync(
        HostActionEntryRequestContext hostContext,
        ContextRecordSteeringAction action,
        CancellationToken ct = default) =>
        entry.InvokeAsync(
            ContextSteeringActionDescriptors.Record,
            action,
            hostContext,
            recordTerminal,
            ct);

    public ValueTask<IReadOnlyList<ContextSteeringRecord>> ListAsync(
        HostActionEntryRequestContext hostContext,
        ContextListSteeringAction action,
        CancellationToken ct = default) =>
        entry.InvokeAsync(
            ContextSteeringActionDescriptors.List,
            action,
            hostContext,
            listTerminal,
            ct);
}

public sealed record ContextApiAction(
    string Operation,
    JsonElement Payload);

public sealed class ContextApiActionExecutor(
    ContextStore store,
    HostPermissionActionEntry permission)
{
    public async ValueTask<JsonElement> ExecuteAsync(
        ActionContext<ContextApiAction> actionContext,
        CancellationToken ct = default)
    {
        var action = actionContext.Action;
        var caller = actionContext.Caller;
        using var authorization = store.PushAuthorization(
            new ModuleActionAuthorization<ContextApiAction>(actionContext, permission));
        ArgumentException.ThrowIfNullOrWhiteSpace(action.Operation);
        return action.Operation switch
        {
            ContextApiOperations.ListChannels => await JsonAsync(await store.ListChannelsAsync(caller, ct, null)),
            ContextApiOperations.GetChannel => await JsonAsync(await store.GetChannelForCallerAsync(caller, GuidValue(action.Payload, "channelId"), ct, hostContext: null)),
            ContextApiOperations.CreateChannel => await JsonAsync(await store.CreateChannelAsync(caller, action.Payload, ct, null)),
            ContextApiOperations.UpdateChannel => await JsonAsync(await store.UpdateChannelAsync(caller, action.Payload, ct, null)),
            ContextApiOperations.DeleteChannel => await JsonAsync(await store.DeleteChannelAsync(caller, GuidValue(action.Payload, "channelId"), ct, null)),
            ContextApiOperations.AssignChannel => await JsonAsync(await store.AssignChannelAsync(caller, action.Payload, ct, null)),
            ContextApiOperations.UnassignChannel => await JsonAsync(await store.UnassignChannelAsync(caller, action.Payload, ct, null)),
            ContextApiOperations.OptInChannel => await JsonAsync(await store.SetChannelOptInAsync(caller, action.Payload, true, ct, null)),
            ContextApiOperations.OptOutChannel => await JsonAsync(await store.SetChannelOptInAsync(caller, action.Payload, false, ct, null)),
            ContextApiOperations.ChannelPermissions => await JsonAsync(await store.GetChannelPermissionsAsync(caller, GuidValue(action.Payload, "channelId"), ct, null)),
            ContextApiOperations.SynchronizeChannel => await JsonAsync(await store.SynchronizeChannelAsync(caller, GuidValue(action.Payload, "channelId"), ct, null)),
            ContextApiOperations.ListContexts => await JsonAsync(await store.ListContextsAsync(caller, ct, null)),
            ContextApiOperations.GetContext => await JsonAsync(await store.GetContextForCallerAsync(caller, GuidValue(action.Payload, "contextId"), ct, null)),
            ContextApiOperations.CreateContext => await JsonAsync(await store.CreateContextAsync(caller, action.Payload, ct, null)),
            ContextApiOperations.UpdateContext => await JsonAsync(await store.UpdateContextAsync(caller, action.Payload, ct, null)),
            ContextApiOperations.DeleteContext => await JsonAsync(await store.DeleteContextAsync(caller, GuidValue(action.Payload, "contextId"), ct, null)),
            ContextApiOperations.AssignContext => await JsonAsync(await store.AssignContextAsync(caller, action.Payload, ct, null)),
            ContextApiOperations.UnassignContext => await JsonAsync(await store.UnassignContextAsync(caller, action.Payload, ct, null)),
            ContextApiOperations.ActivateContext => await JsonAsync(await store.SetContextEnabledAsync(caller, action.Payload, true, ct, null)),
            ContextApiOperations.DeactivateContext => await JsonAsync(await store.SetContextEnabledAsync(caller, action.Payload, false, ct, null)),
            ContextApiOperations.SynchronizeContext => await JsonAsync(await store.SynchronizeContextAsync(caller, GuidValue(action.Payload, "contextId"), ct, null)),
            ContextApiOperations.ContextPermissions => await JsonAsync(await store.GetContextPermissionsAsync(caller, GuidValue(action.Payload, "contextId"), ct, null)),
            ContextApiOperations.ListThreads => await JsonAsync(await store.ListAccessibleThreadsAsync(
                caller,
                GuidValue(action.Payload, "channelId"),
                ct,
                null)),
            ContextApiOperations.GetThread => await JsonAsync(await store.GetThreadForCallerAsync(caller, GuidValue(action.Payload, "threadId"), ct, null)),
            ContextApiOperations.CreateThread => await JsonAsync(await store.CreateThreadFromPayloadAsync(caller, action.Payload, ct, null)),
            ContextApiOperations.UpdateThread => await JsonAsync(await store.UpdateThreadAsync(caller, action.Payload, ct, null)),
            ContextApiOperations.DeleteThread => await JsonAsync(await store.DeleteThreadAsync(caller, GuidValue(action.Payload, "threadId"), ct, null)),
            ContextApiOperations.ReadHistory => await JsonAsync(await ReadHistoryAsync(caller, action, ct)),
            ContextApiOperations.CommitExchange => await JsonAsync(await CommitExchangeAsync(caller, action, ct)),
            _ => throw new ArgumentException($"Unknown Context operation '{action.Operation}'.", nameof(action)),
        };
    }

    private async Task<IReadOnlyList<ContextMessageRecord>> ReadHistoryAsync(
        RequestPrincipal caller,
        ContextApiAction action,
        CancellationToken ct)
    {
        var channelId = GuidValue(action.Payload, "channelId");
        var threadId = GuidValue(action.Payload, "threadId");
        var thread = await store.FindAccessibleThreadAsync(caller, channelId, threadId, ct, null)
            ?? throw new UnauthorizedAccessException("The thread is missing or inaccessible.");
        var maxMessages = action.Payload.TryGetProperty("maxMessages", out var max)
            && max.TryGetInt32(out var requested)
            ? requested
            : 50;
        return await store.ReadMessagesAsync(thread.ThreadId, maxMessages, ct);
    }

    private Task<bool> CommitExchangeAsync(
        RequestPrincipal caller,
        ContextApiAction action,
        CancellationToken ct)
    {
        var threadId = GuidValue(action.Payload, "threadId");
        return store.CommitExchangeAsync(
            caller,
            new ContextCommitExchangeAction(
                threadId,
                StringValue(action.Payload, "userMessage") ?? string.Empty,
                StringValue(action.Payload, "assistantMessage") ?? string.Empty),
            ct,
            null);
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
        HostActionEntryRequestContext hostContext,
        string operation,
        JsonElement payload,
        CancellationToken ct = default);
}

public sealed class ContextActionGateway(
    HostModuleActionEntry entry,
    ContextApiActionTerminal terminal) : IContextActionGateway
{
    public ValueTask<JsonElement> ExecuteAsync(
        HostActionEntryRequestContext hostContext,
        string operation,
        JsonElement payload,
        CancellationToken ct = default)
    {
        var action = new ContextApiAction(operation, payload);
        return entry.InvokeAsync(
            ContextModule.ApiDescriptor,
            action,
            hostContext,
            terminal,
            ct);
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
