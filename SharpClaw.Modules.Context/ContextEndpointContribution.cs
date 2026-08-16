using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.Context;

public sealed class ContextEndpointContribution
{
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
        new(CreateThreadRoute, "POST", ContextApiOperations.CreateThread),
        new(ReadHistoryRoute, "POST", ContextApiOperations.ReadHistory),
        new(CommitExchangeRoute, "POST", ContextApiOperations.CommitExchange),
        new(ChannelRoutes[0], "GET", ContextApiOperations.ListChannels),
        new(ChannelRoutes[1], "GET", ContextApiOperations.ListChannels),
        new(ChannelRoutes[2], "GET", ContextApiOperations.GetChannel),
        new(ChannelRoutes[3], "POST", ContextApiOperations.CreateChannel),
        new(ChannelRoutes[4], "PUT", ContextApiOperations.UpdateChannel),
        new(ChannelRoutes[5], "DELETE", ContextApiOperations.DeleteChannel),
        new(ChannelRoutes[6], "POST", ContextApiOperations.AssignChannel),
        new(ChannelRoutes[7], "POST", ContextApiOperations.UnassignChannel),
        new(ChannelRoutes[8], "POST", ContextApiOperations.OptInChannel),
        new(ChannelRoutes[9], "POST", ContextApiOperations.OptOutChannel),
        new(ChannelRoutes[10], "GET", ContextApiOperations.ChannelPermissions),
        new(ChannelRoutes[11], "POST", ContextApiOperations.SynchronizeChannel),
        new(ChannelRoutes[12], "POST", ContextApiOperations.SynchronizeChannel),
        new(ChannelContextRoutes[0], "GET", ContextApiOperations.ListContexts),
        new(ChannelContextRoutes[1], "GET", ContextApiOperations.ListContexts),
        new(ChannelContextRoutes[2], "GET", ContextApiOperations.GetContext),
        new(ChannelContextRoutes[3], "POST", ContextApiOperations.CreateContext),
        new(ChannelContextRoutes[4], "PUT", ContextApiOperations.UpdateContext),
        new(ChannelContextRoutes[5], "DELETE", ContextApiOperations.DeleteContext),
        new(ChannelContextRoutes[6], "POST", ContextApiOperations.AssignContext),
        new(ChannelContextRoutes[7], "POST", ContextApiOperations.UnassignContext),
        new(ChannelContextRoutes[8], "POST", ContextApiOperations.ActivateContext),
        new(ChannelContextRoutes[9], "POST", ContextApiOperations.DeactivateContext),
        new(ChannelContextRoutes[10], "GET", ContextApiOperations.ContextPermissions),
        new(ChannelContextRoutes[11], "POST", ContextApiOperations.SynchronizeContext),
        new(ThreadRoutes[0], "GET", ContextApiOperations.ListThreads),
        new(ThreadRoutes[1], "GET", ContextApiOperations.GetThread),
        new(ThreadRoutes[2], "POST", ContextApiOperations.CreateThread),
        new(ThreadRoutes[3], "PUT", ContextApiOperations.UpdateThread),
        new(ThreadRoutes[4], "DELETE", ContextApiOperations.DeleteThread),
    ];

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        foreach (var route in Routes)
        {
            endpoints.MapMethods(
                route.Path,
                [route.Method],
                (HttpContext context, IContextActionGateway gateway, CancellationToken ct) =>
                    DispatchAsync(route.Operation, context, gateway, ct));
        }
    }

    private static async Task<IResult> DispatchAsync(
        string operation,
        HttpContext context,
        IContextActionGateway gateway,
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
