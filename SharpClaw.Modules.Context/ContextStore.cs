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

    public ContextStore(IModuleStorageGateway gateway)
    {
        _channels = new(gateway, ModuleId, ChannelsStorage, $"{ModuleId}:{ChannelsStorage}", JsonOptions);
        _contexts = new(gateway, ModuleId, ContextsStorage, $"{ModuleId}:{ContextsStorage}", JsonOptions);
        _threads = new(gateway, ModuleId, ThreadsStorage, $"{ModuleId}:{ThreadsStorage}", JsonOptions);
        _messages = new(gateway, ModuleId, MessagesStorage, $"{ModuleId}:{MessagesStorage}", JsonOptions);
    }

    public Task<ContextChannelRecord?> GetChannelAsync(Guid id, CancellationToken ct = default) =>
        _channels.GetAsync(Key(id), ct);

    public Task<ContextThreadRecord?> GetThreadAsync(Guid id, CancellationToken ct = default) =>
        _threads.GetAsync(Key(id), ct);

    public async Task<ContextChannelRecord> SaveChannelAsync(
        ContextChannelRecord channel,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        await _channels.UpsertAsync(Key(channel.Id), channel, new
        {
            ownerAgentId = channel.OwnerAgentId?.ToString("N"),
            optedIn = channel.CrossThreadOptedIn,
            updatedAt = channel.UpdatedAt,
        }, ct);
        return channel;
    }

    public async Task<ContextRecord> SaveContextAsync(
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

    public async Task<ContextThreadRecord> CreateThreadAsync(
        Guid channelId,
        string name,
        Guid? contextId = null,
        Guid? threadId = null,
        CancellationToken ct = default)
    {
        if (channelId == Guid.Empty)
            throw new ArgumentException("A thread requires a channel id.", nameof(channelId));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A thread requires a name.", nameof(name));

        var now = DateTimeOffset.UtcNow;
        var thread = new ContextThreadRecord(
            threadId.GetValueOrDefault(Guid.NewGuid()),
            name.Trim(),
            channelId,
            contextId,
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

    public async Task AppendMessageAsync(
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

    public async Task<IReadOnlyList<ContextThreadSummary>> ListAccessibleThreadsAsync(
        RequestPrincipal principal,
        Guid currentChannelId,
        IContextAccessPolicy policy,
        CancellationToken ct = default)
    {
        var channels = await _channels.ListAsync(ct);
        var visible = new List<ContextChannelRecord>();
        foreach (var channel in channels.Where(channel => channel.Id != currentChannelId))
        {
            var decision = await policy.EvaluateAsync(new ContextAccessRequest(
                principal,
                channel.Id,
                channel.OwnerAgentId,
                channel.AllowedAgentIds,
                channel.DefaultContextAgentId,
                channel.ContextAllowedAgentIds,
                channel.CrossThreadOptedIn), ct);
            if (decision.Allowed)
                visible.Add(channel);
        }

        var summaries = new List<ContextThreadSummary>();
        foreach (var channel in visible)
        {
            var threads = await _threads.Query()
                .WhereIndex("channelId").EqualTo(channel.Id.ToString("N"))
                .OrderByIndexDescending("updatedAt")
                .ToListAsync(ct);
            summaries.AddRange(threads.Select(thread => new ContextThreadSummary(
                thread.Id,
                thread.Name,
                thread.ChannelId,
                channel.Title,
                thread.UpdatedAt)));
        }

        return summaries
            .OrderByDescending(thread => thread.UpdatedAt)
            .ToArray();
    }

    public async Task<ContextThreadSummary?> FindAccessibleThreadAsync(
        RequestPrincipal principal,
        Guid currentChannelId,
        Guid threadId,
        IContextAccessPolicy policy,
        CancellationToken ct = default)
    {
        var thread = await GetThreadAsync(threadId, ct);
        if (thread is null)
            return null;

        var summaries = await ListAccessibleThreadsAsync(principal, currentChannelId, policy, ct);
        return summaries.FirstOrDefault(candidate => candidate.ThreadId == thread.Id);
    }

    public async Task<IReadOnlyList<ContextMessageRecord>> ReadMessagesAsync(
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

    public async Task<IReadOnlyList<ContextMessageRecord>> ReadAllMessagesAsync(
        Guid threadId,
        CancellationToken ct = default)
    {
        var records = await _messages.Query()
            .WhereIndex("threadId").EqualTo(threadId.ToString("N"))
            .OrderByIndex("createdAt")
            .ToListAsync(ct);
        return records.OrderBy(message => message.CreatedAt).ToArray();
    }

    public async ValueTask<IReadOnlyList<ChatCompletionMessage>> LoadHistoryAsync(
        Guid conversationId,
        CancellationToken ct)
    {
        var messages = await ReadAllMessagesAsync(conversationId, ct);
        return messages.Select(message => new ChatCompletionMessage(message.Role, message.Content)).ToArray();
    }

    public async ValueTask CommitExchangeAsync(ChatExchange exchange, CancellationToken ct)
    {
        var existing = await GetThreadAsync(exchange.Turn.Conversation.ConversationId, ct);
        var thread = existing ?? await CreateThreadAsync(
            Guid.TryParse(exchange.Turn.Input.Caller?.SubjectId, out var callerId)
                ? callerId
                : Guid.NewGuid(),
            "Conversation",
            threadId: exchange.Turn.Conversation.ConversationId,
            ct: ct);
        var now = DateTimeOffset.UtcNow;
        var sender = exchange.Turn.Input.Caller?.SubjectId ?? "unknown";
        await AppendMessageAsync(new ContextMessageRecord(
            Guid.NewGuid(), thread.Id, thread.ChannelId, "user", exchange.UserMessage, sender, now, now), ct);
        if (!string.IsNullOrWhiteSpace(exchange.Completion.Content))
        {
            await AppendMessageAsync(new ContextMessageRecord(
                Guid.NewGuid(), thread.Id, thread.ChannelId, "assistant",
                exchange.Completion.Content!, "assistant", now, now), ct);
        }
    }

    private static string Key(Guid id) => id.ToString("N");
}
