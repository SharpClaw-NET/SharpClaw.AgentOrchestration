using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;

namespace SharpClaw.Modules.Agents;

public sealed class AgentsEndpointContribution(
    AgentsApiActionTerminal terminal) : IModuleEndpointHandler
{
    private static readonly JsonElement EmptyPayload =
        JsonSerializer.SerializeToElement(new { });

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

    public async ValueTask<ModuleEndpointResult> InvokeAsync(
        HostEndpointInvocation invocation,
        IHostActionEntry hostActionEntry,
        CancellationToken cancellationToken)
    {
        var request = new HostActionEntryRequest<AgentsApiAction, JsonElement>(
            AgentsModule.ApiDescriptor,
            new AgentsApiAction(AgentsApiOperations.ListAgents, EmptyPayload),
            invocation.HostActionContext);
        var outcome = await hostActionEntry.InvokeAsync(
            request,
            terminal,
            cancellationToken);
        return ToResult(outcome);
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

    private static ModuleEndpointResult ToResult(IActionOutcome<JsonElement> outcome) =>
        outcome.Kind switch
        {
            ActionOutcomeKind.Completed when outcome.Result is { } result =>
                ModuleEndpointResult.Success(result),
            ActionOutcomeKind.Cancelled => ModuleEndpointResult.Failure(
                "endpoint_cancelled", "The agents endpoint action was cancelled."),
            ActionOutcomeKind.Failed => ModuleEndpointResult.Failure(
                outcome.Error?.Code ?? "endpoint_failed",
                outcome.Error?.Message ?? "The agents endpoint action failed."),
            ActionOutcomeKind.Uncertain => ModuleEndpointResult.Failure(
                outcome.Uncertainty?.Code ?? "endpoint_uncertain",
                outcome.Uncertainty?.Message ?? "The agents endpoint action is uncertain."),
            ActionOutcomeKind.Deferred => ModuleEndpointResult.Failure(
                "endpoint_deferred", "The agents endpoint action was deferred."),
            _ => ModuleEndpointResult.Failure(
                "endpoint_unknown", "The agents endpoint action returned an unknown outcome."),
        };

    private sealed record RouteDefinition(string Path, string Method, string Operation);
}
