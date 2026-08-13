using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.Context;

public sealed class ContextCliHandler(
    ContextStore store) : IModuleCliHandler
{
    public async ValueTask<ModuleCliResult> ExecuteAsync(
        ModuleCliInvocation invocation,
        CancellationToken ct)
    {
        if (invocation.Command.Equals("ctx-thread-list", StringComparison.OrdinalIgnoreCase))
        {
            if (invocation.Arguments.Count == 0 || !Guid.TryParse(invocation.Arguments[0], out var channelId))
                return Failure("ctx-thread-list requires a channel id.");
            var principal = invocation.Caller;
            return Success(System.Text.Json.JsonSerializer.Serialize(
                await store.ListAccessibleThreadsAsync(
                    principal,
                    channelId,
                    ct)));
        }

        return Failure($"Unknown Context command '{invocation.Command}'.");
    }

    private static ModuleCliResult Success(string text) =>
        new(true, [new ModuleCliOutput("stdout", text)]);

    private static ModuleCliResult Failure(string text) =>
        new(false, [new ModuleCliOutput("stderr", text)], new ExecutionError("invalid_arguments", text));
}
