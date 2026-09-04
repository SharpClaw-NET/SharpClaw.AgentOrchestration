using System.Text;
using System.Text.Json;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.ModuleSDK;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.Context;

public sealed class ContextStore : IConversationStore
{
    public const string SourceId = ContextModule.ModuleIdValue;
    public const string ChannelsStorage = "channels";
    public const string ContextsStorage = "contexts";
    public const string ThreadsStorage = "threads";
    public const string MessagesStorage = "messages";
    public const string SteeringStorage = "steering";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    private readonly ScopedDocumentStore<ContextChannelRecord> _channels;
    private readonly ScopedDocumentStore<ContextRecord> _contexts;
    private readonly ScopedDocumentStore<ContextThreadRecord> _threads;
    private readonly ScopedDocumentStore<ContextMessageRecord> _messages;
    private readonly ScopedDocumentStore<ContextSteeringRecord> _steering;
    private readonly HostAuthorizationEntry _authorizationEntry;
    private readonly AsyncLocal<IAuthorizationClient?> _authorization = new();

    public ContextStore(
        IScopedStorageGateway gateway,
        HostAuthorizationEntry authorizationEntry)
    {
        _channels = new(gateway, SourceId, ChannelsStorage, $"{SourceId}:{ChannelsStorage}", JsonOptions);
        _contexts = new(gateway, SourceId, ContextsStorage, $"{SourceId}:{ContextsStorage}", JsonOptions);
        _threads = new(gateway, SourceId, ThreadsStorage, $"{SourceId}:{ThreadsStorage}", JsonOptions);
        _messages = new(gateway, SourceId, MessagesStorage, $"{SourceId}:{MessagesStorage}", JsonOptions);
        _steering = new(gateway, SourceId, SteeringStorage, $"{SourceId}:{SteeringStorage}", JsonOptions);
        _authorizationEntry = authorizationEntry;
    }

    internal IDisposable PushAuthorization(IAuthorizationClient authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        var previous = _authorization.Value;
        _authorization.Value = authorization;
        return new AuthorizationScope(_authorization, previous);
    }

    internal Task<ContextChannelRecord?> GetChannelAsync(Guid id, CancellationToken ct = default) =>
        _channels.GetAsync(Key(id), ct);

    internal Task<ContextThreadRecord?> GetThreadAsync(Guid id, CancellationToken ct = default) =>
        _threads.GetAsync(Key(id), ct);

    internal Task<ContextRecord?> GetContextAsync(Guid id, CancellationToken ct = default) =>
        _contexts.GetAsync(Key(id), ct);

    internal async Task<ContextSteeringRecord> RecordSteeringAsync(
        ActionContext<ContextRecordSteeringAction> actionContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actionContext);
        ct.ThrowIfCancellationRequested();
        var caller = actionContext.Caller;
        var action = actionContext.Action;
        RequireAuthenticatedAgent(caller);
        var normalized = ValidateAndNormalizeSteeringAction(action);
        var target = await ResolveSteeringTargetAsync(action.ChannelId, action.ThreadId, ct);
        await RequireAllowedAsync(
            caller,
            target.Channel,
            target.Context?.Id,
            ContextAccessCapabilities.CommitExchange,
            ct);
        ct.ThrowIfCancellationRequested();

        var recordId = actionContext.IdempotencyKey;
        if (recordId == Guid.Empty)
            throw new InvalidOperationException(
                "The steering action has no host-issued idempotency identity.");
        var existing = await _steering.GetRecordAsync(Key(recordId), ct);
        if (existing is not null)
        {
            var replay = existing.Value
                ?? throw new InvalidOperationException(
                    $"The steering record '{recordId}' has no stored value.");
            var expectedReplay = replay with
            {
                ChannelId = normalized.ChannelId,
                ThreadId = normalized.ThreadId,
                Source = normalized.Source,
                Category = normalized.Category,
                Summary = normalized.Summary,
                Details = normalized.Details,
                ClientType = normalized.ClientType,
                Caller = caller,
            };
            if (SteeringRecordsEqual(replay, expectedReplay))
                return replay;
            throw new InvalidOperationException(
                $"The steering action '{recordId}' replay conflicts with stored data.");
        }

        var record = new ContextSteeringRecord(
            recordId,
            normalized.ChannelId,
            normalized.ThreadId,
            normalized.Source,
            normalized.Category,
            normalized.Summary,
            normalized.Details,
            normalized.ClientType,
            caller,
            DateTimeOffset.UtcNow);

        try
        {
            await _steering.UpsertAsync(
                Key(record.Id),
                record,
                SteeringIndexes(record),
                expectedRevision: 0,
                ct: ct);
        }
        catch (Exception error) when (error is InvalidOperationException or IOException)
        {
            var raced = await _steering.GetRecordAsync(Key(record.Id), ct);
            if (raced?.Value is { } racedValue
                && SteeringActionMatches(
                    racedValue,
                    record.Id,
                    normalized,
                    caller))
                return racedValue;
            throw;
        }

        var persisted = await _steering.GetRecordAsync(Key(record.Id), ct);
        if (persisted?.Value is not { } persistedValue
            || !SteeringRecordsEqual(persistedValue, record))
            throw new InvalidOperationException(
                $"The steering record '{record.Id}' was not stored as requested.");
        return persistedValue;
    }

    internal async Task<IReadOnlyList<ContextSteeringRecord>> ListSteeringAsync(
        RequestPrincipal caller,
        ContextListSteeringAction action,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
    {
        ct.ThrowIfCancellationRequested();
        RequireAuthenticatedAgent(caller);
        if (action.ChannelId == Guid.Empty)
            throw new ArgumentException("A channel id is required.", nameof(action));
        if (action.ThreadId == Guid.Empty)
            throw new ArgumentException("A thread id cannot be empty.", nameof(action));

        var target = await ResolveSteeringTargetAsync(action.ChannelId, action.ThreadId, ct);
        await RequireAllowedAsync(
            caller,
            target.Channel,
            target.Context?.Id,
            ContextAccessCapabilities.ReadHistory,
            ct,
            hostContext);
        ct.ThrowIfCancellationRequested();
        return await QuerySteeringAsync(
            action.ChannelId,
            action.ThreadId,
            Math.Clamp(action.MaxRecords, 1, 200),
            ct);
    }

    internal async Task<ContextChannelRecord> EnsureConversationChannelAsync(
        RequestPrincipal caller,
        Guid channelId,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
    {
        RequireAuthenticatedAgent(caller);
        var existing = await GetChannelAsync(channelId, ct);
        if (existing is not null)
        {
            await RequireAllowedAsync(
                caller,
                existing,
                existing.ContextId,
                ContextAccessCapabilities.CreateThread,
                ct,
                hostContext);
            return existing;
        }

        var ownerAgentId = ParseAgentId(caller.SubjectId);
        var now = DateTimeOffset.UtcNow;
        var created = new ContextChannelRecord(
            channelId,
            "Conversation",
            ownerAgentId,
            null,
            [],
            [],
            false,
            now,
            now);
        await RequireAllowedAsync(
            caller,
            created,
            null,
            ContextAccessCapabilities.CreateThread,
            ct,
            hostContext);
        await SaveChannelAsync(created, ct);
        return created;
    }

    internal async Task<ContextChannelRecord> SaveChannelAsync(
        ContextChannelRecord channel,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        await _channels.UpsertAsync(Key(channel.Id), channel, new
        {
            ownerAgentId = channel.OwnerAgentId?.ToString("N"),
            contextId = channel.ContextId?.ToString("N"),
            optedIn = channel.CrossThreadOptedIn,
            updatedAt = channel.UpdatedAt,
        }, ct);
        return channel;
    }

    internal async Task<ContextRecord> SaveContextAsync(
        ContextRecord context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        await _contexts.UpsertAsync(Key(context.Id), context, new
        {
            defaultAgentId = context.DefaultAgentId?.ToString("N"),
            updatedAt = context.UpdatedAt,
        }, ct);
        return context;
    }

    internal async Task<IReadOnlyList<ContextChannelRecord>> ListChannelsAsync(
        RequestPrincipal caller,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
    {
        RequireAuthenticatedAgent(caller);
        var channels = await _channels.ListAsync(ct);
        var visible = new List<ContextChannelRecord>(channels.Count);
        foreach (var channel in channels)
        {
            var context = channel.ContextId is { } contextId
                ? await GetContextAsync(contextId, ct)
                : null;
            var decision = await EvaluateAsync(
                channel,
                context,
                ContextAccessCapabilities.ReadHistory,
                ct,
                hostContext);
            if (decision.Allowed)
                visible.Add(channel);
        }

        return visible.OrderByDescending(channel => channel.UpdatedAt).ToArray();
    }

    internal async Task<ContextChannelRecord> GetChannelForCallerAsync(
        RequestPrincipal caller,
        Guid channelId,
        CancellationToken ct = default,
        string capability = ContextAccessCapabilities.ReadHistory,
        HostActionEntryRequestContext? hostContext = null)
    {
        var channel = await GetChannelAsync(channelId, ct)
            ?? throw new InvalidOperationException("The channel was not found.");
        var context = channel.ContextId is { } contextId
            ? await GetContextAsync(contextId, ct)
            : null;
        await RequireAllowedAsync(caller, channel, context?.Id, capability, ct, hostContext);
        return channel;
    }

    internal async Task<ContextChannelRecord> CreateChannelAsync(
        RequestPrincipal caller,
        JsonElement payload,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
    {
        RequireAuthenticatedAgent(caller);
        var now = DateTimeOffset.UtcNow;
        var id = GuidValue(payload, "channelId") ?? Guid.NewGuid();
        var contextId = GuidValue(payload, "contextId");
        var owner = ParseAgentId(caller.SubjectId);
        var channel = new ContextChannelRecord(
            id,
            StringValue(payload, "title") ?? "Conversation",
            owner,
            GuidValue(payload, "defaultContextAgentId"),
            GuidList(payload, "allowedAgentIds"),
            GuidList(payload, "contextAllowedAgentIds"),
            BoolValue(payload, "crossThreadOptedIn"),
            now,
            now)
        {
            ContextId = contextId,
        };
        await RequireAllowedAsync(caller, channel, contextId, ContextAccessCapabilities.CreateThread, ct, hostContext);
        return await SaveChannelAsync(channel, ct);
    }

    internal async Task<ContextChannelRecord> UpdateChannelAsync(
        RequestPrincipal caller,
        JsonElement payload,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
    {
        var channelId = GuidValue(payload, "channelId")
            ?? throw new ArgumentException("channelId is required.");
        var existing = await GetChannelAsync(channelId, ct)
            ?? throw new InvalidOperationException("The channel was not found.");
        await RequireAllowedAsync(caller, existing, existing.ContextId, ContextAccessCapabilities.CommitExchange, ct, hostContext);
        var updated = existing with
        {
            Title = StringValue(payload, "title") ?? existing.Title,
            DefaultContextAgentId = GuidValue(payload, "defaultContextAgentId") ?? existing.DefaultContextAgentId,
            AllowedAgentIds = payload.TryGetProperty("allowedAgentIds", out _)
                ? GuidList(payload, "allowedAgentIds")
                : existing.AllowedAgentIds,
            ContextAllowedAgentIds = payload.TryGetProperty("contextAllowedAgentIds", out _)
                ? GuidList(payload, "contextAllowedAgentIds")
                : existing.ContextAllowedAgentIds,
            UpdatedAt = DateTimeOffset.UtcNow,
        } with
        {
            CrossThreadOptedIn = payload.TryGetProperty("crossThreadOptedIn", out _)
                ? BoolValue(payload, "crossThreadOptedIn")
                : existing.CrossThreadOptedIn,
        };
        return await SaveChannelAsync(updated, ct);
    }

    internal async Task<ContextChannelRecord> DeleteChannelAsync(
        RequestPrincipal caller,
        Guid channelId,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
    {
        var channel = await GetChannelForCallerAsync(caller, channelId, ct, hostContext: hostContext);
        await RequireAllowedAsync(caller, channel, channel.ContextId, ContextAccessCapabilities.CommitExchange, ct, hostContext);
        var threads = await _threads.Query().WhereIndex("channelId").EqualTo(channelId.ToString("N")).ToListAsync(ct);
        foreach (var thread in threads)
            await _threads.DeleteAsync(Key(thread.Id), ct);
        await _channels.DeleteAsync(Key(channelId), ct);
        return channel;
    }

    internal async Task<ContextChannelRecord> AssignChannelAsync(
        RequestPrincipal caller,
        JsonElement payload,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null) =>
        await ChangeChannelAssignmentAsync(caller, payload, assign: true, ct, hostContext);

    internal async Task<ContextChannelRecord> UnassignChannelAsync(
        RequestPrincipal caller,
        JsonElement payload,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null) =>
        await ChangeChannelAssignmentAsync(caller, payload, assign: false, ct, hostContext);

    internal async Task<ContextChannelRecord> SetChannelOptInAsync(
        RequestPrincipal caller,
        JsonElement payload,
        bool optedIn,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
    {
        var channelId = GuidValue(payload, "channelId")
            ?? throw new ArgumentException("channelId is required.");
        var channel = await GetChannelForCallerAsync(
            caller,
            channelId,
            ct,
            ContextAccessCapabilities.ReadCrossThreadHistory,
            hostContext);
        await RequireAllowedAsync(caller, channel, channel.ContextId, ContextAccessCapabilities.CommitExchange, ct, hostContext);
        return await SaveChannelAsync(channel with
        {
            CrossThreadOptedIn = optedIn,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, ct);
    }

    internal async Task<object> GetChannelPermissionsAsync(
        RequestPrincipal caller,
        Guid channelId,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
    {
        var channel = await GetChannelForCallerAsync(caller, channelId, ct, hostContext: hostContext);
        return new
        {
            channel.Id,
            channel.OwnerAgentId,
            channel.DefaultContextAgentId,
            channel.AllowedAgentIds,
            channel.ContextAllowedAgentIds,
            channel.CrossThreadOptedIn,
        };
    }

    internal Task<ContextChannelRecord> SynchronizeChannelAsync(
        RequestPrincipal caller,
        Guid channelId,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null) =>
        GetChannelForCallerAsync(caller, channelId, ct, hostContext: hostContext);

    internal async Task<IReadOnlyList<ContextRecord>> ListContextsAsync(
        RequestPrincipal caller,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
    {
        RequireAuthenticatedAgent(caller);
        var contexts = await _contexts.ListAsync(ct);
        var visible = new List<ContextRecord>(contexts.Count);
        foreach (var context in contexts)
        {
            try
            {
                await RequireContextAccessAsync(
                    caller,
                    context,
                    ContextAccessCapabilities.ReadHistory,
                    ct,
                    hostContext);
                visible.Add(context);
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return visible.OrderByDescending(context => context.UpdatedAt).ToArray();
    }

    internal async Task<ContextRecord> GetContextForCallerAsync(
        RequestPrincipal caller,
        Guid contextId,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
    {
        var context = await GetContextAsync(contextId, ct)
            ?? throw new InvalidOperationException("The context was not found.");
        await RequireContextAccessAsync(caller, context, ContextAccessCapabilities.ReadHistory, ct, hostContext);
        return context;
    }

    internal async Task<ContextRecord> CreateContextAsync(
        RequestPrincipal caller,
        JsonElement payload,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
    {
        RequireAuthenticatedAgent(caller);
        var callerId = ParseAgentId(caller.SubjectId);
        var now = DateTimeOffset.UtcNow;
        var context = new ContextRecord(
            GuidValue(payload, "contextId") ?? Guid.NewGuid(),
            StringValue(payload, "name") ?? "Context",
            GuidValue(payload, "defaultAgentId") ?? callerId,
            GuidList(payload, "allowedAgentIds"),
            now,
            now)
        {
            Enabled = true,
        };
        await RequireContextAccessAsync(caller, context, ContextAccessCapabilities.CreateThread, ct, hostContext);
        return await SaveContextAsync(context, ct);
    }

    internal async Task<ContextRecord> UpdateContextAsync(
        RequestPrincipal caller,
        JsonElement payload,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
    {
        var contextId = GuidValue(payload, "contextId")
            ?? throw new ArgumentException("contextId is required.");
        var existing = await GetContextAsync(contextId, ct)
            ?? throw new InvalidOperationException("The context was not found.");
        await RequireContextAccessAsync(caller, existing, ContextAccessCapabilities.CommitExchange, ct, hostContext);
        var updated = existing with
        {
            Name = StringValue(payload, "name") ?? existing.Name,
            DefaultAgentId = GuidValue(payload, "defaultAgentId") ?? existing.DefaultAgentId,
            AllowedAgentIds = payload.TryGetProperty("allowedAgentIds", out _)
                ? GuidList(payload, "allowedAgentIds")
                : existing.AllowedAgentIds,
            UpdatedAt = DateTimeOffset.UtcNow,
            Enabled = payload.TryGetProperty("enabled", out var enabled)
                ? enabled.ValueKind != JsonValueKind.False
                : existing.Enabled,
        };
        return await SaveContextAsync(updated, ct);
    }

    internal async Task<ContextRecord> DeleteContextAsync(
        RequestPrincipal caller,
        Guid contextId,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
    {
        var context = await GetContextForCallerAsync(caller, contextId, ct, hostContext);
        await RequireContextAccessAsync(caller, context, ContextAccessCapabilities.CommitExchange, ct, hostContext);
        await _contexts.DeleteAsync(Key(contextId), ct);
        return context;
    }

    internal Task<ContextRecord> AssignContextAsync(
        RequestPrincipal caller,
        JsonElement payload,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null) =>
        ChangeContextAssignmentAsync(caller, payload, assign: true, ct, hostContext);

    internal Task<ContextRecord> UnassignContextAsync(
        RequestPrincipal caller,
        JsonElement payload,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null) =>
        ChangeContextAssignmentAsync(caller, payload, assign: false, ct, hostContext);

    internal async Task<ContextRecord> SetContextEnabledAsync(
        RequestPrincipal caller,
        JsonElement payload,
        bool enabled,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
    {
        var contextId = GuidValue(payload, "contextId")
            ?? throw new ArgumentException("contextId is required.");
        var context = await GetContextForCallerAsync(caller, contextId, ct, hostContext);
        await RequireContextAccessAsync(caller, context, ContextAccessCapabilities.CommitExchange, ct, hostContext);
        return await SaveContextAsync(context with { Enabled = enabled, UpdatedAt = DateTimeOffset.UtcNow }, ct);
    }

    internal async Task<object> GetContextPermissionsAsync(
        RequestPrincipal caller,
        Guid contextId,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
    {
        var context = await GetContextForCallerAsync(caller, contextId, ct, hostContext);
        return new { context.Id, context.DefaultAgentId, context.AllowedAgentIds, context.Enabled };
    }

    internal Task<ContextRecord> SynchronizeContextAsync(
        RequestPrincipal caller,
        Guid contextId,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null) =>
        GetContextForCallerAsync(caller, contextId, ct, hostContext);

    internal async Task<IReadOnlyList<ContextThreadRecord>> ListThreadsForCallerAsync(
        RequestPrincipal caller,
        JsonElement payload,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
    {
        var channelId = GuidValue(payload, "channelId")
            ?? throw new ArgumentException("channelId is required.");
        var channel = await GetChannelForCallerAsync(
            caller,
            channelId,
            ct,
            ContextAccessCapabilities.ReadCrossThreadHistory,
            hostContext);
        var threads = await _threads.Query()
            .WhereIndex("channelId").EqualTo(channel.Id.ToString("N"))
            .OrderByIndexDescending("updatedAt")
            .ToListAsync(ct);
        return threads;
    }

    internal async Task<ContextThreadRecord> GetThreadForCallerAsync(
        RequestPrincipal caller,
        Guid threadId,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
    {
        await RequireThreadAccessAsync(caller, threadId, ContextAccessCapabilities.ReadHistory, ct, hostContext);
        return await GetThreadAsync(threadId, ct)
            ?? throw new InvalidOperationException("The thread was not found.");
    }

    internal Task<ContextThreadRecord> CreateThreadFromPayloadAsync(
        RequestPrincipal caller,
        JsonElement payload,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
    {
        var channelId = GuidValue(payload, "channelId")
            ?? throw new ArgumentException("channelId is required.");
        var name = StringValue(payload, "name") ?? "Thread";
        return CreateThreadAsync(caller, channelId, name, GuidValue(payload, "contextId"), ct: ct, hostContext: hostContext);
    }

    internal async Task<ContextThreadRecord> UpdateThreadAsync(
        RequestPrincipal caller,
        JsonElement payload,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
    {
        var threadId = GuidValue(payload, "threadId")
            ?? throw new ArgumentException("threadId is required.");
        var existing = await GetThreadForCallerAsync(caller, threadId, ct, hostContext);
        var updated = existing with
        {
            Name = StringValue(payload, "name") ?? existing.Name,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await _threads.UpsertAsync(Key(updated.Id), updated, new
        {
            channelId = updated.ChannelId.ToString("N"),
            contextId = updated.ContextId?.ToString("N"),
            updatedAt = updated.UpdatedAt,
        }, ct);
        return updated;
    }

    internal async Task<ContextThreadRecord> DeleteThreadAsync(
        RequestPrincipal caller,
        Guid threadId,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
    {
        var thread = await GetThreadForCallerAsync(caller, threadId, ct, hostContext);
        await _threads.DeleteAsync(Key(thread.Id), ct);
        var messages = await _messages.Query().WhereIndex("threadId").EqualTo(thread.Id.ToString("N")).ToListAsync(ct);
        foreach (var message in messages)
            await _messages.DeleteAsync(Key(message.Id), ct);
        return thread;
    }

    internal async Task<ContextThreadRecord> CreateThreadAsync(
        RequestPrincipal caller,
        Guid channelId,
        string name,
        Guid? contextId = null,
        Guid? threadId = null,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
    {
        RequireAuthenticatedAgent(caller);
        if (channelId == Guid.Empty)
            throw new ArgumentException("A thread requires a channel id.", nameof(channelId));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A thread requires a name.", nameof(name));

        var channel = await GetChannelAsync(channelId, ct)
            ?? throw new InvalidOperationException("The channel was not found.");
        if (channel.ContextId is { } channelContextId
            && contextId is { } requestedContextId
            && channelContextId != requestedContextId)
        {
            throw new InvalidOperationException("The thread context does not match the channel context.");
        }

        var resolvedContextId = contextId ?? channel.ContextId;
        await RequireAllowedAsync(
            caller,
            channel,
            resolvedContextId,
            ContextAccessCapabilities.CreateThread,
            ct,
            hostContext);

        var now = DateTimeOffset.UtcNow;
        var thread = new ContextThreadRecord(
            threadId.GetValueOrDefault(Guid.NewGuid()),
            name.Trim(),
            channelId,
            resolvedContextId,
            now,
            now);
        await _threads.UpsertAsync(Key(thread.Id), thread, new
        {
            channelId = thread.ChannelId.ToString("N"),
            contextId = thread.ContextId?.ToString("N"),
            updatedAt = thread.UpdatedAt,
        }, ct);
        return thread;
    }

    internal async Task AppendMessageAsync(
        ContextMessageRecord message,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        await _messages.UpsertAsync(Key(message.Id), message, new
        {
            threadId = message.ThreadId.ToString("N"),
            channelId = message.ChannelId.ToString("N"),
            createdAt = message.CreatedAt,
        }, ct);
    }

    internal async Task<IReadOnlyList<ContextThreadSummary>> ListAccessibleThreadsAsync(
        RequestPrincipal principal,
        Guid currentChannelId,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
    {
        var channels = await _channels.ListAsync(ct);
        var summaries = new List<ContextThreadSummary>();
        foreach (var channel in channels.Where(channel => channel.Id != currentChannelId))
        {
            var threads = await _threads.Query()
                .WhereIndex("channelId").EqualTo(channel.Id.ToString("N"))
                .OrderByIndexDescending("updatedAt")
                .ToListAsync(ct);
            foreach (var thread in threads)
            {
                var context = await ResolveContextAsync(channel, thread, ct);
                var decision = await EvaluateAsync(
                    channel,
                    context,
                    ContextAccessCapabilities.ReadCrossThreadHistory,
                    ct,
                    hostContext);
                if (decision.Allowed)
                    summaries.Add(new ContextThreadSummary(
                        thread.Id,
                        thread.Name,
                        thread.ChannelId,
                        channel.Title,
                        thread.UpdatedAt));
            }
        }

        return summaries
            .OrderByDescending(thread => thread.UpdatedAt)
            .ToArray();
    }

    internal async Task<ContextThreadSummary?> FindAccessibleThreadAsync(
        RequestPrincipal principal,
        Guid currentChannelId,
        Guid threadId,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
    {
        var thread = await GetThreadAsync(threadId, ct);
        if (thread is null || thread.ChannelId == currentChannelId)
            return null;

        var channel = await GetChannelAsync(thread.ChannelId, ct);
        if (channel is null)
            return null;
        var context = await ResolveContextAsync(channel, thread, ct);
        var decision = await EvaluateAsync(
            channel,
            context,
            ContextAccessCapabilities.ReadCrossThreadHistory,
            ct,
            hostContext);
        return decision.Allowed
            ? new ContextThreadSummary(
                thread.Id,
                thread.Name,
                thread.ChannelId,
                channel.Title,
                thread.UpdatedAt)
            : null;
    }

    internal async Task<IReadOnlyList<ContextMessageRecord>> ReadMessagesAsync(
        Guid threadId,
        int maxMessages,
        CancellationToken ct = default)
    {
        maxMessages = Math.Clamp(maxMessages, 1, 200);
        var records = await _messages.Query()
            .WhereIndex("threadId").EqualTo(threadId.ToString("N"))
            .OrderByIndexDescending("createdAt")
            .Take(maxMessages)
            .ToListAsync(ct);
        return records.OrderBy(message => message.CreatedAt).ToArray();
    }

    internal async Task<IReadOnlyList<ContextMessageRecord>> ReadAllMessagesAsync(
        Guid threadId,
        CancellationToken ct = default)
    {
        var records = await _messages.Query()
            .WhereIndex("threadId").EqualTo(threadId.ToString("N"))
            .OrderByIndex("createdAt")
            .ToListAsync(ct);
        return records.OrderBy(message => message.CreatedAt).ToArray();
    }

    internal async ValueTask<IReadOnlyList<ChatCompletionMessage>> LoadHistoryAsync(
        RequestPrincipal caller,
        Guid conversationId,
        CancellationToken ct,
        HostActionEntryRequestContext? hostContext = null)
    {
        await RequireThreadAccessAsync(
            caller,
            conversationId,
            ContextAccessCapabilities.ReadHistory,
            ct,
            hostContext);
        var messages = await ReadAllMessagesAsync(conversationId, ct);
        return messages
            .Select(message => new ChatCompletionMessage(message.Role, message.Content))
            .ToArray();
    }

    internal async Task<IReadOnlyList<ContextSteeringRecord>> LoadSteeringForThreadAsync(
        RequestPrincipal caller,
        Guid threadId,
        int maxRecords,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
    {
        ct.ThrowIfCancellationRequested();
        RequireAuthenticatedAgent(caller);
        if (threadId == Guid.Empty)
            throw new ArgumentException("A thread id is required.", nameof(threadId));

        var thread = await GetThreadAsync(threadId, ct)
            ?? throw new InvalidOperationException("The conversation thread was not found.");
        var target = await ResolveSteeringTargetAsync(thread.ChannelId, threadId, ct);
        await RequireAllowedAsync(
            caller,
            target.Channel,
            target.Context?.Id,
            ContextAccessCapabilities.ReadHistory,
            ct,
            hostContext);
        ct.ThrowIfCancellationRequested();

        var limit = Math.Clamp(maxRecords, 1, 200);
        var channelRecords = await QuerySteeringScopeAsync(
            thread.ChannelId,
            null,
            limit,
            ct);
        var threadRecords = await QuerySteeringScopeAsync(
            thread.ChannelId,
            threadId,
            limit,
            ct);
        return MergeSteeringRecords(channelRecords, threadRecords, limit);
    }

    internal static string FormatSteering(ContextSteeringRecord record) =>
        JsonSerializer.Serialize(new
        {
            id = record.Id,
            channelId = record.ChannelId,
            threadId = record.ThreadId,
            source = record.Source,
            category = record.Category,
            summary = record.Summary,
            details = record.Details,
            clientType = record.ClientType,
            caller = new
            {
                subjectId = record.Caller.SubjectId,
                displayName = record.Caller.DisplayName,
                roles = record.Caller.Roles?.OrderBy(role => role, StringComparer.Ordinal).ToArray(),
                isAuthenticated = record.Caller.IsAuthenticated,
            },
            createdAt = record.CreatedAt,
        }, JsonOptions);

    public ValueTask<IReadOnlyList<ChatCompletionMessage>> LoadHistoryAsync(
        Guid conversationId,
        ChatOperationContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var authorization = PushAuthorization(
            new ChatAuthorizationClient(context, _authorizationEntry));
        return LoadHistoryAsync(context.Caller, conversationId, ct);
    }

    public async ValueTask CommitExchangeAsync(
        ChatExchange exchange,
        ChatOperationContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var caller = context.Caller;
        RequireAuthenticatedAgent(caller);
        using var authorization = PushAuthorization(
            new ChatAuthorizationClient(context, _authorizationEntry));
        var thread = await GetThreadAsync(exchange.Turn.Conversation.ConversationId, ct)
            ?? throw new InvalidOperationException("The conversation thread was not found.");
        await RequireThreadAccessAsync(
            caller,
            thread.Id,
            ContextAccessCapabilities.CommitExchange,
            ct);
        await WriteExchangeAsync(thread, caller, exchange.UserMessage, exchange.Completion.Content, ct);
    }

    internal async Task<bool> CommitExchangeAsync(
        RequestPrincipal caller,
        ContextCommitExchangeAction action,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
    {
        RequireAuthenticatedAgent(caller);
        if (action.ThreadId == Guid.Empty)
            throw new ArgumentException("A thread id is required.", nameof(action));
        var thread = await GetThreadAsync(action.ThreadId, ct)
            ?? throw new InvalidOperationException("The conversation thread was not found.");
        await RequireThreadAccessAsync(
            caller,
            thread.Id,
            ContextAccessCapabilities.CommitExchange,
            ct,
            hostContext);
        await WriteExchangeAsync(thread, caller, action.UserMessage, action.AssistantMessage, ct);
        return true;
    }

    internal async ValueTask<AccessDecision> AuthorizeCommitAsync(
        RequestPrincipal caller,
        ContextCommitExchangeAction action,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null)
    {
        if (!caller.IsAuthenticated)
            return AccessDecision.Deny("unauthenticated", "Authentication is required.");
        return await AuthorizeThreadAsync(
            caller,
            action.ThreadId,
            ContextAccessCapabilities.CommitExchange,
            ct,
            hostContext);
    }

    internal Task<AccessDecision> AuthorizeConversationAsync(
        RequestPrincipal caller,
        Guid conversationId,
        string capability,
        CancellationToken ct = default,
        HostActionEntryRequestContext? hostContext = null) =>
        AuthorizeThreadAsync(caller, conversationId, capability, ct, hostContext);

    private async Task WriteExchangeAsync(
        ContextThreadRecord thread,
        RequestPrincipal caller,
        string userMessage,
        string? assistantMessage,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await AppendMessageAsync(new ContextMessageRecord(
            Guid.NewGuid(),
            thread.Id,
            thread.ChannelId,
            "user",
            userMessage,
            caller.SubjectId,
            now,
            now), ct);
        if (!string.IsNullOrWhiteSpace(assistantMessage))
        {
            await AppendMessageAsync(new ContextMessageRecord(
                Guid.NewGuid(),
                thread.Id,
                thread.ChannelId,
                "assistant",
                assistantMessage!,
                "assistant",
                now,
                now), ct);
        }
        await _threads.UpsertAsync(Key(thread.Id), thread with { UpdatedAt = now }, new
        {
            channelId = thread.ChannelId.ToString("N"),
            contextId = thread.ContextId?.ToString("N"),
            updatedAt = now,
        }, ct);
    }

    private async Task RequireThreadAccessAsync(
        RequestPrincipal caller,
        Guid threadId,
        string capability,
        CancellationToken ct,
        HostActionEntryRequestContext? hostContext = null)
    {
        var decision = await AuthorizeThreadAsync(caller, threadId, capability, ct, hostContext);
        if (!decision.Allowed)
            throw new UnauthorizedAccessException($"{decision.Code}: {decision.Message}");
    }

    private async Task<AccessDecision> AuthorizeThreadAsync(
        RequestPrincipal caller,
        Guid threadId,
        string capability,
        CancellationToken ct,
        HostActionEntryRequestContext? hostContext = null)
    {
        if (!caller.IsAuthenticated)
            return AccessDecision.Deny("unauthenticated", "Authentication is required.");
        var thread = await GetThreadAsync(threadId, ct);
        if (thread is null)
            return AccessDecision.Deny("thread_not_found", "The conversation thread was not found.");
        var channel = await GetChannelAsync(thread.ChannelId, ct);
        if (channel is null)
            return AccessDecision.Deny("channel_not_found", "The conversation channel was not found.");
        var context = await ResolveContextAsync(channel, thread, ct);
        return await EvaluateAsync(channel, context, capability, ct, hostContext);
    }

    private async Task<ContextChannelRecord> ChangeChannelAssignmentAsync(
        RequestPrincipal caller,
        JsonElement payload,
        bool assign,
        CancellationToken ct,
        HostActionEntryRequestContext? hostContext = null)
    {
        var channelId = GuidValue(payload, "channelId")
            ?? throw new ArgumentException("channelId is required.");
        var agentId = GuidValue(payload, "agentId")
            ?? throw new ArgumentException("agentId is required.");
        var channel = await GetChannelForCallerAsync(caller, channelId, ct, hostContext: hostContext);
        await RequireAllowedAsync(caller, channel, channel.ContextId, ContextAccessCapabilities.CommitExchange, ct, hostContext);
        var agents = channel.AllowedAgentIds
            .Where(id => id != agentId)
            .Concat(assign ? [agentId] : [])
            .Distinct()
            .ToArray();
        return await SaveChannelAsync(channel with
        {
            AllowedAgentIds = agents,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, ct);
    }

    private async Task<ContextRecord> ChangeContextAssignmentAsync(
        RequestPrincipal caller,
        JsonElement payload,
        bool assign,
        CancellationToken ct,
        HostActionEntryRequestContext? hostContext = null)
    {
        var contextId = GuidValue(payload, "contextId")
            ?? throw new ArgumentException("contextId is required.");
        var agentId = GuidValue(payload, "agentId")
            ?? throw new ArgumentException("agentId is required.");
        var context = await GetContextForCallerAsync(caller, contextId, ct, hostContext);
        await RequireContextAccessAsync(caller, context, ContextAccessCapabilities.CommitExchange, ct, hostContext);
        var agents = context.AllowedAgentIds
            .Where(id => id != agentId)
            .Concat(assign ? [agentId] : [])
            .Distinct()
            .ToArray();
        return await SaveContextAsync(context with
        {
            AllowedAgentIds = agents,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, ct);
    }

    private async Task RequireContextAccessAsync(
        RequestPrincipal caller,
        ContextRecord context,
        string capability,
        CancellationToken ct,
        HostActionEntryRequestContext? hostContext = null)
    {
        RequireAuthenticatedAgent(caller);
        if (!context.Enabled)
            throw new UnauthorizedAccessException("The context is disabled.");
        var decision = await EvaluatePermissionAsync(new ContextAccessRequest(
            Guid.Empty,
            null,
            [],
            context.DefaultAgentId,
            context.AllowedAgentIds,
            false,
            context.Id,
            capability), hostContext, ct);
        if (!decision.Allowed && !IsAdministrator(caller))
            throw new UnauthorizedAccessException($"{decision.Code}: {decision.Message}");
    }

    private async Task RequireAllowedAsync(
        RequestPrincipal principal,
        ContextChannelRecord channel,
        Guid? contextId,
        string capability,
        CancellationToken ct,
        HostActionEntryRequestContext? hostContext = null)
    {
        var context = contextId is { } id
            ? await GetContextAsync(id, ct)
            : null;
        var decision = await EvaluateAsync(channel, context, capability, ct, hostContext);
        if (!decision.Allowed)
            throw new UnauthorizedAccessException($"{decision.Code}: {decision.Message}");
    }

    private async ValueTask<AccessDecision> EvaluateAsync(
        ContextChannelRecord channel,
        ContextRecord? context,
        string capability,
        CancellationToken ct,
        HostActionEntryRequestContext? hostContext = null)
    {
        var decision = await EvaluatePermissionAsync(new ContextAccessRequest(
            channel.Id,
            channel.OwnerAgentId,
            channel.AllowedAgentIds,
            context?.DefaultAgentId ?? channel.DefaultContextAgentId,
            channel.ContextAllowedAgentIds
                .Concat(context?.AllowedAgentIds ?? [])
                .Distinct()
                .ToArray(),
            channel.CrossThreadOptedIn,
            context?.Id ?? channel.ContextId,
            capability), hostContext, ct);
        return decision.Allowed
            ? AccessDecision.Allow(decision.Code)
            : AccessDecision.Deny(decision.Code, decision.Message);
    }

    private ValueTask<AuthorizationDecision> EvaluatePermissionAsync(
        ContextAccessRequest request,
        HostActionEntryRequestContext? hostContext,
        CancellationToken ct)
    {
        if (_authorization.Value is { } authorization)
            return authorization.EvaluateAsync(AuthorizationRequestFactory.ForContext(request), ct);
        return _authorizationEntry.EvaluateAsync(
            RequireHostContext(hostContext),
            AuthorizationRequestFactory.ForContext(request),
            ct);
    }

    private static HostActionEntryRequestContext RequireHostContext(
        HostActionEntryRequestContext? hostContext) =>
        hostContext
        ?? throw new InvalidOperationException(
            "A host action entry context is required for Context permission evaluation.");

    private sealed class AuthorizationScope(
        AsyncLocal<IAuthorizationClient?> slot,
        IAuthorizationClient? previous) : IDisposable
    {
        public void Dispose() => slot.Value = previous;
    }

    private async Task<ContextRecord?> ResolveContextAsync(
        ContextChannelRecord channel,
        ContextThreadRecord thread,
        CancellationToken ct)
    {
        var contextId = thread.ContextId ?? channel.ContextId;
        return contextId is { } id
            ? await GetContextAsync(id, ct)
            : null;
    }

    private async Task<(ContextChannelRecord Channel, ContextRecord? Context)> ResolveSteeringTargetAsync(
        Guid channelId,
        Guid? threadId,
        CancellationToken ct)
    {
        if (channelId == Guid.Empty)
            throw new ArgumentException("A channel id is required.", nameof(channelId));
        if (threadId == Guid.Empty)
            throw new ArgumentException("A thread id cannot be empty.", nameof(threadId));

        var channel = await GetChannelAsync(channelId, ct)
            ?? throw new InvalidOperationException("The steering channel was not found.");
        if (threadId is { } requestedThreadId)
        {
            var thread = await GetThreadAsync(requestedThreadId, ct)
                ?? throw new InvalidOperationException("The steering thread was not found.");
            if (thread.ChannelId != channelId)
                throw new InvalidOperationException(
                    "The steering thread does not belong to the specified channel.");
            return (channel, await ResolveContextAsync(channel, thread, ct));
        }

        var context = channel.ContextId is { } contextId
            ? await GetContextAsync(contextId, ct)
            : null;
        return (channel, context);
    }

    private async Task<IReadOnlyList<ContextSteeringRecord>> QuerySteeringAsync(
        Guid channelId,
        Guid? threadId,
        int maxRecords,
        CancellationToken ct)
    {
        return await QuerySteeringScopeAsync(channelId, threadId, maxRecords, ct);
    }

    private async Task<IReadOnlyList<ContextSteeringRecord>> QuerySteeringScopeAsync(
        Guid channelId,
        Guid? threadId,
        int maxRecords,
        CancellationToken ct)
    {
        var scope = threadId is null ? "channel" : "thread";
        var query = _steering.Query()
            .WhereIndex("channelId").EqualTo(channelId.ToString("N"))
            .WhereIndex("scope").EqualTo(scope);
        if (threadId is { } targetThreadId)
            query = query.WhereIndex("threadId").EqualTo(targetThreadId.ToString("N"));
        var records = await query
            .OrderByIndexDescending("createdAtId")
            .Take(maxRecords)
            .ToListAsync(ct);
        return records
            .OrderByDescending(record => record.CreatedAt)
            .ThenByDescending(record => record.Id.ToString("N"), StringComparer.Ordinal)
            .Take(maxRecords)
            .ToArray();
    }

    private static IReadOnlyList<ContextSteeringRecord> MergeSteeringRecords(
        IReadOnlyList<ContextSteeringRecord> channelRecords,
        IReadOnlyList<ContextSteeringRecord> threadRecords,
        int maxRecords)
    {
        return channelRecords.Concat(threadRecords)
            .OrderByDescending(record => record.CreatedAt)
            .ThenByDescending(record => record.Id.ToString("N"), StringComparer.Ordinal)
            .Take(maxRecords)
            .OrderBy(record => record.CreatedAt)
            .ThenBy(record => record.Id.ToString("N"), StringComparer.Ordinal)
            .ToArray();
    }

    private static object SteeringIndexes(ContextSteeringRecord record) => new
    {
        channelId = record.ChannelId.ToString("N"),
        threadId = record.ThreadId?.ToString("N"),
        scope = record.ThreadId is null ? "channel" : "thread",
        source = record.Source,
        category = record.Category,
        createdAt = record.CreatedAt,
        createdAtId = SteeringOrderKey(record),
    };

    private static string SteeringOrderKey(ContextSteeringRecord record) =>
        $"{record.CreatedAt.UtcDateTime.Ticks:D19}:{record.Id:N}";

    private static ContextRecordSteeringAction ValidateAndNormalizeSteeringAction(
        ContextRecordSteeringAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.ChannelId == Guid.Empty)
            throw new ArgumentException("A steering record requires a channel id.", nameof(action));
        if (action.ThreadId == Guid.Empty)
            throw new ArgumentException("A steering thread id cannot be empty.", nameof(action));
        var normalized = NormalizeSteeringAction(action);
        EnsureTextLength(normalized.Source, 128, nameof(action.Source));
        EnsureTextLength(normalized.Category, 128, nameof(action.Category));
        EnsureTextLength(normalized.Summary, 8000, nameof(action.Summary));
        EnsureTextLength(normalized.Details, 16000, nameof(action.Details));
        EnsureTextLength(normalized.ClientType, 128, nameof(action.ClientType));
        return normalized;
    }

    private static ContextRecordSteeringAction NormalizeSteeringAction(
        ContextRecordSteeringAction action) =>
        action with
        {
            Source = NormalizeRequired(action.Source, nameof(action.Source)),
            Category = NormalizeRequired(action.Category, nameof(action.Category)),
            Summary = NormalizeRequired(action.Summary, nameof(action.Summary)),
            Details = NormalizeOptional(action.Details),
            ClientType = NormalizeRequired(action.ClientType, nameof(action.ClientType)),
        };

    private static string NormalizeRequired(string? value, string name)
    {
        var normalized = value?.Normalize(NormalizationForm.FormC).Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? throw new ArgumentException($"A steering record requires {name}.", name)
            : normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        if (value is null)
            return null;
        var normalized = value.Normalize(NormalizationForm.FormC).Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private static void EnsureTextLength(string? value, int maxLength, string name)
    {
        if (value is not null && value.Length > maxLength)
            throw new ArgumentException(
                $"The steering field {name} cannot exceed {maxLength} characters.",
                name);
    }

    private static bool SteeringRecordsEqual(
        ContextSteeringRecord left,
        ContextSteeringRecord right) =>
        left.Id == right.Id
        && left.ChannelId == right.ChannelId
        && left.ThreadId == right.ThreadId
        && string.Equals(left.Source, right.Source, StringComparison.Ordinal)
        && string.Equals(left.Category, right.Category, StringComparison.Ordinal)
        && string.Equals(left.Summary, right.Summary, StringComparison.Ordinal)
        && string.Equals(left.Details, right.Details, StringComparison.Ordinal)
        && string.Equals(left.ClientType, right.ClientType, StringComparison.Ordinal)
        && left.CreatedAt == right.CreatedAt
        && PrincipalsEqual(left.Caller, right.Caller);

    private static bool SteeringActionMatches(
        ContextSteeringRecord stored,
        Guid id,
        ContextRecordSteeringAction action,
        RequestPrincipal caller) =>
        stored.Id == id
        && stored.ChannelId == action.ChannelId
        && stored.ThreadId == action.ThreadId
        && string.Equals(stored.Source, action.Source, StringComparison.Ordinal)
        && string.Equals(stored.Category, action.Category, StringComparison.Ordinal)
        && string.Equals(stored.Summary, action.Summary, StringComparison.Ordinal)
        && string.Equals(stored.Details, action.Details, StringComparison.Ordinal)
        && string.Equals(stored.ClientType, action.ClientType, StringComparison.Ordinal)
        && PrincipalsEqual(stored.Caller, caller);

    private static bool PrincipalsEqual(RequestPrincipal left, RequestPrincipal right)
    {
        var leftRoles = left.Roles ?? new HashSet<string>();
        var rightRoles = right.Roles ?? new HashSet<string>();
        return string.Equals(left.SubjectId, right.SubjectId, StringComparison.Ordinal)
            && string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal)
            && left.IsAuthenticated == right.IsAuthenticated
            && leftRoles.SetEquals(rightRoles);
    }

    private static void RequireAuthenticatedAgent(RequestPrincipal? caller)
    {
        if (caller is null
            || !caller.IsAuthenticated
            || !Guid.TryParse(caller.SubjectId, out var agentId)
            || agentId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("An authenticated agent caller is required.");
        }
    }

    private static Guid ParseAgentId(string subjectId) =>
        Guid.TryParse(subjectId, out var id) ? id : Guid.Empty;

    private static bool IsAdministrator(RequestPrincipal caller) =>
        caller.Roles?.Any(role => role.Equals("admin", StringComparison.OrdinalIgnoreCase)
            || role.Equals("administrator", StringComparison.OrdinalIgnoreCase)) == true;

    private static Guid? GuidValue(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && Guid.TryParse(value.GetString(), out var id)
            ? id
            : null;

    private static bool BoolValue(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static string? StringValue(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<Guid> GuidList(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String
                    && Guid.TryParse(item.GetString(), out _))
                .Select(item => Guid.Parse(item.GetString()!))
                .Distinct()
                .ToArray()
            : [];

    private static string Key(Guid id) => id.ToString("N");
}
