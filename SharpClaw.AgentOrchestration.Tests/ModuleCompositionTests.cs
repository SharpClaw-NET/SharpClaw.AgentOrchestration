using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.Agents;
using SharpClaw.Modules.AgentOrchestration.Contracts;
using SharpClaw.Modules.Context;
using SharpClaw.Modules.TwoTierPermission;

namespace SharpClaw.AgentOrchestration.Tests;

[TestFixture]
public sealed class ModuleCompositionTests
{
    [Test]
    public void ModulesDeclareCurrentIdentitiesAndOwnedStorage()
    {
        var context = new ContextModule();
        var permission = new TwoTierPermissionModule();
        var agents = new AgentsModule();
        var contextBuilder = new RecordingBuilder();
        var permissionBuilder = new RecordingBuilder();
        var agentsBuilder = new RecordingBuilder();

        context.Configure(contextBuilder);
        permission.Configure(permissionBuilder);
        agents.Configure(agentsBuilder);

        Assert.Multiple(() =>
        {
            Assert.That(context.Identity, Is.EqualTo(new ModuleIdentity(
                "sharpclaw_context", "SharpClaw Context", "ctx")));
            Assert.That(permission.Identity, Is.EqualTo(new ModuleIdentity(
                "sharpclaw_two_tier_permission", "SharpClaw Two Tier Permission", "perm")));
            Assert.That(agents.Identity, Is.EqualTo(new ModuleIdentity(
                "sharpclaw_agents", "SharpClaw Agents", "agents")));
            Assert.That(contextBuilder.Storage.Items.Select(item => item.StorageName), Is.EquivalentTo(
                new[] { "channels", "contexts", "threads", "messages" }));
            Assert.That(permissionBuilder.Storage.Items.Select(item => item.StorageName), Is.EquivalentTo(
                new[] { "policies", "grants", "approvals" }));
            Assert.That(agentsBuilder.Storage.Items.Select(item => item.StorageName), Is.EquivalentTo(
                new[] { "agents", "skills", "memory" }));
            Assert.That(permissionBuilder.Contracts.Exports.Select(item => item.ContractName),
                Does.Contain("sharpclaw.permission"));
            Assert.That(agentsBuilder.Contracts.Requires.Select(item => item.ContractName),
                Is.EquivalentTo(new[] { "sharpclaw.context", "sharpclaw.permission" }));
            Assert.That(contextBuilder.Services.Any(item => item.ServiceType == typeof(IContextActionExecutor)), Is.True);
            Assert.That(permissionBuilder.Services.Any(item => item.ServiceType == typeof(IPermissionActionExecutor)), Is.True);
            Assert.That(agentsBuilder.Services.Any(item => item.ServiceType == typeof(IAgentsActionExecutor)), Is.True);
            Assert.That(contextBuilder.Actions.Items.OfType<ActionDescriptor<ContextCreateThreadAction, ContextThreadRecord>>().Single().SafePoints,
                Is.Not.Empty);
            Assert.That(permissionBuilder.Actions.Items.OfType<ActionDescriptor<PermissionGrantAction, bool>>().Single().SafePoints,
                Is.Not.Empty);
            Assert.That(agentsBuilder.Actions.Items.OfType<ActionDescriptor<AgentsSaveSkillAction, SkillRecord>>().Single().SafePoints,
                Is.Not.Empty);
            Assert.That(contextBuilder.Events.Items.OfType<EventDescriptor<ContextThreadChangedEvent>>(), Has.Exactly(1).Items);
            Assert.That(permissionBuilder.Events.Items.OfType<EventDescriptor<PermissionChangedEvent>>(), Has.Exactly(1).Items);
            Assert.That(agentsBuilder.Events.Items.OfType<EventDescriptor<MemoryChangedEvent>>(), Has.Exactly(1).Items);
        });
    }

    [Test]
    public void ManifestsUseCurrentThreeOwnerComposition()
    {
        var expected = new[]
        {
            ("Context.module.json", "sharpclaw_context", "SharpClaw.Modules.Context.ContextModule", "SharpClaw.Modules.Context.dll"),
            ("TwoTierPermission.module.json", "sharpclaw_two_tier_permission", "SharpClaw.Modules.TwoTierPermission.TwoTierPermissionModule", "SharpClaw.Modules.TwoTierPermission.dll"),
            ("Agents.module.json", "sharpclaw_agents", "SharpClaw.Modules.Agents.AgentsModule", "SharpClaw.Modules.Agents.dll"),
        };

        foreach (var item in expected)
        {
            var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "manifests", item.Item1);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            Assert.Multiple(() =>
            {
                Assert.That(root.GetProperty("id").GetString(), Is.EqualTo(item.Item2));
                Assert.That(root.GetProperty("version").GetString(), Is.EqualTo("0.5.0-beta.1"));
                Assert.That(root.GetProperty("entryAssembly").GetString(), Is.EqualTo(item.Item4));
                Assert.That(root.GetProperty("moduleType").GetString(), Is.EqualTo(item.Item3));
                Assert.That(root.GetProperty("defaultEnabled").GetBoolean(), Is.True);
                Assert.That(root.GetProperty("hostMode").GetString(), Is.EqualTo("sidecar"));
            });
        }
    }

    [Test]
    public async Task ContextHistoryUsesOwnedStorageAndPermissionGate()
    {
        var gateway = new InMemoryStorageGateway();
        var policyStore = new PermissionPolicyStore(gateway);
        var permission = new TwoTierPermissionPolicy(policyStore);
        var agentId = Guid.NewGuid();
        var caller = new RequestPrincipal(agentId.ToString("D"), Roles: new HashSet<string>());
        await policyStore.SaveAsync(new PermissionPolicyRecord(
            caller.SubjectId,
            [], ["read_cross_thread_history"], [],
            PermissionClearance.Independent,
            RequireSourceOptIn: true,
            [], null, DateTimeOffset.UtcNow));

        var store = new ContextStore(gateway);
        var current = new ContextChannelRecord(
            Guid.NewGuid(), "Current", agentId, null, [], [], false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var source = new ContextChannelRecord(
            Guid.NewGuid(), "Source", agentId, null, [], [], false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await store.SaveChannelAsync(current);
        await store.SaveChannelAsync(source);
        var thread = await store.CreateThreadAsync(source.Id, "Source thread");
        await store.AppendMessageAsync(new ContextMessageRecord(
            Guid.NewGuid(), thread.Id, source.Id, "user", "retained history", "tester",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var handler = new ContextToolHandler(store, permission);
        using var arguments = JsonDocument.Parse($$"""{"channelId":"{{current.Id:D}}"}""");
        var result = await handler.InvokeAsync(new ToolInvocation(
            Guid.NewGuid(), null, "call", ContextModule.ListThreadsTool,
            arguments.RootElement, caller, ExtensionFeatureSet.Empty), default);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.Content, Does.Contain(thread.Id.ToString("D")));

        using var readArguments = JsonDocument.Parse($$"""{"channelId":"{{current.Id:D}}","threadId":"{{thread.Id:D}}","maxMessages":10}""");
        var read = await handler.InvokeAsync(new ToolInvocation(
            Guid.NewGuid(), null, "call", ContextModule.ReadHistoryTool,
            readArguments.RootElement, caller, ExtensionFeatureSet.Empty), default);
        Assert.That(read.Content, Does.Contain("retained history"));
    }

    [Test]
    public async Task TwoTierPolicyEnforcesClearanceOptInAndHardDenial()
    {
        var gateway = new InMemoryStorageGateway();
        var store = new PermissionPolicyStore(gateway);
        var policy = new TwoTierPermissionPolicy(store);
        var agentId = Guid.NewGuid();
        var caller = new RequestPrincipal(agentId.ToString("D"));
        await store.SaveAsync(new PermissionPolicyRecord(
            caller.SubjectId, [], ["read_cross_thread_history"], [],
            PermissionClearance.ApprovedBySameLevelUser, true, [], null, DateTimeOffset.UtcNow));
        var request = new ContextAccessRequest(
            caller, Guid.NewGuid(), agentId, [], null, [], SourceChannelOptedIn: false);

        var denied = await policy.EvaluateDetailedAsync(request);
        Assert.That(denied, Has.Property("Allowed").EqualTo(false));
        Assert.That(denied.Code, Is.EqualTo("source_opt_in_required"));

        var allowed = await policy.EvaluateDetailedAsync(request with { SourceChannelOptedIn = true });
        Assert.That(allowed.Allowed, Is.True);

        await store.SaveAsync(new PermissionPolicyRecord(
            caller.SubjectId, [], ["read_cross_thread_history"], ["read_cross_thread_history"],
            PermissionClearance.Independent, false, [], null, DateTimeOffset.UtcNow));
        var hardDenied = await policy.EvaluateDetailedAsync(request with { SourceChannelOptedIn = true });
        Assert.That(hardDenied.Code, Is.EqualTo("hard_denial"));
    }

    [Test]
    public async Task ContextDefaultAgentAssignmentAllowsHistoryDiscovery()
    {
        var gateway = new InMemoryStorageGateway();
        var policyStore = new PermissionPolicyStore(gateway);
        var permission = new TwoTierPermissionPolicy(policyStore);
        var agentId = Guid.NewGuid();
        var caller = new RequestPrincipal(agentId.ToString("D"));
        await policyStore.SaveAsync(new PermissionPolicyRecord(
            caller.SubjectId, [], ["read_cross_thread_history"], [],
            PermissionClearance.Independent, true, [], null, DateTimeOffset.UtcNow));

        var store = new ContextStore(gateway);
        var current = new ContextChannelRecord(
            Guid.NewGuid(), "Current", agentId, null, [], [], false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var context = new ContextRecord(
            Guid.NewGuid(), "Assigned context", agentId, [],
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var source = new ContextChannelRecord(
            Guid.NewGuid(), "Source", null, null, [], [], false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        {
            ContextId = context.Id,
        };
        await store.SaveChannelAsync(current);
        await store.SaveContextAsync(context);
        await store.SaveChannelAsync(source);
        var thread = await store.CreateThreadAsync(source.Id, "Assigned thread", context.Id);

        var visible = await store.ListAccessibleThreadsAsync(
            caller, current.Id, permission);

        Assert.That(visible.Select(item => item.ThreadId), Does.Contain(thread.Id));
    }

    [Test]
    public async Task ActionExecutorsUseOwnedModuleOperations()
    {
        var gateway = new InMemoryStorageGateway();
        var permissionStore = new PermissionPolicyStore(gateway);
        var permission = new TwoTierPermissionPolicy(permissionStore);
        var admin = new RequestPrincipal(Guid.NewGuid().ToString("D"), Roles: new HashSet<string>(["admin"]));
        var permissionExecutor = new PermissionActionExecutor(permission);
        Assert.That(await permissionExecutor.GrantAsync(admin,
            new PermissionGrantAction("subject", "read_memory", "global", PermissionClearance.Independent)), Is.True);
        Assert.That((await permissionExecutor.EvaluateAsync(
            new RequestPrincipal("subject"),
            new PermissionEvaluateAction("subject", "read_memory", "global", false))).Allowed, Is.True);

        var agents = new AgentsActionExecutor(new AgentsCatalog(gateway, permission));
        var agent = await agents.CreateAsync(admin,
            new AgentsCreateAction("Executor Agent", Guid.NewGuid(), "provider", "model", null));
        var skill = await agents.SaveSkillAsync(admin,
            new AgentsSaveSkillAction(new SkillRecord(
                Guid.NewGuid(), "Executor Skill", null, "use the skill", [agent.Id],
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        Assert.That(await agents.AccessSkillAsync(admin, new AgentsAccessSkillAction(skill.Id)),
            Does.Contain("use the skill"));

        var contextStore = new ContextStore(gateway);
        var channel = new ContextChannelRecord(
            Guid.NewGuid(), "Executor Channel", agent.Id, null, [], [], false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await contextStore.SaveChannelAsync(channel);
        var contextExecutor = new ContextActionExecutor(contextStore, permission);
        var thread = await contextExecutor.CreateThreadAsync(admin,
            new ContextCreateThreadAction(channel.Id, "Executor Thread"));
        Assert.That(await contextExecutor.CommitExchangeAsync(admin,
            new ContextCommitExchangeAction(thread.Id, "question", "answer")), Is.True);
        Assert.That((await contextStore.ReadAllMessagesAsync(thread.Id)).Select(item => item.Content),
            Is.EquivalentTo(new[] { "question", "answer" }));
    }

    [Test]
    public async Task AgentsOwnProfileSkillAndMemoryPersistence()
    {
        var gateway = new InMemoryStorageGateway();
        var permissionStore = new PermissionPolicyStore(gateway);
        var permission = new TwoTierPermissionPolicy(permissionStore);
        var catalog = new AgentsCatalog(gateway, permission);
        var admin = new RequestPrincipal("admin", Roles: new HashSet<string>(["admin"]));
        var agent = await catalog.CreateAgentAsync(admin, new(
            "Test Agent", Guid.NewGuid(), "provider", "model", "prompt"));
        await catalog.SaveSkillAsync(admin, new SkillRecord(
            Guid.NewGuid(), "Skill", "Description", "Instruction", [agent.Id],
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var owner = new RequestPrincipal(agent.Id.ToString("D"));
        await permissionStore.SaveAsync(new PermissionPolicyRecord(
            owner.SubjectId, [], ["write_memory", "read_memory"], [],
            PermissionClearance.Independent, false, [], null, DateTimeOffset.UtcNow));
        var memory = await catalog.WriteMemoryAsync(owner, new(
            agent.Id, "preference", "Use concise answers", ["profile"]));

        Assert.That((await catalog.ListAgentsAsync()).Single().Name, Is.EqualTo("Test Agent"));
        Assert.That((await catalog.ListSkillsAsync()).Single().SkillText, Is.EqualTo("Instruction"));
        Assert.That((await catalog.SearchMemoryAsync(owner, agent.Id, "concise")).Single().Id, Is.EqualTo(memory.Id));
        Assert.That((await new AgentChatProfileResolver(catalog).ResolveAsync(
            new ChatTurnContext(Guid.NewGuid(), new ChatTurnInput("hi", Caller: owner),
                new ConversationSelection(Guid.NewGuid())), default)).ModelName, Is.EqualTo("model"));
    }

    private sealed class RecordingBuilder : ISharpClawModuleBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public RecordingContracts Contracts { get; } = new();
        IModuleContractBuilder ISharpClawModuleBuilder.Contracts => Contracts;
        public RecordingStorage Storage { get; } = new();
        IModuleStorageBuilder ISharpClawModuleBuilder.Storage => Storage;
        public RecordingActions Actions { get; } = new();
        IActionDefinitionBuilder ISharpClawModuleBuilder.Actions => Actions;
        public RecordingHooks Hooks { get; } = new();
        IActionHookBuilder ISharpClawModuleBuilder.Hooks => Hooks;
        public RecordingEvents Events { get; } = new();
        IEventDefinitionBuilder ISharpClawModuleBuilder.Events => Events;
        public RecordingTools Tools { get; } = new();
        IToolContributionBuilder ISharpClawModuleBuilder.Tools => Tools;
        public RecordingChat Chat { get; } = new();
        IChatLifecycleBuilder ISharpClawModuleBuilder.Chat => Chat;
    }

    private sealed class RecordingContracts : IModuleContractBuilder
    {
        public List<ModuleContractEntry> Exports { get; } = [];
        public List<ModuleContractEntry> Requires { get; } = [];
        public void Export<T>(string contractName, int schemaVersion = 1, int maxBytes = 65_536) =>
            Exports.Add(new(contractName, typeof(T), schemaVersion, false));
        public void Require<T>(string contractName, int minimumSchemaVersion = 1, bool optional = false) =>
            Requires.Add(new(contractName, typeof(T), minimumSchemaVersion, optional));
    }

    private sealed record ModuleContractEntry(string ContractName, Type ServiceType, int Version, bool Optional);

    private sealed class RecordingStorage : IModuleStorageBuilder
    {
        public List<ModuleStorageContractDescriptor> Items { get; } = [];
        public void Add(ModuleStorageContractDescriptor contract) => Items.Add(contract);
    }

    private sealed class RecordingActions : IActionDefinitionBuilder
    {
        public List<object> Items { get; } = [];
        public void Add<TAction, TResult>(ActionDescriptor<TAction, TResult> descriptor) => Items.Add(descriptor);
    }

    private sealed class RecordingHooks : IActionHookBuilder
    {
        private static readonly IActionHookRegistrationBuilder Registration = new NoOpActionRegistration();
        public IActionHookRegistrationBuilder For(SharpClawActionKey key) => Registration;
        public IActionHookRegistrationBuilder Category(string category) => Registration;
        public IActionHookRegistrationBuilder AnyAction() => Registration;
    }

    private sealed class NoOpActionRegistration : IActionHookRegistrationBuilder
    {
        public void Use<TInterceptor>(HookOrdering ordering) { }
        public void UseAny<TInterceptor>(HookOrdering ordering) { }
    }

    private sealed class RecordingEvents : IEventDefinitionBuilder
    {
        private static readonly IEventHookRegistrationBuilder Registration = new NoOpEventRegistration();
        public List<object> Items { get; } = [];
        public void Add<TEvent>(EventDescriptor<TEvent> descriptor) => Items.Add(descriptor);
        public IEventHookRegistrationBuilder For(SharpClawEventKey key) => Registration;
        public IEventHookRegistrationBuilder Category(string category) => Registration;
        public IEventHookRegistrationBuilder AnyEvent() => Registration;
    }

    private sealed class NoOpEventRegistration : IEventHookRegistrationBuilder
    {
        public void Intercept<TInterceptor>(HookOrdering ordering) { }
        public void InterceptAny<TInterceptor>(HookOrdering ordering) { }
        public void Listen<TListener>(EventDelivery delivery, HookOrdering ordering) { }
        public void ListenAny<TListener>(EventDelivery delivery, HookOrdering ordering) { }
    }

    private sealed class RecordingTools : IToolContributionBuilder
    {
        public List<ToolDescriptor> Items { get; } = [];
        public void Add<THandler>(ToolDescriptor descriptor) where THandler : IToolHandler => Items.Add(descriptor);
    }

    private sealed class RecordingChat : IChatLifecycleBuilder
    {
        public List<Type> Resolvers { get; } = [];
        public List<Type> Profiles { get; } = [];
        public List<Type> Contributors { get; } = [];
        public void UseConversationResolver<TResolver>(ExclusiveRegistration registration)
            where TResolver : IConversationResolver => Resolvers.Add(typeof(TResolver));
        public void UseChatProfileResolver<TResolver>(ExclusiveRegistration registration)
            where TResolver : IChatProfileResolver => Profiles.Add(typeof(TResolver));
        public void AddContextContributor<TContributor>() where TContributor : IChatContextContributor =>
            Contributors.Add(typeof(TContributor));
    }

    private sealed class InMemoryStorageGateway : IModuleStorageGateway
    {
        private sealed record Entry(JsonElement Value, JsonElement Indexes, long Revision);
        private readonly Dictionary<(string Module, string Storage, string Key), Entry> _records = [];

        public IReadOnlyList<ModuleStorageContractDescriptor> ListContracts() => [];

        public Task<JsonElement> InvokeAsync(
            string moduleId,
            string storageName,
            string operation,
            JsonElement parameters,
            CancellationToken ct = default)
        {
            var prefix = (moduleId, storageName);
            return Task.FromResult(operation switch
            {
                ModuleStorageOperations.Get => Get(prefix, parameters),
                ModuleStorageOperations.Upsert => Upsert(prefix, parameters),
                ModuleStorageOperations.Delete => Delete(prefix, parameters),
                ModuleStorageOperations.List => List(prefix),
                ModuleStorageOperations.Query => Query(prefix, parameters),
                ModuleStorageOperations.BatchUpsert => BatchUpsert(prefix, parameters),
                ModuleStorageOperations.BatchDelete => BatchDelete(prefix, parameters),
                _ => throw new NotSupportedException(operation),
            });
        }

        public Task<ModuleStorageMutationAndOutboxResult> CommitMutationAndOutboxAsync(
            string moduleId, string storageName, ModuleStorageMutationAndOutboxRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ModuleStorageClaimResult<T>> ClaimAsync<T>(
            string moduleId, string storageName, ModuleStorageClaimRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ModuleStorageClaimRenewalResult> RenewClaimAsync(
            string moduleId, string storageName, ModuleStorageClaimRenewalRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ModuleStorageClaimRecoveryResult> RecoverClaimAsync(
            string moduleId, string storageName, ModuleStorageClaimRecoveryRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();

        private JsonElement Get((string Module, string Storage) prefix, JsonElement parameters)
        {
            var key = parameters.GetProperty("key").GetString()!;
            if (!_records.TryGetValue((prefix.Module, prefix.Storage, key), out var entry))
                return JsonSerializer.SerializeToElement(new { found = false });
            return JsonSerializer.SerializeToElement(new
            {
                found = true,
                key,
                value = entry.Value,
                revision = entry.Revision,
                indexes = entry.Indexes,
            });
        }

        private JsonElement Upsert((string Module, string Storage) prefix, JsonElement parameters)
        {
            var key = parameters.GetProperty("key").GetString()!;
            var id = (prefix.Module, prefix.Storage, key);
            var revision = _records.TryGetValue(id, out var current) ? current.Revision + 1 : 1;
            var indexes = parameters.TryGetProperty("indexes", out var index) ? index.Clone() : JsonSerializer.SerializeToElement(new { });
            _records[id] = new(parameters.GetProperty("value").Clone(), indexes, revision);
            return JsonSerializer.SerializeToElement(new { saved = true, revision });
        }

        private JsonElement Delete((string Module, string Storage) prefix, JsonElement parameters)
        {
            var key = parameters.GetProperty("key").GetString()!;
            return JsonSerializer.SerializeToElement(new { deleted = _records.Remove((prefix.Module, prefix.Storage, key)) });
        }

        private JsonElement List((string Module, string Storage) prefix) =>
            JsonSerializer.SerializeToElement(new
            {
                records = _records.Where(item => item.Key.Module == prefix.Module && item.Key.Storage == prefix.Storage)
                    .Select(item => new { key = item.Key.Key, value = item.Value.Value, revision = item.Value.Revision, indexes = item.Value.Indexes }),
            });

        private JsonElement Query((string Module, string Storage) prefix, JsonElement parameters)
        {
            var records = _records.Where(item => item.Key.Module == prefix.Module && item.Key.Storage == prefix.Storage)
                .Select(item => new { key = item.Key.Key, entry = item.Value })
                .ToList();
            if (parameters.TryGetProperty("filters", out var filters))
            {
                foreach (var filter in filters.EnumerateArray())
                {
                    var indexName = filter.GetProperty("indexName").GetString()!;
                    var expected = filter.GetProperty("value").ToString();
                    records = records.Where(item => item.entry.Indexes.TryGetProperty(indexName, out var value)
                        && value.ToString() == expected).ToList();
                }
            }
            if (parameters.TryGetProperty("orderBy", out var order) && order.ValueKind == JsonValueKind.Object)
            {
                var indexName = order.GetProperty("indexName").GetString()!;
                var descending = order.GetProperty("direction").GetString() == ModuleStorageSortDirections.Descending;
                records = (descending
                    ? records.OrderByDescending(item => item.entry.Indexes.TryGetProperty(indexName, out var value) ? value.ToString() : "")
                    : records.OrderBy(item => item.entry.Indexes.TryGetProperty(indexName, out var value) ? value.ToString() : "")).ToList();
            }
            if (parameters.TryGetProperty("limit", out var limit) && limit.ValueKind == JsonValueKind.Number && limit.TryGetInt32(out var count))
                records = records.Take(count).ToList();
            return JsonSerializer.SerializeToElement(new
            {
                records = records.Select(item => new { key = item.key, value = item.entry.Value, revision = item.entry.Revision, indexes = item.entry.Indexes }),
            });
        }

        private JsonElement BatchUpsert((string Module, string Storage) prefix, JsonElement parameters)
        {
            var saved = 0;
            foreach (var record in parameters.GetProperty("records").EnumerateArray())
            {
                Upsert(prefix, record);
                saved++;
            }
            return JsonSerializer.SerializeToElement(new { saved });
        }

        private JsonElement BatchDelete((string Module, string Storage) prefix, JsonElement parameters)
        {
            var deleted = 0;
            foreach (var key in parameters.GetProperty("keys").EnumerateArray())
            {
                if (_records.Remove((prefix.Module, prefix.Storage, key.GetString()!)))
                    deleted++;
            }
            return JsonSerializer.SerializeToElement(new { deleted });
        }
    }
}
