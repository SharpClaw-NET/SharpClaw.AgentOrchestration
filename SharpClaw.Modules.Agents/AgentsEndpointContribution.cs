using System.Text.Json;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Modules.Agents;

public sealed class AgentsEndpointContribution(
    AgentsApiActionTerminal terminal) : IHttpEndpointHandler
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
        Route(AgentRoutes[0], "GET", AgentsApiOperations.ListAgents),
        Route(AgentRoutes[1], "GET", AgentsApiOperations.GetAgent),
        Route(AgentRoutes[2], "POST", AgentsApiOperations.CreateAgent),
        Route(AgentRoutes[3], "PUT", AgentsApiOperations.UpdateAgent),
        Route(AgentRoutes[4], "DELETE", AgentsApiOperations.DeleteAgent),
        Route(AgentRoutes[5], "POST", AgentsApiOperations.AssignRole),
        Route(AgentRoutes[6], "POST", AgentsApiOperations.SynchronizeAgent),
        Route(AgentRoutes[7], "GET", AgentsApiOperations.GetCost),
        Route(SkillRoutes[0], "GET", AgentsApiOperations.ListSkills),
        Route(SkillRoutes[1], "GET", AgentsApiOperations.GetSkill),
        Route(SkillRoutes[2], "POST", AgentsApiOperations.SaveSkill),
        Route(SkillRoutes[3], "DELETE", AgentsApiOperations.DeleteSkill),
        Route(SkillRoutes[4], "POST", AgentsApiOperations.AccessSkill),
        Route(MemoryRoutes[0], "POST", AgentsApiOperations.WriteMemory),
        Route(MemoryRoutes[1], "POST", AgentsApiOperations.SearchMemory),
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
                "The Agents endpoint route is not registered.");
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
                "The Agents endpoint payload is not valid JSON.");
        }

        try
        {
            IActionOutcome<JsonElement> outcome = await hostActionEntry.InvokeAsync(
                new HostActionEntryRequest<AgentsApiAction, JsonElement>(
                    AgentsModule.ApiDescriptor,
                    new AgentsApiAction(route.Operation, payload),
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
            return ErrorResponse(403, "endpoint_forbidden", "The Agents endpoint request is not authorized.");
        }
        catch (ArgumentException)
        {
            return ErrorResponse(400, "endpoint_invalid_request", "The Agents endpoint request is invalid.");
        }
        catch (InvalidOperationException)
        {
            return ErrorResponse(404, "endpoint_resource_not_found", "The Agents endpoint resource was not found.");
        }
    }

    private static RouteDefinition Route(string path, string method, string operation) =>
        new(
            new EndpointRouteDescriptor(
                $"{AgentsModule.ModuleIdValue}:http:{method}:{path}",
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
                "The Agents endpoint action was cancelled."),
            ActionOutcomeKind.Failed => ErrorResponse(
                500,
                outcome.Error?.Code ?? "endpoint_failed",
                "The Agents endpoint action failed."),
            ActionOutcomeKind.Uncertain => ErrorResponse(
                503,
                outcome.Uncertainty?.Code ?? "endpoint_uncertain",
                "The Agents endpoint action result is uncertain."),
            ActionOutcomeKind.Deferred => ErrorResponse(
                503,
                "endpoint_deferred",
                "The Agents endpoint action was deferred."),
            _ => ErrorResponse(
                500,
                "endpoint_unknown",
                "The Agents endpoint action returned an unknown outcome."),
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
