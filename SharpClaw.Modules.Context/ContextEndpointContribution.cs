using System.Text.Json;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.Context;

public sealed class ContextEndpointContribution(
    ContextApiActionTerminal terminal) : IModuleHttpEndpointHandler
{
    private static readonly JsonElement EmptyPayload =
        JsonSerializer.SerializeToElement(new { });

    public const string CreateThreadRoute = "/sharpclaw/context/threads";
    public const string ReadHistoryRoute = "/sharpclaw/context/history";
    public const string CommitExchangeRoute = "/sharpclaw/context/exchanges";

    public static IReadOnlyList<string> ChannelRoutes { get; } =
    [
        "/sharpclaw/context/channels",
        "/sharpclaw/context/channels/list",
        "/sharpclaw/context/channels/get",
        "/sharpclaw/context/channels/create",
        "/sharpclaw/context/channels/update",
        "/sharpclaw/context/channels/delete",
        "/sharpclaw/context/channels/assign",
        "/sharpclaw/context/channels/unassign",
        "/sharpclaw/context/channels/opt-in",
        "/sharpclaw/context/channels/opt-out",
        "/sharpclaw/context/channels/permissions",
        "/sharpclaw/context/channels/synchronize",
        "/sharpclaw/context/channels/refresh",
    ];

    public static IReadOnlyList<string> ChannelContextRoutes { get; } =
    [
        "/sharpclaw/context/channel-contexts",
        "/sharpclaw/context/channel-contexts/list",
        "/sharpclaw/context/channel-contexts/get",
        "/sharpclaw/context/channel-contexts/create",
        "/sharpclaw/context/channel-contexts/update",
        "/sharpclaw/context/channel-contexts/delete",
        "/sharpclaw/context/channel-contexts/assign",
        "/sharpclaw/context/channel-contexts/unassign",
        "/sharpclaw/context/channel-contexts/activate",
        "/sharpclaw/context/channel-contexts/deactivate",
        "/sharpclaw/context/channel-contexts/permissions",
        "/sharpclaw/context/channel-contexts/synchronize",
    ];

    public static IReadOnlyList<string> ThreadRoutes { get; } =
    [
        "/sharpclaw/context/threads/list",
        "/sharpclaw/context/threads/get",
        "/sharpclaw/context/threads/create",
        "/sharpclaw/context/threads/update",
        "/sharpclaw/context/threads/delete",
    ];

    private static IReadOnlyList<RouteDefinition> Routes { get; } =
    [
        Route(CreateThreadRoute, "POST", ContextApiOperations.CreateThread),
        Route(ReadHistoryRoute, "POST", ContextApiOperations.ReadHistory),
        Route(CommitExchangeRoute, "POST", ContextApiOperations.CommitExchange),
        Route(ChannelRoutes[0], "GET", ContextApiOperations.ListChannels),
        Route(ChannelRoutes[1], "GET", ContextApiOperations.ListChannels),
        Route(ChannelRoutes[2], "GET", ContextApiOperations.GetChannel),
        Route(ChannelRoutes[3], "POST", ContextApiOperations.CreateChannel),
        Route(ChannelRoutes[4], "PUT", ContextApiOperations.UpdateChannel),
        Route(ChannelRoutes[5], "DELETE", ContextApiOperations.DeleteChannel),
        Route(ChannelRoutes[6], "POST", ContextApiOperations.AssignChannel),
        Route(ChannelRoutes[7], "POST", ContextApiOperations.UnassignChannel),
        Route(ChannelRoutes[8], "POST", ContextApiOperations.OptInChannel),
        Route(ChannelRoutes[9], "POST", ContextApiOperations.OptOutChannel),
        Route(ChannelRoutes[10], "GET", ContextApiOperations.ChannelPermissions),
        Route(ChannelRoutes[11], "POST", ContextApiOperations.SynchronizeChannel),
        Route(ChannelRoutes[12], "POST", ContextApiOperations.SynchronizeChannel),
        Route(ChannelContextRoutes[0], "GET", ContextApiOperations.ListContexts),
        Route(ChannelContextRoutes[1], "GET", ContextApiOperations.ListContexts),
        Route(ChannelContextRoutes[2], "GET", ContextApiOperations.GetContext),
        Route(ChannelContextRoutes[3], "POST", ContextApiOperations.CreateContext),
        Route(ChannelContextRoutes[4], "PUT", ContextApiOperations.UpdateContext),
        Route(ChannelContextRoutes[5], "DELETE", ContextApiOperations.DeleteContext),
        Route(ChannelContextRoutes[6], "POST", ContextApiOperations.AssignContext),
        Route(ChannelContextRoutes[7], "POST", ContextApiOperations.UnassignContext),
        Route(ChannelContextRoutes[8], "POST", ContextApiOperations.ActivateContext),
        Route(ChannelContextRoutes[9], "POST", ContextApiOperations.DeactivateContext),
        Route(ChannelContextRoutes[10], "GET", ContextApiOperations.ContextPermissions),
        Route(ChannelContextRoutes[11], "POST", ContextApiOperations.SynchronizeContext),
        Route(ThreadRoutes[0], "GET", ContextApiOperations.ListThreads),
        Route(ThreadRoutes[1], "GET", ContextApiOperations.GetThread),
        Route(ThreadRoutes[2], "POST", ContextApiOperations.CreateThread),
        Route(ThreadRoutes[3], "PUT", ContextApiOperations.UpdateThread),
        Route(ThreadRoutes[4], "DELETE", ContextApiOperations.DeleteThread),
    ];

    public static IReadOnlyList<ModuleEndpointRouteDescriptor> EndpointRoutes { get; } =
        Routes.Select(route => route.Descriptor).ToArray();

    public async ValueTask<ModuleHttpEndpointResponse> InvokeAsync(
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
                "The Context endpoint route is not registered.");
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
                "The Context endpoint payload is not valid JSON.");
        }

        try
        {
            IActionOutcome<JsonElement> outcome = await hostActionEntry.InvokeAsync(
                new HostActionEntryRequest<ContextApiAction, JsonElement>(
                    ContextModule.ApiDescriptor,
                    new ContextApiAction(route.Operation, payload),
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
            return ErrorResponse(403, "endpoint_forbidden", "The Context endpoint request is not authorized.");
        }
        catch (ArgumentException)
        {
            return ErrorResponse(400, "endpoint_invalid_request", "The Context endpoint request is invalid.");
        }
        catch (InvalidOperationException)
        {
            return ErrorResponse(404, "endpoint_resource_not_found", "The Context endpoint resource was not found.");
        }
    }

    private static RouteDefinition Route(string path, string method, string operation) =>
        new(
            new ModuleEndpointRouteDescriptor(
                $"{ContextModule.ModuleIdValue}:http:{method}:{path}",
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

    private static ModuleHttpEndpointResponse ToResponse(IActionOutcome<JsonElement> outcome) =>
        outcome.Kind switch
        {
            ActionOutcomeKind.Completed when outcome.Result is { } result =>
                ModuleHttpEndpointResponse.Json(200, result),
            ActionOutcomeKind.Cancelled => ErrorResponse(
                409,
                "endpoint_cancelled",
                "The Context endpoint action was cancelled."),
            ActionOutcomeKind.Failed => ErrorResponse(
                500,
                outcome.Error?.Code ?? "endpoint_failed",
                "The Context endpoint action failed."),
            ActionOutcomeKind.Uncertain => ErrorResponse(
                503,
                outcome.Uncertainty?.Code ?? "endpoint_uncertain",
                "The Context endpoint action result is uncertain."),
            ActionOutcomeKind.Deferred => ErrorResponse(
                503,
                "endpoint_deferred",
                "The Context endpoint action was deferred."),
            _ => ErrorResponse(
                500,
                "endpoint_unknown",
                "The Context endpoint action returned an unknown outcome."),
        };

    private static ModuleHttpEndpointResponse ErrorResponse(
        int statusCode,
        string code,
        string message) =>
        ModuleHttpEndpointResponse.Json(
            statusCode,
            JsonSerializer.SerializeToElement(new { error = code, message }));

    private sealed record RouteDefinition(
        ModuleEndpointRouteDescriptor Descriptor,
        string Operation);
}
