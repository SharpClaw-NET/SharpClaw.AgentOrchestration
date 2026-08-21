using System.Text.Json;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.Agents;

public sealed class AgentsApiActionTerminal(
    AgentsApiActionExecutor executor) : IHostActionEntryTerminal<AgentsApiAction, JsonElement>
{
    public Guid TerminalId => AgentsModule.ApiTerminalId;

    public ValueTask<JsonElement> InvokeAsync(
        ActionContext<AgentsApiAction> context,
        CancellationToken ct) =>
        executor.ExecuteAsync(context, ct);
}
