using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.TwoTierPermission;

public interface IPermissionActionExecutor
{
    ValueTask<PermissionDecision> EvaluateAsync(
        ContextAccessRequest request,
        CancellationToken ct = default);

    ValueTask<PermissionDecision> EvaluateAsync(
        RequestPrincipal caller,
        PermissionEvaluateAction action,
        CancellationToken ct = default);

    Task<bool> GrantAsync(
        RequestPrincipal caller,
        PermissionGrantAction action,
        CancellationToken ct = default);

    Task<bool> RevokeAsync(
        RequestPrincipal caller,
        PermissionRevokeAction action,
        CancellationToken ct = default);

    Task<bool> ApproveAsync(
        RequestPrincipal caller,
        PermissionApproveAction action,
        CancellationToken ct = default);
}

public sealed class PermissionActionExecutor(
    TwoTierPermissionPolicy policy) : IPermissionActionExecutor
{
    public ValueTask<PermissionDecision> EvaluateAsync(
        ContextAccessRequest request,
        CancellationToken ct = default) =>
        policy.EvaluateDetailedAsync(request, ct);

    public ValueTask<PermissionDecision> EvaluateAsync(
        RequestPrincipal caller,
        PermissionEvaluateAction action,
        CancellationToken ct = default) =>
        policy.EvaluateCapabilityAsync(caller, action, ct);

    public async Task<bool> GrantAsync(
        RequestPrincipal caller,
        PermissionGrantAction action,
        CancellationToken ct = default)
    {
        await policy.GrantAsync(caller, action, ct);
        return true;
    }

    public async Task<bool> RevokeAsync(
        RequestPrincipal caller,
        PermissionRevokeAction action,
        CancellationToken ct = default)
    {
        await policy.RevokeAsync(caller, action, ct);
        return true;
    }

    public async Task<bool> ApproveAsync(
        RequestPrincipal caller,
        PermissionApproveAction action,
        CancellationToken ct = default)
    {
        await policy.ApproveAsync(caller, action, ct);
        return true;
    }
}
