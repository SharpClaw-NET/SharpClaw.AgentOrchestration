using System.Text.Json;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Modules.TwoTierPermission;

public sealed class PermissionEndpointContribution(
    PermissionApiActionTerminal terminal) : IHttpEndpointHandler
{
    private static readonly JsonElement EmptyPayload =
        JsonSerializer.SerializeToElement(new { });

    public const string EvaluateRoute = "/sharpclaw/permission/evaluate";
    public const string GrantRoute = "/sharpclaw/permission/grants";
    public const string RevokeRoute = "/sharpclaw/permission/grants/revoke";
    public const string ApproveRoute = "/sharpclaw/permission/grants/approve";

    public static IReadOnlyList<string> PolicyRoutes { get; } =
    [
        "/sharpclaw/permission/policies",
        "/sharpclaw/permission/policies/get",
        "/sharpclaw/permission/policies/save",
        "/sharpclaw/permission/policies/delete",
    ];

    public static IReadOnlyList<string> RoleRoutes { get; } =
    [
        "/sharpclaw/permission/roles",
        "/sharpclaw/permission/roles/get",
        "/sharpclaw/permission/roles/save",
        "/sharpclaw/permission/roles/delete",
        "/sharpclaw/permission/roles/assign",
    ];

    public static IReadOnlyList<string> PermissionSetRoutes { get; } =
    [
        "/sharpclaw/permission/sets",
        "/sharpclaw/permission/sets/get",
        "/sharpclaw/permission/sets/save",
        "/sharpclaw/permission/sets/delete",
        "/sharpclaw/permission/sets/assign",
    ];

    private static IReadOnlyList<RouteDefinition> Routes { get; } =
    [
        Route(EvaluateRoute, "POST", PermissionApiOperations.Evaluate),
        Route(GrantRoute, "POST", PermissionApiOperations.Grant),
        Route(RevokeRoute, "POST", PermissionApiOperations.Revoke),
        Route(ApproveRoute, "POST", PermissionApiOperations.Approve),
        Route(PolicyRoutes[0], "GET", PermissionApiOperations.ListPolicies),
        Route(PolicyRoutes[1], "GET", PermissionApiOperations.GetPolicy),
        Route(PolicyRoutes[2], "POST", PermissionApiOperations.SavePolicy),
        Route(PolicyRoutes[3], "DELETE", PermissionApiOperations.DeletePolicy),
        Route(RoleRoutes[0], "GET", PermissionApiOperations.ListRoles),
        Route(RoleRoutes[1], "GET", PermissionApiOperations.GetRole),
        Route(RoleRoutes[2], "POST", PermissionApiOperations.SaveRole),
        Route(RoleRoutes[3], "DELETE", PermissionApiOperations.DeleteRole),
        Route(RoleRoutes[4], "POST", PermissionApiOperations.AssignRole),
        Route(PermissionSetRoutes[0], "GET", PermissionApiOperations.ListPermissionSets),
        Route(PermissionSetRoutes[1], "GET", PermissionApiOperations.GetPermissionSet),
        Route(PermissionSetRoutes[2], "POST", PermissionApiOperations.SavePermissionSet),
        Route(PermissionSetRoutes[3], "DELETE", PermissionApiOperations.DeletePermissionSet),
        Route(PermissionSetRoutes[4], "POST", PermissionApiOperations.AssignPermissionSet),
    ];

    public static IReadOnlyList<EndpointRouteDescriptor> EndpointRoutes { get; } =
        Routes.Select(route => route.Descriptor).ToArray();

    public async ValueTask<HttpEndpointResponse> InvokeAsync(
        HostEndpointRouteRequest request,
        IHostActionEntry hostActionEntry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(hostActionEntry);

        RouteDefinition? route = Routes.SingleOrDefault(candidate =>
            candidate.Descriptor.ToRouteIdentity().Equals(request.Route));
        if (route is null)
        {
            return ErrorResponse(
                404,
                "endpoint_route_not_found",
                "The Permission endpoint route is not registered.");
        }

        JsonElement payload;
        try
        {
            payload = ReadPayload(request.Body);
        }
        catch (JsonException)
        {
            return ErrorResponse(
                400,
                "endpoint_invalid_json",
                "The Permission endpoint payload is not valid JSON.");
        }

        try
        {
            IActionOutcome<JsonElement> outcome = await hostActionEntry.InvokeAsync(
                new HostActionEntryRequest<PermissionApiAction, JsonElement>(
                    TwoTierPermissionModule.ApiDescriptor,
                    new PermissionApiAction(route.Operation, payload),
                    request.Invocation.HostActionContext),
                terminal,
                cancellationToken);
            return ToResponse(outcome);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return ErrorResponse(403, "endpoint_forbidden", "The Permission endpoint request is not authorized.");
        }
        catch (ArgumentException)
        {
            return ErrorResponse(400, "endpoint_invalid_request", "The Permission endpoint request is invalid.");
        }
        catch (InvalidOperationException)
        {
            return ErrorResponse(404, "endpoint_resource_not_found", "The Permission endpoint resource was not found.");
        }
    }

    private static RouteDefinition Route(string path, string method, string operation) =>
        new(
            new EndpointRouteDescriptor(
                $"{TwoTierPermissionModule.ModuleIdValue}:http:{method}:{path}",
                path,
                method,
                HostEndpointTransport.Http),
            operation);

    private static JsonElement ReadPayload(byte[] body)
    {
        if (body is null || body.Length == 0)
            return EmptyPayload;

        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? EmptyPayload
            : document.RootElement.Clone();
    }

    private static HttpEndpointResponse ToResponse(IActionOutcome<JsonElement> outcome) =>
        outcome.Kind switch
        {
            ActionOutcomeKind.Completed when outcome.Result is { } result =>
                HttpEndpointResponse.Json(200, result),
            ActionOutcomeKind.Cancelled => ErrorResponse(
                409,
                "endpoint_cancelled",
                "The Permission endpoint action was cancelled."),
            ActionOutcomeKind.Failed => ErrorResponse(
                500,
                outcome.Error?.Code ?? "endpoint_failed",
                "The Permission endpoint action failed."),
            ActionOutcomeKind.Uncertain => ErrorResponse(
                503,
                outcome.Uncertainty?.Code ?? "endpoint_uncertain",
                "The Permission endpoint action result is uncertain."),
            ActionOutcomeKind.Deferred => ErrorResponse(
                503,
                "endpoint_deferred",
                "The Permission endpoint action was deferred."),
            _ => ErrorResponse(
                500,
                "endpoint_unknown",
                "The Permission endpoint action returned an unknown outcome."),
        };

    private static HttpEndpointResponse ErrorResponse(
        int statusCode,
        string code,
        string message) =>
        HttpEndpointResponse.Json(
            statusCode,
            JsonSerializer.SerializeToElement(new { error = code, message }));

    private sealed record RouteDefinition(
        EndpointRouteDescriptor Descriptor,
        string Operation);
}
