using System.Text.Json;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.TwoTierPermission;

public sealed class PermissionToolHandler(TwoTierPermissionPolicy policy) : IToolHandler
{
    public async ValueTask<ToolResult> InvokeAsync(
        ToolInvocation invocation,
        CancellationToken ct)
    {
        try
        {
            return invocation.ToolName switch
            {
                TwoTierPermissionModule.EvaluateTool => await EvaluateAsync(invocation, ct),
                TwoTierPermissionModule.GrantTool => await GrantAsync(invocation, ct),
                TwoTierPermissionModule.RevokeTool => await RevokeAsync(invocation, ct),
                _ => ToolResult.Error($"Unknown permission tool '{invocation.ToolName}'."),
            };
        }
        catch (UnauthorizedAccessException exception)
        {
            return ToolResult.Error(exception.Message);
        }
    }

    private async Task<ToolResult> EvaluateAsync(ToolInvocation invocation, CancellationToken ct)
    {
        var subjectId = StringValue(invocation.Arguments, "subjectId") ?? invocation.Caller.SubjectId;
        var channelId = GuidValue(invocation.Arguments, "channelId");
        var ownerAgentId = GuidValue(invocation.Arguments, "ownerAgentId");
        var allowedAgents = GuidList(invocation.Arguments, "allowedAgentIds");
        var contextAgentId = GuidValue(invocation.Arguments, "defaultContextAgentId");
        var contextAllowed = GuidList(invocation.Arguments, "contextAllowedAgentIds");
        var optedIn = invocation.Arguments.TryGetProperty("sourceChannelOptedIn", out var opted)
            && opted.ValueKind == JsonValueKind.True;
        var principal = invocation.Caller with { SubjectId = subjectId };
        var result = await policy.EvaluateDetailedAsync(new(
            principal, channelId, ownerAgentId, allowedAgents,
            contextAgentId, contextAllowed, optedIn), ct);
        return new ToolResult(JsonSerializer.Serialize(result));
    }

    private async Task<ToolResult> GrantAsync(ToolInvocation invocation, CancellationToken ct)
    {
        var subjectId = StringValue(invocation.Arguments, "subjectId");
        var capability = StringValue(invocation.Arguments, "capability");
        var scope = StringValue(invocation.Arguments, "scope") ?? "global";
        if (subjectId is null || capability is null)
            return ToolResult.Error("subjectId and capability are required.");
        var clearance = EnumValue(invocation.Arguments, "clearance", PermissionClearance.ApprovedBySameLevelUser);
        var optIn = !invocation.Arguments.TryGetProperty("requireSourceOptIn", out var required)
            || required.ValueKind != JsonValueKind.False;
        await policy.GrantAsync(invocation.Caller,
            new PermissionGrantAction(subjectId, capability, scope, clearance, optIn), ct);
        return ToolResult.Text("Permission granted.");
    }

    private async Task<ToolResult> RevokeAsync(ToolInvocation invocation, CancellationToken ct)
    {
        var subjectId = StringValue(invocation.Arguments, "subjectId");
        var capability = StringValue(invocation.Arguments, "capability");
        var scope = StringValue(invocation.Arguments, "scope") ?? "global";
        if (subjectId is null || capability is null)
            return ToolResult.Error("subjectId and capability are required.");
        await policy.RevokeAsync(invocation.Caller,
            new PermissionRevokeAction(subjectId, capability, scope), ct);
        return ToolResult.Text("Permission revoked.");
    }

    private static string? StringValue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Guid GuidValue(JsonElement root, string name) =>
        Guid.TryParse(StringValue(root, name), out var value) ? value : Guid.Empty;

    private static IReadOnlyList<Guid> GuidList(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Select(item => Guid.TryParse(item.GetString(), out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .ToArray()
            : [];

    private static PermissionClearance EnumValue(
        JsonElement root,
        string name,
        PermissionClearance fallback) =>
        Enum.TryParse<PermissionClearance>(StringValue(root, name), true, out var value)
            ? value
            : fallback;
}
