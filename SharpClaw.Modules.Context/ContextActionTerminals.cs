using System.Text.Json;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.Context;

public sealed class ContextApiActionTerminal(
    ContextApiActionExecutor executor) : IHostActionEntryTerminal<ContextApiAction, JsonElement>
{
    public Guid TerminalId => ContextModule.ApiTerminalId;

    public ValueTask<JsonElement> InvokeAsync(
        ActionContext<ContextApiAction> context,
        CancellationToken ct) =>
        executor.ExecuteAsync(context, ct);
}
