using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.Context;

public sealed class ContextEndpointContribution
{
    public const string CreateThreadRoute = "/sharpclaw/context/threads";
    public const string ReadHistoryRoute = "/sharpclaw/context/history";
    public const string CommitExchangeRoute = "/sharpclaw/context/exchanges";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(CreateThreadRoute, CreateThreadAsync);
        endpoints.MapPost(ReadHistoryRoute, ReadHistoryAsync);
        endpoints.MapPost(CommitExchangeRoute, CommitExchangeAsync);
    }

    private static async Task<IResult> CreateThreadAsync(
        ContextCreateThreadAction action,
        HttpContext context,
        IContextActionExecutor executor,
        CancellationToken ct)
    {
        try
        {
            var thread = await executor.CreateThreadAsync(Caller(context), action, ct);
            return Results.Ok(thread);
        }
        catch (Exception exception) when (IsClientFailure(exception))
        {
            return Failure(exception);
        }
    }

    private static async Task<IResult> ReadHistoryAsync(
        ContextReadHistoryAction action,
        HttpContext context,
        IContextActionExecutor executor,
        CancellationToken ct)
    {
        try
        {
            var messages = await executor.ReadHistoryAsync(Caller(context), action, ct);
            return Results.Ok(messages);
        }
        catch (Exception exception) when (IsClientFailure(exception))
        {
            return Failure(exception);
        }
    }

    private static async Task<IResult> CommitExchangeAsync(
        ContextCommitExchangeAction action,
        HttpContext context,
        IContextActionExecutor executor,
        CancellationToken ct)
    {
        try
        {
            return Results.Ok(await executor.CommitExchangeAsync(Caller(context), action, ct));
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
