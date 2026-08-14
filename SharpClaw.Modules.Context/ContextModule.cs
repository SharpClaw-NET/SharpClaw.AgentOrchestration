using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.Context;

public sealed class ContextModule : ISharpClawModule, ISharpClawApplicationModule
{
    public const string ModuleIdValue = "sharpclaw_context";
    public const string ListThreadsTool = "ctx_list_accessible_threads";
    public const string ReadHistoryTool = "ctx_read_thread_history";
    public const string ThreadChangedEvent = "context.thread.changed";
    public const string ExchangeCommittedEvent = "context.exchange.committed";

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
            SafePoints = SafePoints,
        };

    public ModuleIdentity Identity { get; } = new(ModuleIdValue, "SharpClaw Context", "ctx");

    public void Configure(ISharpClawModuleBuilder module)
    {
        module.Services.AddScoped<ContextStore>();
        module.Services.AddScoped<IConversationStore>(sp => sp.GetRequiredService<ContextStore>());
        module.Services.AddScoped<IConversationResolver, ContextConversationResolver>();
        module.Services.AddScoped<IChatContextContributor, ContextHistoryContributor>();
        module.Services.AddScoped<ContextToolHandler>();
        module.Services.AddScoped<IContextActionExecutor, ContextActionExecutor>();
        module.Services.AddScoped<ContextApiActionExecutor>();
        module.Services.AddScoped<IContextActionGateway, ContextActionGateway>();
        module.Services.AddScoped<IModuleActionPipeline, ModuleActionPipeline>();
        module.Services.AddScoped<ContextCommitAuthorizationHook>();
        module.Services.AddScoped<ContextCliHandler>();

        module.Contracts.Export<ContextModuleContract>("sharpclaw.context");
        module.Contracts.Require<PermissionModuleContract>("sharpclaw.permission");
        module.Contracts.Require<IContextAccessPolicy>("sharpclaw.context-access");

        foreach (var storage in StorageContracts)
            module.Storage.Add(storage);

        module.Actions.Add(ApiDescriptor);

        module.Actions.Add(new ActionDescriptor<ContextCreateThreadAction, ContextThreadRecord>(
            new("context.thread.create"), 1, "context.thread",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Observe,
            false, false, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            SafePoints = SafePoints,
        });
        module.Actions.Add(new ActionDescriptor<ContextReadHistoryAction, IReadOnlyList<ContextMessageRecord>>(
            new("context.thread.read-history"), 1, "context.thread",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Observe,
            false, false, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            SafePoints = SafePoints,
        });
        module.Actions.Add(new ActionDescriptor<ContextCommitExchangeAction, bool>(
            new("context.conversation.commit"), 1, "context.conversation",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel,
            true, false, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            SafePoints = SafePoints,
        });
        module.Hooks.For(new SharpClawActionKey("context.conversation.commit"))
            .Use<ContextCommitAuthorizationHook>(new HookOrdering("context.conversation.commit.authorization"));

        module.Events.Add(new EventDescriptor<ContextThreadChangedEvent>(
            new(ThreadChangedEvent), 1, "context.thread",
            EventInterceptionCapabilities.Inspect | EventInterceptionCapabilities.Observe,
            true, false)
        {
            DeliveryClasses = [EventDelivery.Durable],
        });
        module.Events.Add(new EventDescriptor<ContextExchangeCommittedEvent>(
            new(ExchangeCommittedEvent), 1, "context.conversation",
            EventInterceptionCapabilities.Inspect | EventInterceptionCapabilities.Observe,
            true, true)
        {
            DeliveryClasses = [EventDelivery.Durable],
        });

        module.Tools.Add<ContextToolHandler>(new ToolDescriptor(
            ListThreadsTool,
            "List threads that the caller can read outside the current channel.",
            BuildListSchema()));
        module.Tools.Add<ContextToolHandler>(new ToolDescriptor(
            ReadHistoryTool,
            "Read bounded history from one accessible thread.",
            BuildReadSchema()));
        module.Chat.UseConversationResolver<ContextConversationResolver>(
            new ExclusiveRegistration("sharpclaw.context.conversation-resolver"));
        module.Chat.AddContextContributor<ContextHistoryContributor>();
    }

    public void ConfigureApplication(ISharpClawApplicationBuilder application)
    {
        application.Endpoints.Add<ContextEndpointContribution>();
        foreach (var command in ContextCliHandler.Commands)
        {
            application.Cli.Add<ContextCliHandler>(new ModuleCliCommandDescriptor(
                command.Name,
                command.Name.Equals("ctx-thread-list", StringComparison.OrdinalIgnoreCase)
                    ? ["ctxthreads"]
                    : [],
                $"Execute the Context {command.Operation} operation.",
                new JsonSchemaReference("sharpclaw.context.cli.arguments", 1),
                new JsonSchemaReference("sharpclaw.context.cli.result", 1)));
        }
    }

    public ValueTask StartAsync(ModuleStartContext context, CancellationToken ct) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken ct) => ValueTask.CompletedTask;

    public static IReadOnlyList<ModuleStorageContractDescriptor> StorageContracts =>
    [
        Storage(ContextStore.ChannelsStorage, "Context channel ownership and cross-thread opt-in.",
            [new("ownerAgentId", ModuleStorageIndexValueKind.String), new("contextId", ModuleStorageIndexValueKind.String), new("optedIn", ModuleStorageIndexValueKind.Bool)]),
        Storage(ContextStore.ContextsStorage, "Context ownership and default-agent assignment.",
            [new("defaultAgentId", ModuleStorageIndexValueKind.String)]),
        Storage(ContextStore.ThreadsStorage, "Thread identity, channel identity, and update order.",
            [new("channelId", ModuleStorageIndexValueKind.String), new("contextId", ModuleStorageIndexValueKind.String), new("updatedAt", ModuleStorageIndexValueKind.DateTime)]),
        Storage(ContextStore.MessagesStorage, "Ordered conversation history records.",
            [new("threadId", ModuleStorageIndexValueKind.String), new("channelId", ModuleStorageIndexValueKind.String), new("createdAt", ModuleStorageIndexValueKind.DateTime)]),
    ];

    private static ModuleStorageContractDescriptor Storage(
        string name,
        string description,
        IReadOnlyList<ModuleStorageIndexDescriptor> indexes) =>
        new(ModuleIdValue, name,
            [
                new(ModuleStorageOperations.Get),
                new(ModuleStorageOperations.Upsert),
                new(ModuleStorageOperations.BatchUpsert),
                new(ModuleStorageOperations.Delete),
                new(ModuleStorageOperations.BatchDelete),
                new(ModuleStorageOperations.List),
                new(ModuleStorageOperations.Query),
            ], description, indexes, 524_288, 500);

    private static JsonElement BuildListSchema() => JsonDocument.Parse("""
        {"type":"object","properties":{"channelId":{"type":"string"}},"required":["channelId"],"additionalProperties":false}
        """).RootElement.Clone();

    private static JsonElement BuildReadSchema() => JsonDocument.Parse("""
        {"type":"object","properties":{"channelId":{"type":"string"},"threadId":{"type":"string"},"maxMessages":{"type":"integer","minimum":1,"maximum":200}},"required":["channelId","threadId"],"additionalProperties":false}
        """).RootElement.Clone();
}
