using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.Context;

public sealed class ContextCliHandler(IServiceScopeFactory scopeFactory) : IModuleCliHandler
{
    public static IReadOnlyList<(string Name, string Operation)> Commands { get; } =
    [
        ("ctx-channel-list", ContextApiOperations.ListChannels),
        ("ctx-channel-get", ContextApiOperations.GetChannel),
        ("ctx-channel-create", ContextApiOperations.CreateChannel),
        ("ctx-channel-update", ContextApiOperations.UpdateChannel),
        ("ctx-channel-delete", ContextApiOperations.DeleteChannel),
        ("ctx-channel-assign", ContextApiOperations.AssignChannel),
        ("ctx-channel-unassign", ContextApiOperations.UnassignChannel),
        ("ctx-channel-opt-in", ContextApiOperations.OptInChannel),
        ("ctx-channel-opt-out", ContextApiOperations.OptOutChannel),
        ("ctx-channel-permissions", ContextApiOperations.ChannelPermissions),
        ("ctx-channel-synchronize", ContextApiOperations.SynchronizeChannel),
        ("ctx-context-list", ContextApiOperations.ListContexts),
        ("ctx-context-get", ContextApiOperations.GetContext),
        ("ctx-context-create", ContextApiOperations.CreateContext),
        ("ctx-context-update", ContextApiOperations.UpdateContext),
        ("ctx-context-delete", ContextApiOperations.DeleteContext),
        ("ctx-context-assign", ContextApiOperations.AssignContext),
        ("ctx-context-unassign", ContextApiOperations.UnassignContext),
        ("ctx-context-activate", ContextApiOperations.ActivateContext),
        ("ctx-context-deactivate", ContextApiOperations.DeactivateContext),
        ("ctx-context-permissions", ContextApiOperations.ContextPermissions),
        ("ctx-context-synchronize", ContextApiOperations.SynchronizeContext),
        ("ctx-thread-list", ContextApiOperations.ListThreads),
        ("ctx-thread-get", ContextApiOperations.GetThread),
        ("ctx-thread-create", ContextApiOperations.CreateThread),
        ("ctx-thread-update", ContextApiOperations.UpdateThread),
        ("ctx-thread-delete", ContextApiOperations.DeleteThread),
        ("ctx-history-read", ContextApiOperations.ReadHistory),
        ("ctx-exchange-commit", ContextApiOperations.CommitExchange),
    ];

    public async ValueTask<ModuleCliResult> ExecuteAsync(
        ModuleCliInvocation invocation,
        CancellationToken ct)
    {
        var command = Commands.FirstOrDefault(item =>
            item.Name.Equals(invocation.Command, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(command.Name))
            return Failure($"Unknown Context command '{invocation.Command}'.");

        try
        {
            var payload = BuildPayload(command.Operation, invocation.Arguments);
            using var scope = scopeFactory.CreateScope();
            var gateway = scope.ServiceProvider.GetRequiredService<IContextActionGateway>();
            var result = await gateway.ExecuteAsync(
                invocation.HostActionContext,
                command.Operation,
                payload,
                ct);
            return Success(result.GetRawText());
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure(exception.Message);
        }
        catch (ArgumentException exception)
        {
            return Failure(exception.Message);
        }
    }

    private static JsonElement BuildPayload(
        string operation,
        IReadOnlyList<string> arguments)
    {
        if (arguments.Count > 0 && arguments[0].TrimStart().StartsWith('{'))
            return JsonDocument.Parse(arguments[0]).RootElement.Clone();

        return operation switch
        {
            ContextApiOperations.ListChannels
                or ContextApiOperations.ListContexts => Empty(),
            ContextApiOperations.GetChannel
                or ContextApiOperations.DeleteChannel
                or ContextApiOperations.ChannelPermissions
                or ContextApiOperations.SynchronizeChannel => IdPayload("channelId", arguments),
            ContextApiOperations.GetContext
                or ContextApiOperations.DeleteContext
                or ContextApiOperations.ContextPermissions
                or ContextApiOperations.SynchronizeContext => IdPayload("contextId", arguments),
            ContextApiOperations.GetThread
                or ContextApiOperations.DeleteThread => IdPayload("threadId", arguments),
            ContextApiOperations.ListThreads => IdPayload("channelId", arguments),
            ContextApiOperations.CreateThread => Payload(
                ("channelId", arguments.ElementAtOrDefault(0)),
                ("name", arguments.ElementAtOrDefault(1) ?? "Thread")),
            ContextApiOperations.UpdateThread => Payload(
                ("threadId", arguments.ElementAtOrDefault(0)),
                ("name", arguments.ElementAtOrDefault(1))),
            ContextApiOperations.ReadHistory => Payload(
                ("channelId", arguments.ElementAtOrDefault(0)),
                ("threadId", arguments.ElementAtOrDefault(1))),
            ContextApiOperations.CommitExchange => Payload(
                ("threadId", arguments.ElementAtOrDefault(0)),
                ("userMessage", arguments.ElementAtOrDefault(1)),
                ("assistantMessage", arguments.ElementAtOrDefault(2))),
            ContextApiOperations.CreateChannel => Payload(
                ("title", arguments.ElementAtOrDefault(0) ?? "Conversation")),
            ContextApiOperations.UpdateChannel => Payload(
                ("channelId", arguments.ElementAtOrDefault(0)),
                ("title", arguments.ElementAtOrDefault(1))),
            ContextApiOperations.AssignChannel
                or ContextApiOperations.UnassignChannel => Payload(
                    ("channelId", arguments.ElementAtOrDefault(0)),
                    ("agentId", arguments.ElementAtOrDefault(1)),
                    ("assign", operation == ContextApiOperations.AssignChannel)),
            ContextApiOperations.OptInChannel
                or ContextApiOperations.OptOutChannel => Payload(
                    ("channelId", arguments.ElementAtOrDefault(0)),
                    ("crossThreadOptedIn", operation == ContextApiOperations.OptInChannel)),
            ContextApiOperations.CreateContext => Payload(
                ("name", arguments.ElementAtOrDefault(0) ?? "Context")),
            ContextApiOperations.UpdateContext => Payload(
                ("contextId", arguments.ElementAtOrDefault(0)),
                ("name", arguments.ElementAtOrDefault(1))),
            ContextApiOperations.AssignContext
                or ContextApiOperations.UnassignContext => Payload(
                    ("contextId", arguments.ElementAtOrDefault(0)),
                    ("agentId", arguments.ElementAtOrDefault(1)),
                    ("assign", operation == ContextApiOperations.AssignContext)),
            ContextApiOperations.ActivateContext
                or ContextApiOperations.DeactivateContext => Payload(
                    ("contextId", arguments.ElementAtOrDefault(0)),
                    ("enabled", operation == ContextApiOperations.ActivateContext)),
            _ => throw new ArgumentException("The Context command arguments are invalid."),
        };
    }

    private static JsonElement IdPayload(string name, IReadOnlyList<string> arguments) =>
        Payload((name, arguments.ElementAtOrDefault(0)));

    private static JsonElement Payload(params (string Name, object? Value)[] values) =>
        JsonSerializer.SerializeToElement(values
            .Where(item => item.Value is not null)
            .ToDictionary(item => item.Name, item => item.Value));

    private static JsonElement Empty() => JsonSerializer.SerializeToElement(new { });

    private static ModuleCliResult Success(string text) =>
        new(true, [new ModuleCliOutput("stdout", text)]);

    private static ModuleCliResult Failure(string text) =>
        new(false, [new ModuleCliOutput("stderr", text)], new ExecutionError("invalid_arguments", text));
}
