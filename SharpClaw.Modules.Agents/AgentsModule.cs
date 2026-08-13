using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
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

    private static readonly ActionRepeatPolicy RepeatPolicy =
        new(ActionRepeatKind.Receipted, 3, TimeSpan.FromMilliseconds(100), "agents");

    public ModuleIdentity Identity { get; } = new(ModuleIdValue, "SharpClaw Agents", "agents");

    public void Configure(ISharpClawModuleBuilder module)
    {
        module.Services.AddScoped<AgentsCatalog>();
        module.Services.AddScoped<AgentsToolHandler>();
        module.Services.AddScoped<AgentsCliHandler>();
        module.Services.AddScoped<AgentChatProfileResolver>();
        module.Services.AddScoped<AgentsDbContextAccessor>();

        module.Contracts.Export<AgentsModuleContract>("sharpclaw.agents");
        module.Contracts.Require<ContextModuleContract>("sharpclaw.context");
        module.Contracts.Require<PermissionModuleContract>("sharpclaw.permission");

        foreach (var storage in StorageContracts)
            module.Storage.Add(storage);

        module.Actions.Add(new ActionDescriptor<AgentsCreateAction, AgentRecord>(
            new("agents.create"), 1, "agents",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromSeconds(30)));
        module.Actions.Add(new ActionDescriptor<AgentsUpdateAction, AgentRecord?>(
            new("agents.update"), 1, "agents",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromSeconds(30)));
        module.Actions.Add(new ActionDescriptor<AgentsWriteMemoryAction, MemoryRecord>(
            new("agents.memory.write"), 1, "agents.memory",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromSeconds(30)));

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
        application.Cli.Add<AgentsCliHandler>(new ModuleCliCommandDescriptor(
            "agents-list",
            ["agent-list"],
            "List configured agents.",
            new JsonSchemaReference("sharpclaw.agents.cli.arguments", 1),
            new JsonSchemaReference("sharpclaw.agents.cli.result", 1),
            RequiresAdministrator: true));
        application.Cli.Add<AgentsCliHandler>(new ModuleCliCommandDescriptor(
            "skills-list",
            ["skill-list"],
            "List configured skills.",
            new JsonSchemaReference("sharpclaw.agents.skills.cli.arguments", 1),
            new JsonSchemaReference("sharpclaw.agents.skills.cli.result", 1),
            RequiresAdministrator: true));
    }

    public ValueTask StartAsync(ModuleStartContext context, CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken ct) => ValueTask.CompletedTask;

    public static IReadOnlyList<ModuleStorageContractDescriptor> StorageContracts =>
    [
        Storage(AgentsCatalog.AgentsStorage, "Agent profiles and model selection.",
            [new("name", ModuleStorageIndexValueKind.String), new("providerKey", ModuleStorageIndexValueKind.String)]),
        Storage(AgentsCatalog.SkillsStorage, "Reusable agent skill instructions.",
            [new("name", ModuleStorageIndexValueKind.String), new("updatedAt", ModuleStorageIndexValueKind.DateTime)]),
        Storage(AgentsCatalog.MemoryStorage, "Agent-owned memory records and update order.",
            [new("agentId", ModuleStorageIndexValueKind.String), new("memoryKey", ModuleStorageIndexValueKind.String), new("updatedAt", ModuleStorageIndexValueKind.DateTime)]),
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

public sealed class AgentsDbContextAccessor(IModuleDbContextFactory factory)
{
    public AgentsDbContext Create() => factory.CreateDbContext<AgentsDbContext>();
}
