using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.Context;

public sealed class ContextCommitAuthorizationHook
    : IActionInterceptor<ContextCommitExchangeAction, bool>
{
    public ValueTask<IActionOutcome<bool>> InvokeAsync(
        ActionContext<ContextCommitExchangeAction> context,
        IActionControl<ContextCommitExchangeAction, bool> control,
        CancellationToken ct)
    {
        if (!context.Caller.IsAuthenticated)
            return ValueTask.FromResult(control.Cancel(
                "unauthenticated",
                "Authentication is required to commit a context exchange."));

        return control.ProceedAsync(ct);
    }
}
