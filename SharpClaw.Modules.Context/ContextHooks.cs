using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.Context;

public sealed class ContextCommitAuthorizationHook
    (ContextStore store) : IActionInterceptor<ContextCommitExchangeAction, bool>
{
    public async ValueTask<IActionOutcome<bool>> InvokeAsync(
        ActionContext<ContextCommitExchangeAction> context,
        IActionControl<ContextCommitExchangeAction, bool> control,
        CancellationToken ct)
    {
        if (!context.Caller.IsAuthenticated)
            return control.Cancel(
                "unauthenticated",
                "Authentication is required to commit a context exchange.");

        var decision = await store.AuthorizeCommitAsync(context.Caller, context.Action, ct);
        if (!decision.Allowed)
            return control.Cancel(decision.Code, decision.Message);

        return await control.ProceedAsync(ct);
    }
}
