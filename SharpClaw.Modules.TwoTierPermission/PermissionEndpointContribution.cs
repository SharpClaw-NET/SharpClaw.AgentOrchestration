using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.TwoTierPermission;

public sealed class PermissionEndpointContribution
{
    public const string EvaluateRoute = "/sharpclaw/permission/evaluate";
    public const string GrantRoute = "/sharpclaw/permission/grants";
    public const string RevokeRoute = "/sharpclaw/permission/grants/revoke";
    public const string ApproveRoute = "/sharpclaw/permission/grants/approve";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(EvaluateRoute, EvaluateAsync);
        endpoints.MapPost(GrantRoute, GrantAsync);
        endpoints.MapPost(RevokeRoute, RevokeAsync);
        endpoints.MapPost(ApproveRoute, ApproveAsync);
    }

    private static async Task<IResult> EvaluateAsync(
        PermissionEvaluateAction action,
        HttpContext context,
        IPermissionActionExecutor executor,
        CancellationToken ct)
    {
        try
        {
            return Results.Ok(await executor.EvaluateAsync(Caller(context), action, ct));
        }
        catch (Exception exception) when (IsClientFailure(exception))
        {
            return Failure(exception);
        }
    }

    private static async Task<IResult> GrantAsync(
        PermissionGrantAction action,
        HttpContext context,
        IPermissionActionExecutor executor,
        CancellationToken ct)
    {
        try
        {
            return Results.Ok(await executor.GrantAsync(Caller(context), action, ct));
        }
        catch (Exception exception) when (IsClientFailure(exception))
        {
            return Failure(exception);
        }
    }

    private static async Task<IResult> RevokeAsync(
        PermissionRevokeAction action,
        HttpContext context,
        IPermissionActionExecutor executor,
        CancellationToken ct)
    {
        try
        {
            return Results.Ok(await executor.RevokeAsync(Caller(context), action, ct));
        }
        catch (Exception exception) when (IsClientFailure(exception))
        {
            return Failure(exception);
        }
    }

    private static async Task<IResult> ApproveAsync(
        PermissionApproveAction action,
        HttpContext context,
        IPermissionActionExecutor executor,
        CancellationToken ct)
    {
        try
        {
            return Results.Ok(await executor.ApproveAsync(Caller(context), action, ct));
        }
        catch (Exception exception) when (IsClientFailure(exception))
        {
            return Failure(exception);
        }
    }

    private static RequestPrincipal Caller(HttpContext context)
    {
        var subjectId = context.User.FindFirst("sub")?.Value
            ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? string.Empty;
        var roles = context.User.Claims
            .Where(claim => claim.Type == ClaimTypes.Role
                || claim.Type.Equals("role", StringComparison.OrdinalIgnoreCase))
            .Select(claim => claim.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new RequestPrincipal(
            subjectId,
            Roles: roles,
            IsAuthenticated: context.User.Identity?.IsAuthenticated == true
                && !string.IsNullOrWhiteSpace(subjectId));
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
}
