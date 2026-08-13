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

public sealed class ContextActionExecutor(
    ContextStore store,
    IContextAccessPolicy policy) : IContextActionExecutor
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
        return await store.CreateThreadAsync(action.ChannelId, action.Name, action.ContextId, ct: ct);
    }

    public async Task<IReadOnlyList<ContextMessageRecord>> ReadHistoryAsync(
        RequestPrincipal caller,
        ContextReadHistoryAction action,
        CancellationToken ct = default)
    {
        var thread = await store.FindAccessibleThreadAsync(
            caller, action.ChannelId, action.ThreadId, policy, ct)
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
