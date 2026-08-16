using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.TwoTierPermission;

public sealed class PermissionEndpointContribution
{
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
        new(EvaluateRoute, "POST", PermissionApiOperations.Evaluate),
        new(GrantRoute, "POST", PermissionApiOperations.Grant),
        new(RevokeRoute, "POST", PermissionApiOperations.Revoke),
        new(ApproveRoute, "POST", PermissionApiOperations.Approve),
        new(PolicyRoutes[0], "GET", PermissionApiOperations.ListPolicies),
        new(PolicyRoutes[1], "GET", PermissionApiOperations.GetPolicy),
        new(PolicyRoutes[2], "POST", PermissionApiOperations.SavePolicy),
        new(PolicyRoutes[3], "DELETE", PermissionApiOperations.DeletePolicy),
        new(RoleRoutes[0], "GET", PermissionApiOperations.ListRoles),
        new(RoleRoutes[1], "GET", PermissionApiOperations.GetRole),
        new(RoleRoutes[2], "POST", PermissionApiOperations.SaveRole),
        new(RoleRoutes[3], "DELETE", PermissionApiOperations.DeleteRole),
        new(RoleRoutes[4], "POST", PermissionApiOperations.AssignRole),
        new(PermissionSetRoutes[0], "GET", PermissionApiOperations.ListPermissionSets),
        new(PermissionSetRoutes[1], "GET", PermissionApiOperations.GetPermissionSet),
        new(PermissionSetRoutes[2], "POST", PermissionApiOperations.SavePermissionSet),
        new(PermissionSetRoutes[3], "DELETE", PermissionApiOperations.DeletePermissionSet),
        new(PermissionSetRoutes[4], "POST", PermissionApiOperations.AssignPermissionSet),
    ];

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        foreach (var route in Routes)
        {
            endpoints.MapMethods(
                route.Path,
                [route.Method],
                (HttpContext context, IPermissionActionGateway gateway, CancellationToken ct) =>
                    DispatchAsync(route.Operation, context, gateway, ct));
        }
    }

    private static async Task<IResult> DispatchAsync(
        string operation,
        HttpContext context,
        IPermissionActionGateway gateway,
        CancellationToken ct)
    {
        try
        {
            var payload = await context.Request.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                payload = JsonSerializer.SerializeToElement(new { });
            var hostInvocation = context.RequestServices
                .GetRequiredService<HostEndpointInvocation>();
            var result = await gateway.ExecuteAsync(
                hostInvocation.HostActionContext,
                operation,
                payload,
                ct);
            return Results.Ok(result);
        }
        catch (Exception exception) when (IsClientFailure(exception))
        {
            return Failure(exception);
        }
    }

    private static bool IsClientFailure(Exception exception) =>
        exception is UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException;

    private static IResult Failure(Exception exception) => exception switch
    {
        UnauthorizedAccessException => Results.StatusCode(StatusCodes.Status403Forbidden),
        ArgumentException argument => Results.BadRequest(new { error = argument.Message }),
        InvalidOperationException operation => Results.NotFound(new { error = operation.Message }),
        _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
    };

    private sealed record RouteDefinition(string Path, string Method, string Operation);
}
