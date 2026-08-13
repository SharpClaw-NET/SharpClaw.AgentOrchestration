using System.Security.Claims;
using System.Text.Json;
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

    public static IReadOnlyList<string> AgentRoutes { get; } =
    [
        "/sharpclaw/agents/list",
        "/sharpclaw/agents/get",
        CreateRoute,
        UpdateRoute,
        "/sharpclaw/agents/delete",
        "/sharpclaw/agents/role",
        "/sharpclaw/agents/synchronize",
        "/sharpclaw/agents/cost",
    ];

    public static IReadOnlyList<string> SkillRoutes { get; } =
    [
        "/sharpclaw/skills/list",
        "/sharpclaw/skills/get",
        SaveSkillRoute,
        "/sharpclaw/skills/delete",
        AccessSkillRoute,
    ];

    public static IReadOnlyList<string> MemoryRoutes { get; } =
    [
        WriteMemoryRoute,
        SearchMemoryRoute,
    ];

    private static IReadOnlyList<RouteDefinition> Routes { get; } =
    [
        new(AgentRoutes[0], "GET", AgentsApiOperations.ListAgents),
        new(AgentRoutes[1], "GET", AgentsApiOperations.GetAgent),
        new(AgentRoutes[2], "POST", AgentsApiOperations.CreateAgent),
        new(AgentRoutes[3], "PUT", AgentsApiOperations.UpdateAgent),
        new(AgentRoutes[4], "DELETE", AgentsApiOperations.DeleteAgent),
        new(AgentRoutes[5], "POST", AgentsApiOperations.AssignRole),
        new(AgentRoutes[6], "POST", AgentsApiOperations.SynchronizeAgent),
        new(AgentRoutes[7], "GET", AgentsApiOperations.GetCost),
        new(SkillRoutes[0], "GET", AgentsApiOperations.ListSkills),
        new(SkillRoutes[1], "GET", AgentsApiOperations.GetSkill),
        new(SkillRoutes[2], "POST", AgentsApiOperations.SaveSkill),
        new(SkillRoutes[3], "DELETE", AgentsApiOperations.DeleteSkill),
        new(SkillRoutes[4], "POST", AgentsApiOperations.AccessSkill),
        new(MemoryRoutes[0], "POST", AgentsApiOperations.WriteMemory),
        new(MemoryRoutes[1], "POST", AgentsApiOperations.SearchMemory),
    ];

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        foreach (var route in Routes)
        {
            endpoints.MapMethods(
                route.Path,
                [route.Method],
                (HttpContext context, IAgentsActionGateway gateway, CancellationToken ct) =>
                    DispatchAsync(route.Operation, context, gateway, ct));
        }
    }

    private static async Task<IResult> DispatchAsync(
        string operation,
        HttpContext context,
        IAgentsActionGateway gateway,
        CancellationToken ct)
    {
        try
        {
            var payload = await context.Request.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                payload = JsonSerializer.SerializeToElement(new { });
            var result = await gateway.ExecuteAsync(Caller(context), operation, payload, ct);
            return Results.Ok(result);
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

    private sealed record RouteDefinition(string Path, string Method, string Operation);
}
