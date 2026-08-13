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

    private static readonly JsonElement EmptySchema = ToolSchemas.EmptyObject;
    private static readonly ActionRepeatPolicy RepeatPolicy =
        new(ActionRepeatKind.Idempotent, 3, TimeSpan.FromMilliseconds(50), "context");

    public ModuleIdentity Identity { get; } = new(ModuleIdValue, "SharpClaw Context", "ctx");

    public void Configure(ISharpClawModuleBuilder module)
    {
        module.Services.AddScoped<ContextStore>();
        module.Services.AddScoped<IConversationStore>(sp => sp.GetRequiredService<ContextStore>());
        module.Services.AddScoped<IConversationResolver, ContextConversationResolver>();
        module.Services.AddScoped<IChatContextContributor, ContextHistoryContributor>();
        module.Services.AddScoped<ContextToolHandler>();
        module.Services.AddScoped<ContextCliHandler>();
        module.Services.AddScoped<ContextDbContextAccessor>();

        module.Contracts.Export<ContextModuleContract>("sharpclaw.context");
        module.Contracts.Require<PermissionModuleContract>("sharpclaw.permission");

        foreach (var storage in StorageContracts)
            module.Storage.Add(storage);

        module.Actions.Add(new ActionDescriptor<ContextCreateThreadAction, ContextThreadRecord>(
            new("context.thread.create"), 1, "context.thread",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Observe,
            false, false, RepeatPolicy, null, TimeSpan.FromSeconds(30)));
        module.Actions.Add(new ActionDescriptor<ContextReadHistoryAction, IReadOnlyList<ContextMessageRecord>>(
            new("context.thread.read-history"), 1, "context.thread",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Observe,
            false, false, RepeatPolicy, null, TimeSpan.FromSeconds(30)));
        module.Actions.Add(new ActionDescriptor<ContextCommitExchangeAction, bool>(
            new("context.conversation.commit"), 1, "context.conversation",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Observe,
            false, false, RepeatPolicy, null, TimeSpan.FromSeconds(30)));

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
        application.Cli.Add<ContextCliHandler>(new ModuleCliCommandDescriptor(
            "ctx-thread-list",
            ["ctxthreads"],
            "List accessible Context threads.",
            new JsonSchemaReference("sharpclaw.context.cli.arguments", 1),
            new JsonSchemaReference("sharpclaw.context.cli.result", 1)));
    }

    public ValueTask StartAsync(ModuleStartContext context, CancellationToken ct) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken ct) => ValueTask.CompletedTask;

    public static IReadOnlyList<ModuleStorageContractDescriptor> StorageContracts =>
    [
        Storage(ContextStore.ChannelsStorage, "Context channel ownership and cross-thread opt-in.",
            [new("ownerAgentId", ModuleStorageIndexValueKind.String), new("optedIn", ModuleStorageIndexValueKind.Bool)]),
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
