using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Modules.TwoTierPermission;

public sealed class PermissionGrantAuthorizationHook
    : IActionInterceptor<PermissionGrantAction, bool>
{
    public ValueTask<IActionOutcome<bool>> InvokeAsync(
        ActionContext<PermissionGrantAction> context,
        IActionControl<PermissionGrantAction, bool> control,
        CancellationToken ct)
    {
        if (!context.Caller.IsAuthenticated)
            return ValueTask.FromResult(control.Cancel(
                "unauthenticated",
                "Authentication is required to grant permission."));

        return control.ProceedAsync(ct);
    }
}
