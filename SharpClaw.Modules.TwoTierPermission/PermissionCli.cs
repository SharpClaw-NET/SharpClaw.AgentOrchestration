using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.TwoTierPermission;

public sealed class PermissionCliHandler(TwoTierPermissionPolicy policy) : IModuleCliHandler
{
    public async ValueTask<ModuleCliResult> ExecuteAsync(
        ModuleCliInvocation invocation,
        CancellationToken ct)
    {
        if (invocation.Command.Equals("perm-grant", StringComparison.OrdinalIgnoreCase))
        {
            if (invocation.Arguments.Count < 3)
                return Failure("perm-grant requires subject id, capability, and clearance.");
            if (!Enum.TryParse(invocation.Arguments[2], true, out PermissionClearance clearance))
                return Failure("perm-grant has an invalid clearance.");
            try
            {
                await policy.GrantAsync(invocation.Caller,
                    new PermissionGrantAction(
                        invocation.Arguments[0], invocation.Arguments[1], "global", clearance), ct);
                return Success("Permission granted.");
            }
            catch (UnauthorizedAccessException exception)
            {
                return Failure(exception.Message);
            }
        }

        if (invocation.Command.Equals("perm-approve", StringComparison.OrdinalIgnoreCase))
        {
            if (invocation.Arguments.Count < 2)
                return Failure("perm-approve requires subject id and capability.");
            try
            {
                await policy.ApproveAsync(invocation.Caller,
                    new PermissionApproveAction(
                        invocation.Arguments[0], invocation.Arguments[1],
                        invocation.Arguments.Count > 2 ? invocation.Arguments[2] : "global"), ct);
                return Success("Permission approved.");
            }
            catch (UnauthorizedAccessException exception)
            {
                return Failure(exception.Message);
            }
        }

        return Failure($"Unknown permission command '{invocation.Command}'.");
    }

    private static ModuleCliResult Success(string text) =>
        new(true, [new ModuleCliOutput("stdout", text)]);

    private static ModuleCliResult Failure(string text) =>
        new(false, [new ModuleCliOutput("stderr", text)], new ExecutionError("permission_denied", text));
}
