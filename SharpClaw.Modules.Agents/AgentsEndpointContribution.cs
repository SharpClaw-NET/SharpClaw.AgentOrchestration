using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.Agents;

public sealed class AgentsEndpointContribution
{
    public const string CreateRoute = "/sharpclaw/agents";
    public const string UpdateRoute = "/sharpclaw/agents/update";
    public const string WriteMemoryRoute = "/sharpclaw/agents/memory";
    public const string SearchMemoryRoute = "/sharpclaw/agents/memory/search";
    public const string SaveSkillRoute = "/sharpclaw/skills";
    public const string AccessSkillRoute = "/sharpclaw/skills/access";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(CreateRoute, CreateAsync);
        endpoints.MapPost(UpdateRoute, UpdateAsync);
        endpoints.MapPost(WriteMemoryRoute, WriteMemoryAsync);
        endpoints.MapPost(SearchMemoryRoute, SearchMemoryAsync);
        endpoints.MapPost(SaveSkillRoute, SaveSkillAsync);
        endpoints.MapPost(AccessSkillRoute, AccessSkillAsync);
    }

    private static async Task<IResult> CreateAsync(
        AgentsCreateAction action,
        HttpContext context,
        IAgentsActionExecutor executor,
        CancellationToken ct)
    {
        try
        {
            return Results.Ok(await executor.CreateAsync(Caller(context), action, ct));
        }
        catch (Exception exception) when (IsClientFailure(exception))
        {
            return Failure(exception);
        }
    }

    private static async Task<IResult> UpdateAsync(
        AgentsUpdateAction action,
        HttpContext context,
        IAgentsActionExecutor executor,
        CancellationToken ct)
    {
        try
        {
            var agent = await executor.UpdateAsync(Caller(context), action, ct);
            return agent is null
                ? Results.NotFound()
                : Results.Ok(agent);
        }
        catch (Exception exception) when (IsClientFailure(exception))
        {
            return Failure(exception);
        }
    }

    private static async Task<IResult> WriteMemoryAsync(
        AgentsWriteMemoryAction action,
        HttpContext context,
        IAgentsActionExecutor executor,
        CancellationToken ct)
    {
        try
        {
            return Results.Ok(await executor.WriteMemoryAsync(Caller(context), action, ct));
        }
        catch (Exception exception) when (IsClientFailure(exception))
        {
            return Failure(exception);
        }
    }

    private static async Task<IResult> SearchMemoryAsync(
        AgentsSearchMemoryAction action,
        HttpContext context,
        IAgentsActionExecutor executor,
        CancellationToken ct)
    {
        try
        {
            return Results.Ok(await executor.SearchMemoryAsync(Caller(context), action, ct));
        }
        catch (Exception exception) when (IsClientFailure(exception))
        {
            return Failure(exception);
        }
    }

    private static async Task<IResult> SaveSkillAsync(
        AgentsSaveSkillAction action,
        HttpContext context,
        IAgentsActionExecutor executor,
        CancellationToken ct)
    {
        try
        {
            return Results.Ok(await executor.SaveSkillAsync(Caller(context), action, ct));
        }
        catch (Exception exception) when (IsClientFailure(exception))
        {
            return Failure(exception);
        }
    }

    private static async Task<IResult> AccessSkillAsync(
        AgentsAccessSkillAction action,
        HttpContext context,
        IAgentsActionExecutor executor,
        CancellationToken ct)
    {
        try
        {
            return Results.Ok(await executor.AccessSkillAsync(Caller(context), action, ct));
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
