using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.ModuleSDK;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.Agents;

public sealed class AgentsModule : ISharpClawModule, ISharpClawApplicationModule
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

    public void Configure(ISharpClawModuleBuilder module)
    {
        module.Services.AddScoped<AgentsCatalog>();
        module.Services.AddScoped<HostModuleActionEntry>();
        module.Services.AddScoped<AgentsToolHandler>();
        module.Services.AddScoped<AgentsApiActionExecutor>();
        module.Services.AddScoped<AgentsApiActionTerminal>();
        module.Services.AddScoped<AgentsEndpointContribution>();
        module.Services.AddScoped<IAgentsActionGateway, AgentsActionGateway>();
        module.Services.AddScoped<IModuleActionPipeline, ModuleActionPipeline>();
        module.Services.AddScoped<IAgentsActionExecutor, AgentsActionExecutor>();
        module.Services.AddScoped<IAgentsJobActionExecutor, AgentsJobActionExecutor>();
        module.Services.AddScoped<AgentsCreateAuthorizationHook>();
        module.Services.AddSingleton<AgentsCliHandler>();
        module.Services.AddScoped<AgentChatProfileResolver>();

        module.Contracts.Export<AgentsModuleContract>("sharpclaw.agents");
        module.Contracts.Require<ContextModuleContract>("sharpclaw.context");
        module.UseAgentOrchestrationPermission(AgentOrchestrationPermissionUse.Agents);

        foreach (var storage in StorageContracts)
            module.Storage.Add(storage);

        module.Actions.Add(ApiDescriptor);
        module.AddActionEntry<AgentsApiAction, JsonElement, AgentsApiActionTerminal>(
            ApiDescriptor,
            ApiTerminalId);

        module.Actions.Add(new ActionDescriptor<AgentsCreateAction, AgentRecord>(
            new("agents.create"), 1, "agents",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            SafePoints = SafePoints,
        });
        module.Actions.Add(new ActionDescriptor<AgentsUpdateAction, AgentRecord?>(
            new("agents.update"), 1, "agents",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            SafePoints = SafePoints,
        });
        module.Actions.Add(new ActionDescriptor<AgentsWriteMemoryAction, MemoryRecord>(
            new("agents.memory.write"), 1, "agents.memory",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            SafePoints = SafePoints,
        });
        module.Actions.Add(new ActionDescriptor<AgentsSaveSkillAction, SkillRecord>(
            new("agents.skill.save"), 1, "agents.skills",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            SafePoints = SafePoints,
        });
        module.Actions.Add(new ActionDescriptor<AgentsAccessSkillAction, string>(
            new("agents.skill.access"), 1, "agents.skills",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Observe,
            false, false, RepeatPolicy, null, TimeSpan.FromSeconds(10))
        {
            SafePoints = SafePoints,
        });
        module.Actions.Add(new ActionDescriptor<AgentsSearchMemoryAction, IReadOnlyList<MemoryRecord>>(
            new("agents.memory.search"), 1, "agents.memory",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Observe,
            true, false, RepeatPolicy, null, TimeSpan.FromSeconds(10))
        {
            SafePoints = SafePoints,
        });
        module.Actions.Add(new ActionDescriptor<AgentsRecordJobAction, AgentJob>(
            new(RecordAgentJobAction), 1, "agents.jobs",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            SafePoints = SafePoints,
        });
        module.Actions.Add(new ActionDescriptor<AgentsAttachCanonicalJobAction, AgentJob>(
            new(AttachCanonicalJobAction), 1, "agents.jobs",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            SafePoints = SafePoints,
        });
        module.Actions.Add(new ActionDescriptor<AgentsCompleteJobAction, AgentJob>(
            new(CompleteAgentJobAction), 1, "agents.jobs",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            SafePoints = SafePoints,
        });
        module.Actions.Add(new ActionDescriptor<AgentsImportJobsAction, IReadOnlyList<AgentJob>>(
            new(ImportAgentJobsAction), 1, "agents.jobs",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromMinutes(2))
        {
            SafePoints = SafePoints,
        });
        module.Hooks.For(new SharpClawActionKey("agents.create"))
            .Use<AgentsCreateAuthorizationHook>(new HookOrdering("agents.create.authorization"));
        module.Events.Add(new EventDescriptor<AgentChangedEvent>(
            new(AgentChangedEvent), 1, "agents",
            EventInterceptionCapabilities.Inspect | EventInterceptionCapabilities.Observe,
            true, true)
        {
            DeliveryClasses = [EventDelivery.Durable],
        });
        module.Events.Add(new EventDescriptor<SkillChangedEvent>(
            new(SkillChangedEvent), 1, "agents.skills",
            EventInterceptionCapabilities.Inspect | EventInterceptionCapabilities.Observe,
            true, true)
        {
            DeliveryClasses = [EventDelivery.Durable],
        });
        module.Events.Add(new EventDescriptor<MemoryChangedEvent>(
            new(MemoryChangedEvent), 1, "agents.memory",
            EventInterceptionCapabilities.Inspect | EventInterceptionCapabilities.Observe,
            true, true)
        {
            DeliveryClasses = [EventDelivery.Durable],
        });

        module.Tools.Add<AgentsToolHandler>(new ToolDescriptor(
            CreateTool,
            "Create an agent with a provider and model profile.",
            BuildCreateSchema(), ContainsSensitiveData: true));
        module.Tools.Add<AgentsToolHandler>(new ToolDescriptor(
            UpdateTool,
            "Update an agent profile.",
            BuildUpdateSchema(), ContainsSensitiveData: true));
        module.Tools.Add<AgentsToolHandler>(new ToolDescriptor(
            AccessSkillTool,
            "Read one permitted skill.",
            BuildIdSchema("skillId")));
        module.Tools.Add<AgentsToolHandler>(new ToolDescriptor(
            WriteMemoryTool,
            "Write one agent memory record.",
            BuildMemorySchema(), ContainsSensitiveData: true));
        module.Tools.Add<AgentsToolHandler>(new ToolDescriptor(
            SearchMemoryTool,
            "Search one agent memory store.",
            BuildSearchMemorySchema(), ContainsSensitiveData: true));

        module.Chat.UseChatProfileResolver<AgentChatProfileResolver>(
            new ExclusiveRegistration("sharpclaw.agents.chat-profile"));
    }

    public void ConfigureApplication(ISharpClawApplicationBuilder application)
    {
        foreach (ModuleEndpointRouteDescriptor route in AgentsEndpointContribution.EndpointRoutes)
            application.Endpoints.AddHttp<AgentsEndpointContribution>(route);
        foreach (var command in AgentsCliHandler.Commands)
        {
            application.Cli.Add<AgentsCliHandler>(new ModuleCliCommandDescriptor(
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

    public ValueTask StartAsync(ModuleStartContext context, CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken ct) => ValueTask.CompletedTask;

    public static IReadOnlyList<ModuleStorageContractDescriptor> StorageContracts =>
    [
        Storage(AgentsCatalog.AgentsStorage, "Agent profiles and model selection.",
            [new("name", ModuleStorageIndexValueKind.String), new("providerKey", ModuleStorageIndexValueKind.String), new("updatedAt", ModuleStorageIndexValueKind.DateTime)]),
        Storage(AgentsCatalog.SkillsStorage, "Reusable agent skill instructions.",
            [new("name", ModuleStorageIndexValueKind.String), new("updatedAt", ModuleStorageIndexValueKind.DateTime)]),
        Storage(AgentsCatalog.MemoryStorage, "Agent-owned memory records and update order.",
            [new("agentId", ModuleStorageIndexValueKind.String), new("memoryKey", ModuleStorageIndexValueKind.String), new("updatedAt", ModuleStorageIndexValueKind.DateTime)]),
        Storage(AgentsCatalog.CostsStorage, "Agent model usage and cost totals.",
            [new("agentId", ModuleStorageIndexValueKind.String), new("updatedAt", ModuleStorageIndexValueKind.DateTime)]),
        Storage(AgentsCatalog.SynchronizationStorage, "Agent provider synchronization state.",
            [new("agentId", ModuleStorageIndexValueKind.String), new("updatedAt", ModuleStorageIndexValueKind.DateTime)]),
        Storage(AgentsCatalog.AgentJobsStorage, "Agent-owned job definitions and canonical Jobs references.",
            [
                new("agentId", ModuleStorageIndexValueKind.String),
                new("callerIdentity", ModuleStorageIndexValueKind.String),
                new("actionIdentity", ModuleStorageIndexValueKind.String),
                new("resource", ModuleStorageIndexValueKind.String),
                new("canonicalJobId", ModuleStorageIndexValueKind.String),
                new("channelId", ModuleStorageIndexValueKind.String),
                new("contextId", ModuleStorageIndexValueKind.String),
                new("permissionIdentity", ModuleStorageIndexValueKind.String),
                new("status", ModuleStorageIndexValueKind.String),
                new("handlerKey", ModuleStorageIndexValueKind.String),
                new("payloadCodec", ModuleStorageIndexValueKind.String),
                new("recoveryMode", ModuleStorageIndexValueKind.String),
                new("createdAt", ModuleStorageIndexValueKind.DateTime),
                new("updatedAt", ModuleStorageIndexValueKind.DateTime),
            ]),
        Storage(AgentsCatalog.AgentJobImportsStorage, "Agent job import manifests and completion markers.",
            [
                new("snapshotId", ModuleStorageIndexValueKind.String),
                new("aggregateHash", ModuleStorageIndexValueKind.String),
                new("mappingHash", ModuleStorageIndexValueKind.String),
                new("expectedRecordCount", ModuleStorageIndexValueKind.Number),
                new("importedRecordCount", ModuleStorageIndexValueKind.Number),
                new("completed", ModuleStorageIndexValueKind.String),
                new("capturedAt", ModuleStorageIndexValueKind.DateTime),
            ]),
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
