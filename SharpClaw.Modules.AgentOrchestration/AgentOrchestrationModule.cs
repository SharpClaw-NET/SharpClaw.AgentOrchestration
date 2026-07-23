using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpClaw.Contracts.Chat;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Modules.AgentOrchestration.Services;

namespace SharpClaw.Modules.AgentOrchestration;

/// <summary>
/// Runtime module for agent lifecycle, skill access, custom headers, and
/// cross-thread context tools.
/// </summary>
public sealed class AgentOrchestrationModule : ISharpClawRuntimeModule
{
    public const string ModuleIdValue = "sharpclaw_agent_orchestration";

    public string Id => ModuleIdValue;
    public string DisplayName => "Agent Orchestration";
    public string ToolPrefix => "ao";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<SkillStore>();
        services.TryAddScoped<AgentOrchestrationService>();
        services.TryAddScoped<IContextDataReader>(sp =>
            HostContextDataReaderAdapter.TryCreate() is { } hostReader
                ? hostReader
                : sp.GetService<ISharpClawDataContext>() is { } data
                    ? new ContextDataReader(data)
                    : throw new InvalidOperationException(
                        "Context tools require either SharpClaw host capabilities or ISharpClawDataContext."));
        services.TryAddScoped<ContextToolsService>();
    }

    public IReadOnlyList<ModuleContractExport> ExportedContracts => [];

    public IReadOnlyList<ModuleStorageContractDescriptor> GetStorageContracts() =>
    [
        new(
            ModuleIdValue,
            "skills",
            StorageOperations,
            "Reusable agent skill records.",
            [new("name", ModuleStorageIndexValueKind.String)],
            MaxDocumentBytes: 524_288,
            MaxBatchSize: 100),
    ];

    public IReadOnlyList<ModuleHeaderTag>? GetHeaderTags() =>
    [
        new ModuleHeaderTag(
            Name: "accessible-threads",
            Resolve: static (_, _) => Task.FromResult("(none)"))
        {
            ResolveWithContext = static async (sp, context, ct) =>
                await sp.GetRequiredService<ContextToolsService>()
                    .FormatAccessibleThreadsHeaderAsync(context.AgentId, context.ChannelId, ct)
        }
    ];

    public IReadOnlyList<ModuleResourceTypeDescriptor> GetResourceTypeDescriptors() =>
    [
        new("AoAgent", "ManageAgent", "ManageAgentAsync", static async (sp, ct) =>
        {
            var ids = sp.GetRequiredService<ICoreEntityIdProvider>();
            return await ids.GetAgentIdsAsync(ct);
        },
        LoadLookupItems: static async (sp, ct) =>
        {
            var ids = sp.GetRequiredService<ICoreEntityIdProvider>();
            return await ids.GetAgentLookupItemsAsync(ct);
        },
        DefaultResourceKey: "agent"),
        new("AoSkill", "AccessSkill", "AccessSkillAsync", static async (sp, ct) =>
        {
            var store = sp.GetRequiredService<SkillStore>();
            return [.. (await store.ListAsync(ct)).Select(skill => skill.Id)];
        },
        LoadLookupItems: static async (sp, ct) =>
        {
            var store = sp.GetRequiredService<SkillStore>();
            return [.. (await store.ListAsync(ct)).Select(skill =>
                new ValueTuple<Guid, string>(skill.Id, skill.Name))];
        },
        DefaultResourceKey: "skill"),
        new("AoAgentHeader", "EditAgentHeader", "EditAgentHeaderAsync", static async (sp, ct) =>
        {
            var ids = sp.GetRequiredService<ICoreEntityIdProvider>();
            return await ids.GetAgentIdsAsync(ct);
        },
        LoadLookupItems: static async (sp, ct) =>
        {
            var ids = sp.GetRequiredService<ICoreEntityIdProvider>();
            return await ids.GetAgentLookupItemsAsync(ct);
        }),
        new("AoChannelHeader", "EditChannelHeader", "EditChannelHeaderAsync", static async (sp, ct) =>
        {
            var ids = sp.GetRequiredService<ICoreEntityIdProvider>();
            return await ids.GetChannelIdsAsync(ct);
        },
        LoadLookupItems: static async (sp, ct) =>
        {
            var ids = sp.GetRequiredService<ICoreEntityIdProvider>();
            return await ids.GetChannelLookupItemsAsync(ct);
        }),
    ];

    public IReadOnlyList<ModuleCliCommand> GetCliCommands() =>
    [
        new(
            Name: "aoskill",
            Aliases: ["aos"],
            Scope: ModuleCliScope.ResourceType,
            Description: "Agent Orchestration skill management",
            UsageLines:
            [
                "resource aoskill add <name> --text <skillText> [--description <description>]",
                "resource aoskill get <id>                        Show an AO skill",
                "resource aoskill list                            List AO skills",
                "resource aoskill update <id> [--name <name>] [--description <description>] [--text <skillText>]",
                "resource aoskill delete <id>                     Delete an AO skill",
            ],
            Handler: HandleResourceAoSkillCommandAsync),
    ];

    public IReadOnlyList<ModuleGlobalFlagDescriptor> GetGlobalFlagDescriptors() =>
    [
        new("CanCreateSubAgents", "Create Sub-Agents",
            "Create sub-agents with permissions less than or equal to the creator's.",
            "CreateSubAgentAsync"),
        new("CanEditAgentHeader", "Edit Agent Header",
            "Edit the custom chat header of specific agents.",
            "CanEditAgentHeaderAsync"),
        new("CanEditChannelHeader", "Edit Channel Header",
            "Edit the custom chat header of specific channels.",
            "CanEditChannelHeaderAsync"),
        new(ContextToolsPermissionKeys.CanReadCrossThreadHistory,
            "Read Cross-Thread History",
            "Read conversation history from other threads and channels.",
            "ReadCrossThreadHistoryAsync"),
    ];

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions()
    {
        var globalNoResource = new ModuleToolPermission(
            IsPerResource: false, Check: null,
            DelegateTo: "CreateSubAgentAsync");
        var perResourceManageAgent = new ModuleToolPermission(
            IsPerResource: true, Check: null,
            DelegateTo: "ManageAgentAsync");
        var perResourceAccessSkill = new ModuleToolPermission(
            IsPerResource: true, Check: null,
            DelegateTo: "AccessSkillAsync");
        var perResourceEditAgentHeader = new ModuleToolPermission(
            IsPerResource: true, Check: null,
            DelegateTo: "EditAgentHeaderAsync");
        var perResourceEditChannelHeader = new ModuleToolPermission(
            IsPerResource: true, Check: null,
            DelegateTo: "EditChannelHeaderAsync");

        return
        [
            new("create_sub_agent",
                "Create a sub-agent (name, modelId, optional systemPrompt).",
                BuildCreateSubAgentSchema(), globalNoResource),
            new("ao_manage_agent",
                "Update agent name, systemPrompt, or modelId.",
                BuildManageAgentSchema(), perResourceManageAgent,
                Aliases: ["manage_agent"]),
            new("ao_access_skill",
                "Retrieve a skill's instruction text.",
                BuildResourceOnlySchema(), perResourceAccessSkill,
                Aliases: ["access_skill"]),
            new("ao_edit_agent_header",
                "Set or clear the custom chat header for an agent.",
                BuildHeaderSchema(), perResourceEditAgentHeader,
                Aliases: ["edit_agent_header"]),
            new("ao_edit_channel_header",
                "Set or clear the custom chat header for a channel.",
                BuildHeaderSchema(), perResourceEditChannelHeader,
                Aliases: ["edit_channel_header"]),
        ];
    }

    public async Task<string> ExecuteToolAsync(
        string toolName, JsonElement parameters, AgentJobContext job,
        IServiceProvider sp, CancellationToken ct)
    {
        var service = sp.GetRequiredService<AgentOrchestrationService>();

        return toolName switch
        {
            "create_sub_agent" => await service.CreateSubAgentAsync(parameters, ct),
            "ao_manage_agent" or "manage_agent" => await service.ManageAgentAsync(
                job.ResourceId ?? throw new InvalidOperationException(
                    "manage_agent requires a ResourceId (target agent)."),
                parameters, ct),
            "ao_access_skill" or "access_skill" => await service.AccessSkillAsync(
                job.ResourceId ?? throw new InvalidOperationException(
                    "access_skill requires a ResourceId (target skill)."),
                ct),
            "ao_edit_agent_header" or "edit_agent_header" =>
                await service.EditAgentHeaderAsync(
                    job.ResourceId ?? throw new InvalidOperationException(
                        "edit_agent_header requires a ResourceId (target agent)."),
                    parameters, ct),
            "ao_edit_channel_header" or "edit_channel_header" =>
                await service.EditChannelHeaderAsync(
                    job.ResourceId ?? throw new InvalidOperationException(
                        "edit_channel_header requires a ResourceId (target channel)."),
                    parameters, ct),
            _ => throw new InvalidOperationException(
                $"Unknown Agent Orchestration tool: '{toolName}'."),
        };
    }

    public IReadOnlyList<ModuleInlineToolDefinition> GetInlineToolDefinitions()
    {
        var crossThreadPermission = new ModuleToolPermission(
            IsPerResource: false, Check: null,
            DelegateTo: "ReadCrossThreadHistoryAsync");

        return
        [
            new("wait",
                "Pause for 1-300 seconds. No tokens consumed while waiting.",
                BuildWaitSchema()),
            new("list_accessible_threads",
                "List readable threads from other channels (IDs, names, parent channel).",
                BuildContextToolsGlobalActionSchema(), crossThreadPermission),
            new("read_thread_history",
                "Read cross-channel thread history. Optional maxMessages (1-200, default 50).",
                BuildReadThreadHistorySchema(), crossThreadPermission),
        ];
    }

    public async Task<string> ExecuteInlineToolAsync(
        string toolName, JsonElement parameters, InlineToolContext context,
        IServiceProvider sp, CancellationToken ct)
    {
        var service = sp.GetRequiredService<ContextToolsService>();

        return toolName switch
        {
            "wait" => await ContextToolsService.WaitAsync(parameters, ct),
            "list_accessible_threads" => await service.ListAccessibleThreadsAsync(
                context.AgentId, context.ChannelId, ct),
            "read_thread_history" => await service.ReadThreadHistoryAsync(
                parameters, context.AgentId, context.ChannelId, ct),
            _ => throw new InvalidOperationException(
                $"Unknown Agent Orchestration inline tool: '{toolName}'."),
        };
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken ct) =>
        Task.CompletedTask;

    public Task ShutdownAsync() => Task.CompletedTask;

    public void MapEndpoints(object app)
    {
    }

    private static async Task HandleResourceAoSkillCommandAsync(
        string[] args, IServiceProvider sp, CancellationToken ct)
    {
        var ids = sp.GetRequiredService<ICliIdResolver>();
        var store = sp.GetRequiredService<SkillStore>();

        if (args.Length < 3)
        {
            PrintAoSkillUsage();
            return;
        }

        var sub = args[2].ToLowerInvariant();
        switch (sub)
        {
            case "add" when args.Length >= 4:
            {
                var flags = ParseFlags(args, 4);
                if (!flags.TryGetValue("text", out var skillText)
                    || string.IsNullOrWhiteSpace(skillText))
                {
                    Console.Error.WriteLine(
                        "resource aoskill add requires --text <skillText>.");
                    break;
                }

                flags.TryGetValue("description", out var description);
                var skill = new Models.SkillDB
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Name = args[3],
                    Description = description,
                    SkillText = skillText,
                };

                await store.CreateAsync(skill, ct);
                ids.PrintJson(ToAoSkillDto(skill));
                break;
            }
            case "add":
                Console.Error.WriteLine(
                    "resource aoskill add <name> --text <skillText> [--description <description>]");
                break;
            case "get" when args.Length >= 4:
            {
                var skill = await store.GetByIdAsync(ids.Resolve(args[3]), ct);
                if (skill is not null)
                    ids.PrintJson(ToAoSkillDto(skill));
                else
                    Console.Error.WriteLine("Not found.");
                break;
            }
            case "get":
                Console.Error.WriteLine("resource aoskill get <id>");
                break;
            case "list":
            {
                var skills = (await store.ListAsync(ct))
                    .OrderBy(skill => skill.Name)
                    .ToList();
                ids.PrintJson(skills.Select(ToAoSkillDto).ToList());
                break;
            }
            case "update" when args.Length >= 4:
            {
                var flags = ParseFlags(args, 4);
                var skill = await store.UpdateAsync(ids.Resolve(args[3]), storedSkill =>
                {
                    if (flags.TryGetValue("name", out var name)
                        && !string.IsNullOrWhiteSpace(name))
                    {
                        storedSkill.Name = name;
                    }

                    if (flags.TryGetValue("description", out var description))
                        storedSkill.Description = description;
                    if (flags.TryGetValue("text", out var text)
                        && !string.IsNullOrWhiteSpace(text))
                    {
                        storedSkill.SkillText = text;
                    }
                }, ct);

                if (skill is null)
                {
                    Console.Error.WriteLine("Not found.");
                    break;
                }

                ids.PrintJson(ToAoSkillDto(skill));
                break;
            }
            case "update":
                Console.Error.WriteLine(
                    "resource aoskill update <id> [--name <name>] [--description <description>] [--text <skillText>]");
                break;
            case "delete" when args.Length >= 4:
            {
                var deleted = await store.DeleteAsync(ids.Resolve(args[3]), ct);
                Console.WriteLine(deleted ? "Done." : "Not found.");
                break;
            }
            case "delete":
                Console.Error.WriteLine("resource aoskill delete <id>");
                break;
            default:
                Console.Error.WriteLine($"Unknown command: resource aoskill {sub}");
                PrintAoSkillUsage();
                break;
        }
    }

    private static Dictionary<string, string> ParseFlags(string[] args, int start)
    {
        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = start; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                continue;

            var key = args[i][2..];
            if (string.IsNullOrWhiteSpace(key))
                continue;

            flags[key] = i + 1 < args.Length
                && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : string.Empty;
        }

        return flags;
    }

    private static object ToAoSkillDto(Models.SkillDB skill) => new
    {
        skill.Id,
        skill.Name,
        skill.Description,
        skill.SkillText,
        skill.CreatedAt,
        skill.UpdatedAt,
    };

    private static void PrintAoSkillUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine(
            "  resource aoskill add <name> --text <skillText> [--description <description>]");
        Console.Error.WriteLine("  resource aoskill get <id>                        Show an AO skill");
        Console.Error.WriteLine("  resource aoskill list                            List AO skills");
        Console.Error.WriteLine(
            "  resource aoskill update <id> [--name <name>] [--description <description>] [--text <skillText>]");
        Console.Error.WriteLine("  resource aoskill delete <id>                     Delete an AO skill");
    }

    private static JsonElement BuildCreateSubAgentSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "name": { "type": "string", "description": "Agent name." },
                    "modelId": { "type": "string", "description": "Model GUID." },
                    "systemPrompt": { "type": "string", "description": "System prompt." }
                },
                "required": ["name", "modelId"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildManageAgentSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "resource_id": { "type": "string", "description": "Agent GUID." },
                    "name": { "type": "string", "description": "New name." },
                    "systemPrompt": { "type": "string", "description": "New system prompt." },
                    "modelId": { "type": "string", "description": "New model GUID." }
                },
                "required": ["resource_id"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildResourceOnlySchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "resource_id": { "type": "string", "description": "Resource GUID." }
                },
                "required": ["resource_id"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildHeaderSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "resource_id": { "type": "string", "description": "Target agent or channel GUID." },
                    "header": { "type": "string", "description": "Header template text. Empty or null clears the custom header." }
                },
                "required": ["resource_id"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildWaitSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "seconds": { "type": "integer", "description": "Seconds (1-300)." }
                },
                "required": ["seconds"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildContextToolsGlobalActionSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {},
                "additionalProperties": false
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildReadThreadHistorySchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "threadId": { "type": "string", "description": "Thread GUID (from list_accessible_threads)." },
                    "maxMessages": { "type": "integer", "description": "Max messages (1-200, default 50)." }
                },
                "required": ["threadId"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static IReadOnlyList<ModuleStorageOperationDescriptor> StorageOperations =>
    [
        new(ModuleStorageOperations.Get),
        new(ModuleStorageOperations.Upsert),
        new(ModuleStorageOperations.BatchUpsert),
        new(ModuleStorageOperations.Delete),
        new(ModuleStorageOperations.BatchDelete),
        new(ModuleStorageOperations.List),
        new(ModuleStorageOperations.Query),
    ];
}
