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

public sealed class PermissionContextAccessActionTerminal(
    IPermissionActionExecutor executor) : IHostActionEntryTerminal<PermissionContextAccessAction, PermissionDecision>
{
    public Guid TerminalId => TwoTierPermissionModule.ContextAccessTerminalId;

    public ValueTask<PermissionDecision> InvokeAsync(
        ActionContext<PermissionContextAccessAction> context,
        CancellationToken ct) =>
        executor.EvaluateAsync(context.Caller, context.Action, ct);
}

public sealed class PermissionAgentAccessActionTerminal(
    IPermissionActionExecutor executor) : IHostActionEntryTerminal<PermissionAgentAccessAction, PermissionDecision>
{
    public Guid TerminalId => TwoTierPermissionModule.AgentAccessTerminalId;

    public ValueTask<PermissionDecision> InvokeAsync(
        ActionContext<PermissionAgentAccessAction> context,
        CancellationToken ct) =>
        executor.EvaluateAsync(context.Caller, context.Action, ct);
}
