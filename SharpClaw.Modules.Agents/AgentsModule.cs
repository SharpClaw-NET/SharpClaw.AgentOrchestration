using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Persistence;
using SharpClaw.ModuleSDK;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.Agents;

public sealed class AgentsModule : ISharpClawModule
{
    public const string ModuleIdValue = "sharpclaw_agents";
    public const string CreateTool = "agents_create";
    public const string UpdateTool = "agents_update";
    public const string AccessSkillTool = "agents_access_skill";
    public const string WriteMemoryTool = "agents_write_memory";
    public const string SearchMemoryTool = "agents_search_memory";
    public const string RecordAgentJobAction = "agents.job.record";
    public const string AttachCanonicalJobAction = "agents.job.attach";
    public const string CompleteAgentJobAction = "agents.job.complete";
    public const string ImportAgentJobsAction = "agents.job.import";
    public const string AgentChangedEvent = "agents.agent.changed";
    public const string SkillChangedEvent = "agents.skill.changed";
    public const string MemoryChangedEvent = "agents.memory.changed";
    public static readonly Guid ApiTerminalId = Guid.Parse("8f7be0a6-2f4d-5b72-9dc8-3ca4e9c2f201");

    private static readonly ActionRepeatPolicy RepeatPolicy =
        new(ActionRepeatKind.Receipted, 3, TimeSpan.FromMilliseconds(100), "agents");
    private static readonly IReadOnlyList<ActionSafePoint> SafePoints =
    [
        ActionSafePoint.BeforeTerminal,
        ActionSafePoint.AfterTerminal,
        ActionSafePoint.BeforeCommit,
        ActionSafePoint.AfterCommit,
    ];

    public static readonly ActionDescriptor<AgentsApiAction, JsonElement> ApiDescriptor =
        new(new("agents.api.dispatch"), 1, "agents.api",
            ActionInterceptionCapabilities.Inspect
            | ActionInterceptionCapabilities.Cancel
            | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            InputSchema = new JsonSchemaReference(
                "sharpclaw.kernel.action.input.agents.api.dispatch",
                1,
                "27B5426804CE4372B54F88B8516A9E545DCF4023778CB8CD8BB9413309544628"),
            ResultSchema = new JsonSchemaReference(
                "sharpclaw.kernel.action.result.agents.api.dispatch",
                1,
                "EBF621A68F0061626C140836F73212CB5D24ABF6D6FE9FAE994E6C3BA794FB65"),
            SafePoints = SafePoints,
        };

    public ModuleIdentity Identity { get; } = new(ModuleIdValue, "SharpClaw Agents", "agents");

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<AgentsCatalog>();
        services.AddScoped<HostActionInvoker>();
        services.AddScoped<AgentsToolHandler>();
        services.AddScoped<AgentsApiActionExecutor>();
        services.AddScoped<AgentsEndpointContribution>();
        services.AddScoped<IAgentsActionGateway, AgentsActionGateway>();
        services.AddScoped<IAgentsActionExecutor, AgentsActionExecutor>();
        services.AddScoped<IAgentsJobActionExecutor, AgentsJobActionExecutor>();
        services.AddScoped<AgentsCreateAuthorizationHook>();
        services.AddSingleton<AgentsCliHandler>();
        services.AddScoped<AgentChatProfileResolver>();

        services.ExportContract<AgentCapabilityContract>("sharpclaw.agents");
        services.RequireContract<ContextCapabilityContract>("sharpclaw.context");
        services.RequireAuthorization();

        foreach (var storage in StorageContracts)
            services.AddStorage(storage);

        services.AddAction(ApiDescriptor)
            .UseTerminal<AgentsApiActionTerminal>(ApiTerminalId);

        services.AddAction(new ActionDescriptor<AgentsCreateAction, AgentRecord>(
            new("agents.create"), 1, "agents",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            SafePoints = SafePoints,
        });
        services.AddAction(new ActionDescriptor<AgentsUpdateAction, AgentRecord?>(
            new("agents.update"), 1, "agents",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            SafePoints = SafePoints,
        });
        services.AddAction(new ActionDescriptor<AgentsWriteMemoryAction, MemoryRecord>(
            new("agents.memory.write"), 1, "agents.memory",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            SafePoints = SafePoints,
        });
        services.AddAction(new ActionDescriptor<AgentsSaveSkillAction, SkillRecord>(
            new("agents.skill.save"), 1, "agents.skills",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            SafePoints = SafePoints,
        });
        services.AddAction(new ActionDescriptor<AgentsAccessSkillAction, string>(
            new("agents.skill.access"), 1, "agents.skills",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Observe,
            false, false, RepeatPolicy, null, TimeSpan.FromSeconds(10))
        {
            SafePoints = SafePoints,
        });
        services.AddAction(new ActionDescriptor<AgentsSearchMemoryAction, IReadOnlyList<MemoryRecord>>(
            new("agents.memory.search"), 1, "agents.memory",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Observe,
            true, false, RepeatPolicy, null, TimeSpan.FromSeconds(10))
        {
            SafePoints = SafePoints,
        });
        services.AddAction(new ActionDescriptor<AgentsRecordJobAction, AgentJob>(
            new(RecordAgentJobAction), 1, "agents.jobs",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            SafePoints = SafePoints,
        });
        services.AddAction(new ActionDescriptor<AgentsAttachCanonicalJobAction, AgentJob>(
            new(AttachCanonicalJobAction), 1, "agents.jobs",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            SafePoints = SafePoints,
        });
        services.AddAction(new ActionDescriptor<AgentsCompleteJobAction, AgentJob>(
            new(CompleteAgentJobAction), 1, "agents.jobs",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            SafePoints = SafePoints,
        });
        services.AddAction(new ActionDescriptor<AgentsImportJobsAction, IReadOnlyList<AgentJob>>(
            new(ImportAgentJobsAction), 1, "agents.jobs",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromMinutes(2))
        {
            SafePoints = SafePoints,
        });
        services.OnAction(new SharpClawActionKey("agents.create"))
            .Use<AgentsCreateAuthorizationHook>(new HookOrdering("agents.create.authorization"));
        services.AddEvent(new EventDescriptor<AgentChangedEvent>(
            new(AgentChangedEvent), 1, "agents",
            EventInterceptionCapabilities.Inspect | EventInterceptionCapabilities.Observe,
            true, true)
        {
            DeliveryClasses = [EventDelivery.Durable],
        });
        services.AddEvent(new EventDescriptor<SkillChangedEvent>(
            new(SkillChangedEvent), 1, "agents.skills",
            EventInterceptionCapabilities.Inspect | EventInterceptionCapabilities.Observe,
            true, true)
        {
            DeliveryClasses = [EventDelivery.Durable],
        });
        services.AddEvent(new EventDescriptor<MemoryChangedEvent>(
            new(MemoryChangedEvent), 1, "agents.memory",
            EventInterceptionCapabilities.Inspect | EventInterceptionCapabilities.Observe,
            true, true)
        {
            DeliveryClasses = [EventDelivery.Durable],
        });

        services.AddTool<AgentsToolHandler>(new ToolDescriptor(
            CreateTool,
            "Create an agent with a provider and model profile.",
            BuildCreateSchema(), ContainsSensitiveData: true));
        services.AddTool<AgentsToolHandler>(new ToolDescriptor(
            UpdateTool,
            "Update an agent profile.",
            BuildUpdateSchema(), ContainsSensitiveData: true));
        services.AddTool<AgentsToolHandler>(new ToolDescriptor(
            AccessSkillTool,
            "Read one permitted skill.",
            BuildIdSchema("skillId")));
        services.AddTool<AgentsToolHandler>(new ToolDescriptor(
            WriteMemoryTool,
            "Write one agent memory record.",
            BuildMemorySchema(), ContainsSensitiveData: true));
        services.AddTool<AgentsToolHandler>(new ToolDescriptor(
            SearchMemoryTool,
            "Search one agent memory store.",
            BuildSearchMemorySchema(), ContainsSensitiveData: true));

        services.UseChatProfileResolver<AgentChatProfileResolver>(
            new ExclusiveClaim("sharpclaw.agents.chat-profile"));

        foreach (EndpointRouteDescriptor route in AgentsEndpointContribution.EndpointRoutes)
            services.AddHttpEndpoint<AgentsEndpointContribution>(route);
        foreach (var command in AgentsCliHandler.Commands)
        {
            services.AddCliCommand<AgentsCliHandler>(new CliCommandDescriptor(
                command.Name,
                command.Name switch
                {
                    "agents-list" => ["agent-list"],
                    "skills-list" => ["skill-list"],
                    _ => [],
                },
                $"Execute the Agents {command.Operation} operation.",
                new JsonSchemaReference("sharpclaw.agents.cli.arguments", 1),
                new JsonSchemaReference("sharpclaw.agents.cli.result", 1),
                RequiresAdministrator: true));
        }
    }

    public ValueTask StartAsync(ServiceStartContext context, CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken ct) => ValueTask.CompletedTask;

    public static IReadOnlyList<ScopedStorageContractDescriptor> StorageContracts =>
    [
        Storage(AgentsCatalog.AgentsStorage, "Agent profiles and model selection.",
            [new("name", ScopedStorageIndexValueKind.String), new("providerKey", ScopedStorageIndexValueKind.String), new("updatedAt", ScopedStorageIndexValueKind.DateTime)]),
        Storage(AgentsCatalog.SkillsStorage, "Reusable agent skill instructions.",
            [new("name", ScopedStorageIndexValueKind.String), new("updatedAt", ScopedStorageIndexValueKind.DateTime)]),
        Storage(AgentsCatalog.MemoryStorage, "Agent-owned memory records and update order.",
            [new("agentId", ScopedStorageIndexValueKind.String), new("memoryKey", ScopedStorageIndexValueKind.String), new("updatedAt", ScopedStorageIndexValueKind.DateTime)]),
        Storage(AgentsCatalog.CostsStorage, "Agent model usage and cost totals.",
            [new("agentId", ScopedStorageIndexValueKind.String), new("updatedAt", ScopedStorageIndexValueKind.DateTime)]),
        Storage(AgentsCatalog.SynchronizationStorage, "Agent provider synchronization state.",
            [new("agentId", ScopedStorageIndexValueKind.String), new("updatedAt", ScopedStorageIndexValueKind.DateTime)]),
        Storage(AgentsCatalog.AgentJobsStorage, "Agent-owned job definitions and canonical Jobs references.",
            [
                new("agentId", ScopedStorageIndexValueKind.String),
                new("callerIdentity", ScopedStorageIndexValueKind.String),
                new("actionIdentity", ScopedStorageIndexValueKind.String),
                new("resource", ScopedStorageIndexValueKind.String),
                new("canonicalJobId", ScopedStorageIndexValueKind.String),
                new("channelId", ScopedStorageIndexValueKind.String),
                new("contextId", ScopedStorageIndexValueKind.String),
                new("permissionIdentity", ScopedStorageIndexValueKind.String),
                new("status", ScopedStorageIndexValueKind.String),
                new("handlerKey", ScopedStorageIndexValueKind.String),
                new("payloadCodec", ScopedStorageIndexValueKind.String),
                new("recoveryMode", ScopedStorageIndexValueKind.String),
                new("createdAt", ScopedStorageIndexValueKind.DateTime),
                new("updatedAt", ScopedStorageIndexValueKind.DateTime),
            ]),
        Storage(AgentsCatalog.AgentJobImportsStorage, "Agent job import manifests and completion markers.",
            [
                new("snapshotId", ScopedStorageIndexValueKind.String),
                new("aggregateHash", ScopedStorageIndexValueKind.String),
                new("mappingHash", ScopedStorageIndexValueKind.String),
                new("expectedRecordCount", ScopedStorageIndexValueKind.Number),
                new("importedRecordCount", ScopedStorageIndexValueKind.Number),
                new("completed", ScopedStorageIndexValueKind.String),
                new("capturedAt", ScopedStorageIndexValueKind.DateTime),
            ]),
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

    private static JsonElement BuildCreateSchema() => JsonDocument.Parse("""
        {"type":"object","properties":{"name":{"type":"string"},"modelId":{"type":"string"},"providerKey":{"type":"string"},"modelName":{"type":"string"},"systemPrompt":{"type":"string"}},"required":["name","modelId"],"additionalProperties":false}
        """).RootElement.Clone();

    private static JsonElement BuildUpdateSchema() => JsonDocument.Parse("""
        {"type":"object","properties":{"agentId":{"type":"string"},"name":{"type":"string"},"modelId":{"type":"string"},"providerKey":{"type":"string"},"systemPrompt":{"type":"string"}},"required":["agentId"],"additionalProperties":false}
        """).RootElement.Clone();

    private static JsonElement BuildIdSchema(string name) => JsonDocument.Parse($"{{\"type\":\"object\",\"properties\":{{\"{name}\":{{\"type\":\"string\"}}}},\"required\":[\"{name}\"],\"additionalProperties\":false}}", new JsonDocumentOptions()).RootElement.Clone();

    private static JsonElement BuildMemorySchema() => JsonDocument.Parse("""
        {"type":"object","properties":{"agentId":{"type":"string"},"key":{"type":"string"},"content":{"type":"string"},"tags":{"type":"array","items":{"type":"string"}}},"required":["agentId","key","content"],"additionalProperties":false}
        """).RootElement.Clone();

    private static JsonElement BuildSearchMemorySchema() => JsonDocument.Parse("""
        {"type":"object","properties":{"agentId":{"type":"string"},"query":{"type":"string"}},"required":["agentId"],"additionalProperties":false}
        """).RootElement.Clone();
}
