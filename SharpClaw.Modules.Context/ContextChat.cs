using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.Context;

public sealed class ContextConversationResolver(
    ContextStore store,
    HostAuthorizationEntry authorization) : IConversationResolver
{
    public async ValueTask<ConversationSelection> ResolveAsync(
        ChatTurnInput input,
        ChatOperationContext context,
        CancellationToken ct)
    {
        var caller = context.Caller;
        if (!caller.IsAuthenticated)
            throw new UnauthorizedAccessException("Authentication is required to resolve a conversation.");

        using var authorizationScope = store.PushAuthorization(
            new ChatAuthorizationClient(context, authorization));

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

        var channelId = CreateConversationChannelId(context.Caller);
        var channel = await store.EnsureConversationChannelAsync(caller, channelId, ct);
        var thread = await store.CreateThreadAsync(
            caller,
            channel.Id,
            "Conversation",
            ct: ct);
        return new ConversationSelection(thread.Id, Created: true);
    }

    private static Guid CreateConversationChannelId(RequestPrincipal caller) =>
        Guid.TryParse(caller.SubjectId, out var id)
            ? id
            : Guid.NewGuid();
}

public sealed class ContextHistoryContributor(
    ContextStore store,
    HostAuthorizationEntry authorization) : IChatContextContributor
{
    public async ValueTask<ChatContextContribution> ContributeAsync(
        ChatContextRequest request,
        ChatOperationContext context,
        CancellationToken ct)
    {
        var caller = context.Caller;
        if (!caller.IsAuthenticated)
            throw new UnauthorizedAccessException("Authentication is required to load conversation history.");
        using var authorizationScope = store.PushAuthorization(
            new ChatAuthorizationClient(context, authorization));
        var messages = await store.LoadHistoryAsync(caller, request.ConversationId, ct);
        return new ChatContextContribution([], messages, []);
    }
}
