using System.Text.Json;
using System.Text.Json.Serialization;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.TwoTierPermission;

public sealed class PermissionToolHandler(IPermissionActionGateway gateway) : IToolHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async ValueTask<ToolResult> InvokeAsync(
        ToolInvocation invocation,
        CancellationToken ct)
    {
        try
        {
            var operation = invocation.ToolName switch
            {
                TwoTierPermissionModule.EvaluateTool => PermissionApiOperations.Evaluate,
                TwoTierPermissionModule.GrantTool => PermissionApiOperations.Grant,
                TwoTierPermissionModule.RevokeTool => PermissionApiOperations.Revoke,
                TwoTierPermissionModule.ApproveTool => PermissionApiOperations.Approve,
                _ => null,
            };
            if (operation is null)
                return ToolResult.Error($"Unknown permission tool '{invocation.ToolName}'.");

            var payload = BuildPayload(operation, invocation);
            var result = await gateway.ExecuteAsync(
                invocation.HostActionContext,
                operation,
                payload,
                ct);
            return new ToolResult(result.GetRawText());
        }
        catch (UnauthorizedAccessException exception)
        {
            return ToolResult.Error(exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ToolResult.Error(exception.Message);
        }
    }

    private static JsonElement BuildPayload(
        string operation,
        ToolInvocation invocation)
    {
        if (operation == PermissionApiOperations.Evaluate)
            return invocation.Arguments;

        var subjectId = StringValue(invocation.Arguments, "subjectId")
            ?? throw new ArgumentException("subjectId is required.");
        var capability = StringValue(invocation.Arguments, "capability")
            ?? throw new ArgumentException("capability is required.");
        var scope = StringValue(invocation.Arguments, "scope") ?? "global";
        return operation switch
        {
            PermissionApiOperations.Grant => JsonSerializer.SerializeToElement(
                new PermissionGrantAction(
                    subjectId,
                    capability,
                    scope,
                    EnumValue(invocation.Arguments, "clearance", PermissionClearance.ApprovedBySameLevelUser),
                    !invocation.Arguments.TryGetProperty("requireSourceOptIn", out var required)
                        || required.ValueKind != JsonValueKind.False,
                    DateTimeOffset.TryParse(StringValue(invocation.Arguments, "expiresAt"), out var grantExpiry)
                        ? grantExpiry
                        : null), JsonOptions),
            PermissionApiOperations.Revoke => JsonSerializer.SerializeToElement(
                new PermissionRevokeAction(subjectId, capability, scope), JsonOptions),
            PermissionApiOperations.Approve => JsonSerializer.SerializeToElement(
                new PermissionApproveAction(
                    subjectId,
                    capability,
                    scope,
                    DateTimeOffset.TryParse(StringValue(invocation.Arguments, "expiresAt"), out var approvalExpiry)
                        ? approvalExpiry
                        : null), JsonOptions),
            _ => throw new ArgumentException("The permission operation is not supported.", nameof(operation)),
        };
    }

    private static string? StringValue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static PermissionClearance EnumValue(
        JsonElement root,
        string name,
        PermissionClearance fallback) =>
        Enum.TryParse<PermissionClearance>(StringValue(root, name), true, out var value)
            ? value
            : fallback;
}
