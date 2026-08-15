using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.Agents;

public sealed class AgentsCreateAuthorizationHook
    : IActionInterceptor<AgentsCreateAction, AgentRecord>
{
    public ValueTask<IActionOutcome<AgentRecord>> InvokeAsync(
        ActionContext<AgentsCreateAction> context,
        IActionControl<AgentsCreateAction, AgentRecord> control,
        CancellationToken ct)
    {
        if (!context.Caller.IsAuthenticated)
            return ValueTask.FromResult(control.Cancel(
                "unauthenticated",
                "Authentication is required to create an agent."));

        return control.ProceedAsync(ct);
    }
}

public sealed class AgentsPermissionActionHook
    : IActionInterceptor<PermissionAgentAccessAction, PermissionDecision>
{
    public ValueTask<IActionOutcome<PermissionDecision>> InvokeAsync(
        ActionContext<PermissionAgentAccessAction> context,
        IActionControl<PermissionAgentAccessAction, PermissionDecision> control,
        CancellationToken ct) =>
        control.ProceedAsync(ct);
}
