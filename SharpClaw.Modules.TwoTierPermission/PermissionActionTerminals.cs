using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.TwoTierPermission;

public sealed class PermissionApiActionTerminal(
    PermissionApiActionExecutor executor) : IHostActionEntryTerminal<PermissionApiAction, JsonElement>
{
    public Guid TerminalId => TwoTierPermissionModule.ApiTerminalId;

    public ValueTask<JsonElement> InvokeAsync(
        ActionContext<PermissionApiAction> context,
        CancellationToken ct) =>
        executor.ExecuteAsync(context, ct);
}
