namespace SharpClaw.Modules.Context;

public sealed record ContextChannelRecord(
    Guid Id,
    string Title,
    Guid? OwnerAgentId,
    Guid? DefaultContextAgentId,
    IReadOnlyList<Guid> AllowedAgentIds,
    IReadOnlyList<Guid> ContextAllowedAgentIds,
    bool CrossThreadOptedIn,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public Guid? ContextId { get; init; }
}

public sealed record ContextRecord(
    Guid Id,
    string Name,
    Guid? DefaultAgentId,
    IReadOnlyList<Guid> AllowedAgentIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ContextThreadRecord(
    Guid Id,
    string Name,
    Guid ChannelId,
    Guid? ContextId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ContextMessageRecord(
    Guid Id,
    Guid ThreadId,
    Guid ChannelId,
    string Role,
    string Content,
    string Sender,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ContextThreadSummary(
    Guid ThreadId,
    string ThreadName,
    Guid ChannelId,
    string ChannelTitle,
    DateTimeOffset UpdatedAt);

public sealed record ContextCreateThreadAction(
    Guid ChannelId,
    string Name,
    Guid? ContextId = null);

public sealed record ContextReadHistoryAction(
    Guid ChannelId,
    Guid ThreadId,
    int MaxMessages = 50);

public sealed record ContextCommitExchangeAction(
    Guid ThreadId,
    string UserMessage,
    string AssistantMessage);

public sealed record ContextThreadChangedEvent(
    Guid ThreadId,
    Guid ChannelId,
    string Change,
    DateTimeOffset ChangedAt);

public sealed record ContextExchangeCommittedEvent(
    Guid ThreadId,
    int MessageCount,
    DateTimeOffset CommittedAt);
