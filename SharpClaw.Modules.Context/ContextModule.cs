using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Persistence;
using SharpClaw.ModuleSDK;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.Context;

public sealed class ContextModule : ISharpClawModule
{
    public const string ModuleIdValue = "sharpclaw_context";
    public const string ListThreadsTool = "ctx_list_accessible_threads";
    public const string ReadHistoryTool = "ctx_read_thread_history";
    public const string ThreadChangedEvent = "context.thread.changed";
    public const string ExchangeCommittedEvent = "context.exchange.committed";
    public static readonly Guid ApiTerminalId = Guid.Parse("8f7be0a6-2f4d-5b72-9dc8-3ca4e9c2f001");
    public static readonly Guid SteeringRecordTerminalId = Guid.Parse("8f7be0a6-2f4d-5b72-9dc8-3ca4e9c2f002");
    public static readonly Guid SteeringListTerminalId = Guid.Parse("8f7be0a6-2f4d-5b72-9dc8-3ca4e9c2f003");

    private static readonly JsonElement EmptySchema = ToolSchemas.EmptyObject;
    private static readonly ActionRepeatPolicy RepeatPolicy =
        new(ActionRepeatKind.Idempotent, 3, TimeSpan.FromMilliseconds(50), "context");
    private static readonly IReadOnlyList<ActionSafePoint> SafePoints =
    [
        ActionSafePoint.BeforeTerminal,
        ActionSafePoint.AfterTerminal,
        ActionSafePoint.BeforeCommit,
        ActionSafePoint.AfterCommit,
    ];

    public static readonly ActionDescriptor<ContextApiAction, JsonElement> ApiDescriptor =
        new(new("context.api.dispatch"), 1, "context.api",
            ActionInterceptionCapabilities.Inspect
            | ActionInterceptionCapabilities.Cancel
            | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            InputSchema = new JsonSchemaReference(
                "sharpclaw.kernel.action.input.context.api.dispatch",
                1,
                "941361CD8AD62ECC21CD1B23957542A777266F05009DD3A8849608C8F52FD961"),
            ResultSchema = new JsonSchemaReference(
                "sharpclaw.kernel.action.result.context.api.dispatch",
                1,
                "8EB000590FB81F540B757EE45A1DB72EF84BF921444663E7AAF07B0F2711CB8D"),
            SafePoints = SafePoints,
        };

    public ModuleIdentity Identity { get; } = new(ModuleIdValue, "SharpClaw Context", "ctx");

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<ContextStore>();
        services.AddScoped<IConversationStore>(sp => sp.GetRequiredService<ContextStore>());
        services.AddScoped<ContextConversationResolver>();
        services.AddScoped<IConversationResolver>(sp =>
            sp.GetRequiredService<ContextConversationResolver>());
        services.AddScoped<ContextHistoryContributor>();
        services.AddScoped<IChatContextContributor>(sp =>
            sp.GetRequiredService<ContextHistoryContributor>());
        services.AddScoped<ContextToolHandler>();
        services.AddScoped<IContextActionExecutor, ContextActionExecutor>();
        services.AddScoped<IContextSteeringActionExecutor, ContextSteeringActionExecutor>();
        services.AddScoped<IContextSteeringActionGateway, ContextSteeringActionGateway>();
        services.AddScoped<ContextApiActionExecutor>();
        services.AddScoped<ContextEndpointContribution>();
        services.AddScoped<IContextActionGateway, ContextActionGateway>();
        services.AddScoped<HostActionInvoker>();
        services.AddScoped<ContextCommitAuthorizationHook>();
        services.AddSingleton<ContextCliHandler>();

        services.ExportContract<ContextCapabilityContract>("sharpclaw.context");
        services.RequireAuthorization();

        foreach (var storage in StorageContracts)
            services.AddStorage(storage);

        services.AddAction(ApiDescriptor)
            .UseTerminal<ContextApiActionTerminal>(ApiTerminalId);
        services.AddAction(ContextSteeringActionDescriptors.Record)
            .UseTerminal<ContextSteeringRecordActionTerminal>(SteeringRecordTerminalId);
        services.AddAction(ContextSteeringActionDescriptors.List)
            .UseTerminal<ContextSteeringListActionTerminal>(SteeringListTerminalId);

        services.AddAction(new ActionDescriptor<ContextCreateThreadAction, ContextThreadRecord>(
            new("context.thread.create"), 1, "context.thread",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Observe,
            false, false, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            SafePoints = SafePoints,
        });
        services.AddAction(new ActionDescriptor<ContextReadHistoryAction, IReadOnlyList<ContextMessageRecord>>(
            new("context.thread.read-history"), 1, "context.thread",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Observe,
            false, false, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            SafePoints = SafePoints,
        });
        services.AddAction(new ActionDescriptor<ContextCommitExchangeAction, bool>(
            new("context.conversation.commit"), 1, "context.conversation",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel,
            true, false, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            SafePoints = SafePoints,
        });
        services.OnAction(new SharpClawActionKey("context.conversation.commit"))
            .Use<ContextCommitAuthorizationHook>(new HookOrdering("context.conversation.commit.authorization"));
        services.AddEvent(new EventDescriptor<ContextThreadChangedEvent>(
            new(ThreadChangedEvent), 1, "context.thread",
            EventInterceptionCapabilities.Inspect | EventInterceptionCapabilities.Observe,
            true, false)
        {
            DeliveryClasses = [EventDelivery.Durable],
        });
        services.AddEvent(new EventDescriptor<ContextExchangeCommittedEvent>(
            new(ExchangeCommittedEvent), 1, "context.conversation",
            EventInterceptionCapabilities.Inspect | EventInterceptionCapabilities.Observe,
            true, true)
        {
            DeliveryClasses = [EventDelivery.Durable],
        });

        services.AddTool<ContextToolHandler>(new ToolDescriptor(
            ListThreadsTool,
            "List threads that the caller can read outside the current channel.",
            BuildListSchema()));
        services.AddTool<ContextToolHandler>(new ToolDescriptor(
            ReadHistoryTool,
            "Read bounded history from one accessible thread.",
            BuildReadSchema()));
        services.UseConversationResolver<ContextConversationResolver>(
            new ExclusiveClaim("sharpclaw.context.conversation-resolver"));
        services.AddChatContext<ContextHistoryContributor>();

        foreach (EndpointRouteDescriptor route in ContextEndpointContribution.EndpointRoutes)
            services.AddHttpEndpoint<ContextEndpointContribution>(route);
        foreach (var command in ContextCliHandler.Commands)
        {
            services.AddCliCommand<ContextCliHandler>(new CliCommandDescriptor(
                command.Name,
                command.Name.Equals("ctx-thread-list", StringComparison.OrdinalIgnoreCase)
                    ? ["ctxthreads"]
                    : [],
                $"Execute the Context {command.Operation} operation.",
                new JsonSchemaReference("sharpclaw.context.cli.arguments", 1),
                new JsonSchemaReference("sharpclaw.context.cli.result", 1)));
        }
    }

    public ValueTask StartAsync(ServiceStartContext context, CancellationToken ct) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken ct) => ValueTask.CompletedTask;

    public static IReadOnlyList<ScopedStorageContractDescriptor> StorageContracts =>
    [
        Storage(ContextStore.ChannelsStorage, "Context channel ownership and cross-thread opt-in.",
            [new("ownerAgentId", ScopedStorageIndexValueKind.String), new("contextId", ScopedStorageIndexValueKind.String), new("optedIn", ScopedStorageIndexValueKind.Bool), new("updatedAt", ScopedStorageIndexValueKind.DateTime)]),
        Storage(ContextStore.ContextsStorage, "Context ownership and default-agent assignment.",
            [new("defaultAgentId", ScopedStorageIndexValueKind.String), new("updatedAt", ScopedStorageIndexValueKind.DateTime)]),
        Storage(ContextStore.ThreadsStorage, "Thread identity, channel identity, and update order.",
            [new("channelId", ScopedStorageIndexValueKind.String), new("contextId", ScopedStorageIndexValueKind.String), new("updatedAt", ScopedStorageIndexValueKind.DateTime)]),
        Storage(ContextStore.MessagesStorage, "Ordered conversation history records.",
            [new("threadId", ScopedStorageIndexValueKind.String), new("channelId", ScopedStorageIndexValueKind.String), new("createdAt", ScopedStorageIndexValueKind.DateTime)]),
        Storage(ContextStore.SteeringStorage, "Explicit channel and thread steering records.",
            [new("channelId", ScopedStorageIndexValueKind.String), new("threadId", ScopedStorageIndexValueKind.String), new("scope", ScopedStorageIndexValueKind.String), new("source", ScopedStorageIndexValueKind.String), new("category", ScopedStorageIndexValueKind.String), new("createdAt", ScopedStorageIndexValueKind.DateTime), new("createdAtId", ScopedStorageIndexValueKind.String)]),
    ];

    private static ScopedStorageContractDescriptor Storage(
        string name,
        string description,
        IReadOnlyList<ScopedStorageIndexDescriptor> indexes) =>
        new(ModuleIdValue, name,
            [
                new(ScopedStorageOperations.Get),
                new(ScopedStorageOperations.Upsert),
                new(ScopedStorageOperations.BatchUpsert),
                new(ScopedStorageOperations.Delete),
                new(ScopedStorageOperations.BatchDelete),
                new(ScopedStorageOperations.List),
                new(ScopedStorageOperations.Query),
            ], description, indexes, 524_288, 500);

    private static JsonElement BuildListSchema() => JsonDocument.Parse("""
        {"type":"object","properties":{"channelId":{"type":"string"}},"required":["channelId"],"additionalProperties":false}
        """).RootElement.Clone();

    private static JsonElement BuildReadSchema() => JsonDocument.Parse("""
        {"type":"object","properties":{"channelId":{"type":"string"},"threadId":{"type":"string"},"maxMessages":{"type":"integer","minimum":1,"maximum":200}},"required":["channelId","threadId"],"additionalProperties":false}
        """).RootElement.Clone();
}
