using System.Text.Json;
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

    public ValueTask<PermissionDecision> EvaluateAsync(
        RequestPrincipal caller,
        PermissionContextAccessAction action,
        CancellationToken ct = default) =>
        policy.EvaluateDetailedAsync(
            action.Request with { Principal = caller },
            ct);

    public ValueTask<PermissionDecision> EvaluateAsync(
        RequestPrincipal caller,
        PermissionAgentAccessAction action,
        CancellationToken ct = default) =>
        policy.EvaluateAgentDetailedAsync(
            caller,
            action.Capability,
            action.TargetAgentId,
            ct);

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

public sealed class PermissionApiActionExecutor(
    TwoTierPermissionPolicy policy,
    PermissionPolicyStore store)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
    public async ValueTask<JsonElement> ExecuteAsync(
        ActionContext<PermissionApiAction> actionContext,
        CancellationToken ct = default)
    {
        var action = actionContext.Action;
        var caller = actionContext.Caller;
        return action.Operation switch
        {
            PermissionApiOperations.Evaluate => Json(await EvaluateAsync(caller, action, ct)),
            PermissionApiOperations.ListPolicies => Json(await policy.ListPoliciesAsync(caller, ct)),
            PermissionApiOperations.GetPolicy => Json(await policy.GetPolicyAsync(caller, StringValue(action.Payload, "subjectId"), ct)),
            PermissionApiOperations.SavePolicy => Json(await policy.SavePolicyAsync(caller, Deserialize<PermissionPolicyRecord>(action.Payload), ct)),
            PermissionApiOperations.DeletePolicy => Json(await policy.DeletePolicyAsync(caller, StringValue(action.Payload, "subjectId"), ct)),
            PermissionApiOperations.Grant => Json(await GrantAsync(caller, action, ct)),
            PermissionApiOperations.Revoke => Json(await RevokeAsync(caller, action, ct)),
            PermissionApiOperations.Approve => Json(await ApproveAsync(caller, action, ct)),
            PermissionApiOperations.ListRoles => Json(await policy.ListRolesAsync(caller, ct)),
            PermissionApiOperations.GetRole => Json(await policy.GetRoleAsync(caller, StringValue(action.Payload, "roleId"), ct)),
            PermissionApiOperations.SaveRole => Json(await policy.SaveRoleAsync(caller, Deserialize<PermissionRoleRecord>(action.Payload), ct)),
            PermissionApiOperations.DeleteRole => Json(await policy.DeleteRoleAsync(caller, StringValue(action.Payload, "roleId"), ct)),
            PermissionApiOperations.AssignRole => Json(await policy.AssignRoleAsync(caller, action.Payload, ct)),
            PermissionApiOperations.ListPermissionSets => Json(await policy.ListPermissionSetsAsync(caller, ct)),
            PermissionApiOperations.GetPermissionSet => Json(await policy.GetPermissionSetAsync(caller, StringValue(action.Payload, "permissionSetId"), ct)),
            PermissionApiOperations.SavePermissionSet => Json(await policy.SavePermissionSetAsync(caller, Deserialize<PermissionSetRecord>(action.Payload), ct)),
            PermissionApiOperations.DeletePermissionSet => Json(await policy.DeletePermissionSetAsync(caller, StringValue(action.Payload, "permissionSetId"), ct)),
            PermissionApiOperations.AssignPermissionSet => Json(await policy.AssignPermissionSetAsync(caller, action.Payload, ct)),
            _ => throw new ArgumentException($"Unknown permission operation '{action.Operation}'.", nameof(action)),
        };
    }

    private async Task<PermissionDecision> EvaluateAsync(
        RequestPrincipal caller,
        PermissionApiAction action,
        CancellationToken ct)
    {
        var subjectId = StringValue(action.Payload, "subjectId") ?? caller.SubjectId;
        var capability = StringValue(action.Payload, "capability")
            ?? throw new ArgumentException("capability is required.");
        return await policy.EvaluateCapabilityAsync(
            caller,
            new PermissionEvaluateAction(
                subjectId,
                capability,
                StringValue(action.Payload, "scope") ?? "global",
                BoolValue(action.Payload, "sourceChannelOptedIn")),
            ct);
    }

    private async Task<bool> GrantAsync(
        RequestPrincipal caller,
        PermissionApiAction action,
        CancellationToken ct)
    {
        await policy.GrantAsync(caller, Deserialize<PermissionGrantAction>(action.Payload), ct);
        return true;
    }

    private async Task<bool> RevokeAsync(
        RequestPrincipal caller,
        PermissionApiAction action,
        CancellationToken ct)
    {
        await policy.RevokeAsync(caller, Deserialize<PermissionRevokeAction>(action.Payload), ct);
        return true;
    }

    private async Task<bool> ApproveAsync(
        RequestPrincipal caller,
        PermissionApiAction action,
        CancellationToken ct)
    {
        await policy.ApproveAsync(caller, Deserialize<PermissionApproveAction>(action.Payload), ct);
        return true;
    }

    private static T Deserialize<T>(JsonElement payload) =>
        payload.Deserialize<T>(JsonOptions)
        ?? throw new ArgumentException($"The {typeof(T).Name} payload is invalid.");

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static string? StringValue(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool BoolValue(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;
}

public interface IPermissionActionGateway
{
    ValueTask<JsonElement> ExecuteAsync(
        HostActionEntryRequestContext hostContext,
        string operation,
        JsonElement payload,
        CancellationToken ct = default);
}

public sealed class PermissionActionGateway(
    HostModuleActionEntry entry,
    PermissionApiActionExecutor executor) : IPermissionActionGateway
{
    public ValueTask<JsonElement> ExecuteAsync(
        HostActionEntryRequestContext hostContext,
        string operation,
        JsonElement payload,
        CancellationToken ct = default)
    {
        var action = new PermissionApiAction(operation, payload);
        return entry.InvokeAsync(
            TwoTierPermissionModule.ApiDescriptor,
            action,
            hostContext,
            (context, terminalCt) => executor.ExecuteAsync(context, terminalCt),
            ct);
    }
}
