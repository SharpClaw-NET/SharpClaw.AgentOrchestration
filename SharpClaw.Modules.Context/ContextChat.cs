using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.Context;

public sealed class ContextConversationResolver(ContextStore store) : IConversationResolver
{
    public async ValueTask<ConversationSelection> ResolveAsync(
        ChatTurnInput input,
        CancellationToken ct)
    {
        var caller = input.Caller;
        if (caller is null || !caller.IsAuthenticated)
            throw new UnauthorizedAccessException("Authentication is required to resolve a conversation.");

        if (input.ConversationId is { } existing)
        {
            var decision = await store.AuthorizeConversationAsync(
                caller,
                existing,
                ContextAccessCapabilities.ReadHistory,
                ct);
            if (!decision.Allowed)
                throw new UnauthorizedAccessException($"{decision.Code}: {decision.Message}");
            return new ConversationSelection(existing);
        }

        var channelId = CreateConversationChannelId(input);
        var channel = await store.EnsureConversationChannelAsync(caller, channelId, ct);
        var thread = await store.CreateThreadAsync(
            caller,
            channel.Id,
            "Conversation",
            ct: ct);
        return new ConversationSelection(thread.Id, Created: true);
    }

    private static Guid CreateConversationChannelId(ChatTurnInput input) =>
        input.Caller is { SubjectId: var subject }
        && Guid.TryParse(subject, out var id)
            ? id
            : Guid.NewGuid();
}

public sealed class ContextHistoryContributor(ContextStore store) : IChatContextContributor
{
    public async ValueTask<ChatContextContribution> ContributeAsync(
        ChatContextRequest request,
        CancellationToken ct)
    {
        var caller = request.Turn?.Input.Caller
            ?? throw new UnauthorizedAccessException(
                "A caller is required to load conversation history.");
        var messages = await store.LoadHistoryAsync(caller, request.ConversationId, ct);
        return new ChatContextContribution([], messages, []);
    }
}
