using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.Context;

public sealed class ContextConversationResolver(ContextStore store) : IConversationResolver
{
    public async ValueTask<ConversationSelection> ResolveAsync(
        ChatTurnInput input,
        CancellationToken ct)
    {
        if (input.ConversationId is { } existing)
        {
            if (await store.GetThreadAsync(existing, ct) is not null)
                return new ConversationSelection(existing);
            throw new InvalidOperationException($"Conversation '{existing}' does not exist.");
        }

        var thread = await store.CreateThreadAsync(
            CreateConversationChannelId(input),
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
        var messages = await store.LoadHistoryAsync(request.ConversationId, ct);
        return new ChatContextContribution([], messages, []);
    }
}

public sealed class ContextDbContextAccessor(IModuleDbContextFactory factory)
{
    public ContextDbContext Create() => factory.CreateDbContext<ContextDbContext>();
}
