using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.Context;

public sealed class ContextStore : IConversationStore
{
    public const string ModuleId = ContextModule.ModuleIdValue;
    public const string ChannelsStorage = "channels";
    public const string ContextsStorage = "contexts";
    public const string ThreadsStorage = "threads";
    public const string MessagesStorage = "messages";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    private readonly ModuleDocumentStore<ContextChannelRecord> _channels;
    private readonly ModuleDocumentStore<ContextRecord> _contexts;
    private readonly ModuleDocumentStore<ContextThreadRecord> _threads;
    private readonly ModuleDocumentStore<ContextMessageRecord> _messages;
    private readonly IContextAccessPolicy _policy;

    public ContextStore(
        IModuleStorageGateway gateway,
        IContextAccessPolicy policy)
    {
        _channels = new(gateway, ModuleId, ChannelsStorage, $"{ModuleId}:{ChannelsStorage}", JsonOptions);
        _contexts = new(gateway, ModuleId, ContextsStorage, $"{ModuleId}:{ContextsStorage}", JsonOptions);
        _threads = new(gateway, ModuleId, ThreadsStorage, $"{ModuleId}:{ThreadsStorage}", JsonOptions);
        _messages = new(gateway, ModuleId, MessagesStorage, $"{ModuleId}:{MessagesStorage}", JsonOptions);
        _policy = policy;
    }

    internal Task<ContextChannelRecord?> GetChannelAsync(Guid id, CancellationToken ct = default) =>
        _channels.GetAsync(Key(id), ct);

    internal Task<ContextThreadRecord?> GetThreadAsync(Guid id, CancellationToken ct = default) =>
        _threads.GetAsync(Key(id), ct);

    internal Task<ContextRecord?> GetContextAsync(Guid id, CancellationToken ct = default) =>
        _contexts.GetAsync(Key(id), ct);

    internal async Task<ContextChannelRecord> EnsureConversationChannelAsync(
        RequestPrincipal caller,
        Guid channelId,
        CancellationToken ct = default)
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
                ct);
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
            ct);
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

    internal async Task<ContextThreadRecord> CreateThreadAsync(
        RequestPrincipal caller,
        Guid channelId,
        string name,
        Guid? contextId = null,
        Guid? threadId = null,
        CancellationToken ct = default)
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
            ct);

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
        CancellationToken ct = default)
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
                    principal,
                    channel,
                    context,
                    ContextAccessCapabilities.ReadCrossThreadHistory,
                    ct);
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
        CancellationToken ct = default)
    {
        var thread = await GetThreadAsync(threadId, ct);
        if (thread is null || thread.ChannelId == currentChannelId)
            return null;

        var channel = await GetChannelAsync(thread.ChannelId, ct);
        if (channel is null)
            return null;
        var context = await ResolveContextAsync(channel, thread, ct);
        var decision = await EvaluateAsync(
            principal,
            channel,
            context,
            ContextAccessCapabilities.ReadCrossThreadHistory,
            ct);
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
        CancellationToken ct)
    {
        await RequireThreadAccessAsync(
            caller,
            conversationId,
            ContextAccessCapabilities.ReadHistory,
            ct);
        var messages = await ReadAllMessagesAsync(conversationId, ct);
        return messages
            .Select(message => new ChatCompletionMessage(message.Role, message.Content))
            .ToArray();
    }

    public ValueTask<IReadOnlyList<ChatCompletionMessage>> LoadHistoryAsync(
        Guid conversationId,
        CancellationToken ct)
    {
        throw new UnauthorizedAccessException(
            "A caller is required to load conversation history through the Context policy.");
    }

    public async ValueTask CommitExchangeAsync(ChatExchange exchange, CancellationToken ct)
    {
        var caller = exchange.Turn.Input.Caller
            ?? throw new UnauthorizedAccessException("Authentication is required to commit a conversation exchange.");
        RequireAuthenticatedAgent(caller);
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
        CancellationToken ct = default)
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
            ct);
        await WriteExchangeAsync(thread, caller, action.UserMessage, action.AssistantMessage, ct);
        return true;
    }

    internal async ValueTask<ContextAccessDecision> AuthorizeCommitAsync(
        RequestPrincipal caller,
        ContextCommitExchangeAction action,
        CancellationToken ct = default)
    {
        if (!caller.IsAuthenticated)
            return ContextAccessDecision.Deny("unauthenticated", "Authentication is required.");
        return await AuthorizeThreadAsync(
            caller,
            action.ThreadId,
            ContextAccessCapabilities.CommitExchange,
            ct);
    }

    internal Task<ContextAccessDecision> AuthorizeConversationAsync(
        RequestPrincipal caller,
        Guid conversationId,
        string capability,
        CancellationToken ct = default) =>
        AuthorizeThreadAsync(caller, conversationId, capability, ct);

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
        CancellationToken ct)
    {
        var decision = await AuthorizeThreadAsync(caller, threadId, capability, ct);
        if (!decision.Allowed)
            throw new UnauthorizedAccessException($"{decision.Code}: {decision.Message}");
    }

    private async Task<ContextAccessDecision> AuthorizeThreadAsync(
        RequestPrincipal caller,
        Guid threadId,
        string capability,
        CancellationToken ct)
    {
        if (!caller.IsAuthenticated)
            return ContextAccessDecision.Deny("unauthenticated", "Authentication is required.");
        var thread = await GetThreadAsync(threadId, ct);
        if (thread is null)
            return ContextAccessDecision.Deny("thread_not_found", "The conversation thread was not found.");
        var channel = await GetChannelAsync(thread.ChannelId, ct);
        if (channel is null)
            return ContextAccessDecision.Deny("channel_not_found", "The conversation channel was not found.");
        var context = await ResolveContextAsync(channel, thread, ct);
        return await EvaluateAsync(caller, channel, context, capability, ct);
    }

    private async Task RequireAllowedAsync(
        RequestPrincipal principal,
        ContextChannelRecord channel,
        Guid? contextId,
        string capability,
        CancellationToken ct)
    {
        var context = contextId is { } id
            ? await GetContextAsync(id, ct)
            : null;
        var decision = await EvaluateAsync(principal, channel, context, capability, ct);
        if (!decision.Allowed)
            throw new UnauthorizedAccessException($"{decision.Code}: {decision.Message}");
    }

    private ValueTask<ContextAccessDecision> EvaluateAsync(
        RequestPrincipal principal,
        ContextChannelRecord channel,
        ContextRecord? context,
        string capability,
        CancellationToken ct) =>
        _policy.EvaluateAsync(new ContextAccessRequest(
            principal,
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
            capability), ct);

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

    private static string Key(Guid id) => id.ToString("N");
}
