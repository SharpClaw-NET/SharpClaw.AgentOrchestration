using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Modules.Agents;
using SharpClaw.Modules.AgentOrchestration.Contracts;
using SharpClaw.Modules.Context;
using SharpClaw.ModuleSDK;
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
        var contextGraph = CompileModule(context);
        var permissionGraph = CompileModule(permission);
        var agentsGraph = CompileModule(agents);
        var contextCreate = GetAction<ContextCreateThreadAction, ContextThreadRecord>(contextGraph, "context.thread.create");
        var contextCommit = GetAction<ContextCommitExchangeAction, bool>(contextGraph, "context.conversation.commit");
        var contextSteeringRecord = GetAction<ContextRecordSteeringAction, ContextSteeringRecord>(contextGraph, ContextSteeringActionKeys.Record);
        var contextSteeringList = GetAction<ContextListSteeringAction, IReadOnlyList<ContextSteeringRecord>>(contextGraph, ContextSteeringActionKeys.List);
        var permissionGrant = GetAction<PermissionGrantAction, bool>(permissionGraph, "permission.grant");
        var agentsSaveSkill = GetAction<AgentsSaveSkillAction, SkillRecord>(agentsGraph, "agents.skill.save");
        var agentsRecordJob = GetAction<AgentsRecordJobAction, AgentJob>(agentsGraph, AgentsModule.RecordAgentJobAction);
        var agentsAttachJob = GetAction<AgentsAttachCanonicalJobAction, AgentJob>(agentsGraph, AgentsModule.AttachCanonicalJobAction);
        var agentsCompleteJob = GetAction<AgentsCompleteJobAction, AgentJob>(agentsGraph, AgentsModule.CompleteAgentJobAction);
        var agentsImportJobs = GetAction<AgentsImportJobsAction, IReadOnlyList<AgentJob>>(agentsGraph, AgentsModule.ImportAgentJobsAction);

        Assert.Multiple(() =>
        {
            Assert.That(context.Identity, Is.EqualTo(new ModuleIdentity(
                "sharpclaw_context", "SharpClaw Context", "ctx")));
            Assert.That(permission.Identity, Is.EqualTo(new ModuleIdentity(
                "sharpclaw_two_tier_permission", "SharpClaw Two Tier Permission", "perm")));
            Assert.That(agents.Identity, Is.EqualTo(new ModuleIdentity(
                "sharpclaw_agents", "SharpClaw Agents", "agents")));
            Assert.That(contextGraph.Storage.Select(item => item.StorageName), Is.EquivalentTo(
                new[] { "channels", "contexts", "threads", "messages", "steering" }));
            Assert.That(permissionGraph.Storage.Select(item => item.StorageName), Is.EquivalentTo(
                new[] { "policies", "grants", "approvals", "roles", "permission_sets" }));
            Assert.That(
                TwoTierPermissionModule.StorageContracts
                    .Single(item => item.StorageName == PermissionPolicyStore.PoliciesStorage)
                    .Indexes!
                    .Select(item => (item.Name, item.ValueKind)),
                Is.EquivalentTo(new[]
                {
                    ("subjectId", ModuleStorageIndexValueKind.String),
                    ("clearance", ModuleStorageIndexValueKind.String),
                    ("updatedAt", ModuleStorageIndexValueKind.DateTime),
                }));
            Assert.That(agentsGraph.Storage.Select(item => item.StorageName), Is.EquivalentTo(
                new[] { "agents", "skills", "memory", "costs", "synchronization", "agent_jobs", "agent_job_imports" }));
            Assert.That(permissionGraph.Contracts.Where(item => item.IsExport).Select(item => item.ContractName),
                Does.Contain("sharpclaw.permission"));
            Assert.That(agentsGraph.Contracts.Where(item => !item.IsExport).Select(item => item.ContractName),
                Is.EquivalentTo(new[] { "sharpclaw.context", "sharpclaw.permission" }));
            Assert.That(contextGraph.Contracts.Where(item => !item.IsExport).Select(item => item.ContractName),
                Does.Not.Contain("sharpclaw.context-access"));
            Assert.That(permissionGraph.Contracts.Where(item => item.IsExport).Select(item => item.ContractName),
                Does.Not.Contain("sharpclaw.context-access"));
            Assert.That(permissionGraph.Contracts.Where(item => item.IsExport).Select(item => item.ContractName),
                Does.Not.Contain("sharpclaw.agent-access"));
            Assert.That(contextGraph.Services.Any(item => item.ServiceType == typeof(IContextActionExecutor)), Is.True);
            Assert.That(contextGraph.Services.Any(item => item.ServiceType == typeof(IContextSteeringActionExecutor)), Is.True);
            Assert.That(contextGraph.Services.Any(item => item.ServiceType == typeof(HostPermissionActionEntry)), Is.True);
            Assert.That(permissionGraph.Services.Any(item => item.ServiceType == typeof(IPermissionActionExecutor)), Is.True);
            Assert.That(agentsGraph.Services.Any(item => item.ServiceType == typeof(IAgentsActionExecutor)), Is.True);
            Assert.That(agentsGraph.Services.Any(item => item.ServiceType == typeof(HostPermissionActionEntry)), Is.True);
            Assert.That(agentsGraph.Services.Any(item => item.ServiceType == typeof(IAgentsJobActionExecutor)), Is.True);
            Assert.That(contextCreate.SafePoints, Is.Not.Empty);
            Assert.That(contextCommit.Capabilities,
                Is.EqualTo(ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel));
            Assert.That(contextSteeringRecord.Key.Value, Is.EqualTo(ContextSteeringActionKeys.Record));
            Assert.That(contextSteeringList.Key.Value, Is.EqualTo(ContextSteeringActionKeys.List));
            Assert.That(permissionGrant.SafePoints, Is.Not.Empty);
            Assert.That(permissionGraph.Actions.Any(item => ReferenceEquals(item.TypedDescriptor, PermissionActionDescriptors.ContextAccess)), Is.True);
            Assert.That(permissionGraph.Actions.Any(item => ReferenceEquals(item.TypedDescriptor, PermissionActionDescriptors.AgentAccess)), Is.True);
            Assert.That(agentsSaveSkill.SafePoints, Is.Not.Empty);
            Assert.That(agentsRecordJob.Key.Value,
                Is.EqualTo(AgentsModule.RecordAgentJobAction));
            Assert.That(agentsAttachJob.Key.Value,
                Is.EqualTo(AgentsModule.AttachCanonicalJobAction));
            Assert.That(agentsCompleteJob.Key.Value,
                Is.EqualTo(AgentsModule.CompleteAgentJobAction));
            Assert.That(agentsImportJobs.Key.Value, Is.EqualTo(AgentsModule.ImportAgentJobsAction));
            Assert.That(
                AgentsModule.StorageContracts.Single(item => item.StorageName == AgentsCatalog.AgentJobsStorage).Indexes!
                    .Select(item => item.Name),
                Is.EquivalentTo(new[]
                {
                    "agentId", "callerIdentity", "actionIdentity", "resource", "canonicalJobId",
                    "channelId", "contextId", "permissionIdentity", "status", "handlerKey", "payloadCodec",
                    "recoveryMode", "createdAt", "updatedAt",
                }));
            Assert.That(
                AgentsModule.StorageContracts.Single(item => item.StorageName == AgentsCatalog.AgentJobImportsStorage).Indexes!
                    .Select(item => item.Name),
                Is.EquivalentTo(new[]
                {
                    "snapshotId", "aggregateHash", "mappingHash", "expectedRecordCount", "importedRecordCount",
                    "completed", "capturedAt",
                }));
            Assert.That(contextGraph.Events.Count(item => item.Descriptor.Key.Value == ContextModule.ThreadChangedEvent), Is.EqualTo(1));
            Assert.That(permissionGraph.Events.Count(item => item.Descriptor.Key.Value == TwoTierPermissionModule.PermissionChangedEvent), Is.EqualTo(1));
            Assert.That(agentsGraph.Events.Count(item => item.Descriptor.Key.Value == AgentsModule.MemoryChangedEvent), Is.EqualTo(1));
            Assert.That(contextGraph.ActionHooks.Any(item => item.ActionKey?.Value == "context.conversation.commit"), Is.True);
            Assert.That(contextGraph.ActionHooks.Any(item => item.ActionKey?.Value == "permission.context-access"
                && item.HandlerType == typeof(ContextPermissionActionHook)
                && item.HookId == "permission.context-access.host-entry"), Is.True);
            Assert.That(permissionGraph.ActionHooks.Any(item => item.ActionKey?.Value == "permission.grant"), Is.True);
            Assert.That(agentsGraph.ActionHooks.Any(item => item.ActionKey?.Value == "agents.create"), Is.True);
            Assert.That(agentsGraph.ActionHooks.Any(item => item.ActionKey?.Value == "permission.agent-access"
                && item.HandlerType == typeof(AgentsPermissionActionHook)
                && item.HookId == "permission.agent-access.host-entry"), Is.True);
            Assert.That(contextGraph.ActionEntries.Select(item => item.Descriptor.Key.Value), Is.EqualTo([
                ContextModule.ApiDescriptor.Key.Value,
                ContextSteeringActionKeys.Record,
                ContextSteeringActionKeys.List]));
            Assert.That(permissionGraph.ActionEntries.Select(item => item.Descriptor.Key.Value), Is.EquivalentTo([
                TwoTierPermissionModule.ApiDescriptor.Key.Value,
                PermissionActionDescriptors.ContextAccess.Key.Value,
                PermissionActionDescriptors.AgentAccess.Key.Value]));
            Assert.That(agentsGraph.ActionEntries.Select(item => item.Descriptor.Key.Value), Is.EqualTo([AgentsModule.ApiDescriptor.Key.Value]));
            Assert.That(contextGraph.ActionEntries.Single(item => item.Descriptor.Key.Value == ContextModule.ApiDescriptor.Key.Value).TerminalType,
                Is.EqualTo(typeof(ContextApiActionTerminal)));
            Assert.That(contextGraph.ActionEntries.Single(item => item.Descriptor.Key.Value == ContextSteeringActionKeys.Record).TerminalType,
                Is.EqualTo(typeof(ContextSteeringRecordActionTerminal)));
            Assert.That(contextGraph.ActionEntries.Single(item => item.Descriptor.Key.Value == ContextSteeringActionKeys.List).TerminalType,
                Is.EqualTo(typeof(ContextSteeringListActionTerminal)));
            Assert.That(permissionGraph.ActionEntries.Single(item => item.Descriptor.Key.Value == PermissionActionDescriptors.ContextAccess.Key.Value).TerminalType,
                Is.EqualTo(typeof(PermissionContextAccessActionTerminal)));
            Assert.That(agentsGraph.ActionEntries.Single().TerminalType, Is.EqualTo(typeof(AgentsApiActionTerminal)));
        });
    }

    [Test]
    public void ManifestsUseCurrentThreeOwnerComposition()
    {
        (string Manifest, string Id, string ModuleType, string Assembly, string Version)[] expected =
        {
        ("Context.module.json", "sharpclaw_context", "SharpClaw.Modules.Context.ContextModule", "SharpClaw.Modules.Context.dll", "0.5.0-beta.14"),
        ("TwoTierPermission.module.json", "sharpclaw_two_tier_permission", "SharpClaw.Modules.TwoTierPermission.TwoTierPermissionModule", "SharpClaw.Modules.TwoTierPermission.dll", "0.5.0-beta.15"),
        ("Agents.module.json", "sharpclaw_agents", "SharpClaw.Modules.Agents.AgentsModule", "SharpClaw.Modules.Agents.dll", "0.5.0-beta.15"),
        };

        foreach (var item in expected)
        {
            var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "manifests", item.Manifest);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            Assert.Multiple(() =>
            {
                Assert.That(root.GetProperty("id").GetString(), Is.EqualTo(item.Id));
                Assert.That(root.GetProperty("version").GetString(), Is.EqualTo(item.Version));
                Assert.That(root.GetProperty("entryAssembly").GetString(), Is.EqualTo(item.Assembly));
                Assert.That(root.GetProperty("moduleType").GetString(), Is.EqualTo(item.ModuleType));
                Assert.That(root.GetProperty("defaultEnabled").GetBoolean(), Is.True);
                Assert.That(root.GetProperty("hostMode").GetString(), Is.EqualTo("sidecar"));
                Assert.That(root.GetProperty("requestedHooks").GetArrayLength(), Is.GreaterThan(0));
                var hookTargets = root.GetProperty("requestedHooks")
                    .EnumerateArray()
                    .Select(hook => hook.GetProperty("target").GetString())
                    .ToArray();
                if (item.Item2 == "sharpclaw_context")
                    Assert.That(hookTargets, Does.Contain("permission.context-access"));
                if (item.Item2 == "sharpclaw_agents")
                    Assert.That(hookTargets, Does.Contain("permission.agent-access"));
            });
        }
    }

    [Test]
    public void ApplicationContributionsDeclareOwnedApiEndpoints()
    {
        var expected = new[]
        {
            (Module: (ISharpClawApplicationModule)new ContextModule(),
                Contribution: typeof(ContextEndpointContribution),
                Routes: new[]
                {
                    ContextEndpointContribution.CreateThreadRoute,
                    ContextEndpointContribution.ReadHistoryRoute,
                    ContextEndpointContribution.CommitExchangeRoute,
                }),
            (Module: (ISharpClawApplicationModule)new TwoTierPermissionModule(),
                Contribution: typeof(PermissionEndpointContribution),
                Routes: new[]
                {
                    PermissionEndpointContribution.EvaluateRoute,
                    PermissionEndpointContribution.GrantRoute,
                    PermissionEndpointContribution.RevokeRoute,
                    PermissionEndpointContribution.ApproveRoute,
                }),
            (Module: (ISharpClawApplicationModule)new AgentsModule(),
                Contribution: typeof(AgentsEndpointContribution),
                Routes: new[]
                {
                    AgentsEndpointContribution.CreateRoute,
                    AgentsEndpointContribution.UpdateRoute,
                    AgentsEndpointContribution.WriteMemoryRoute,
                    AgentsEndpointContribution.SearchMemoryRoute,
                    AgentsEndpointContribution.SaveSkillRoute,
                    AgentsEndpointContribution.AccessSkillRoute,
                }),
        };

        foreach (var item in expected)
        {
            var application = new RecordingApplicationBuilder();
            item.Module.ConfigureApplication(application);

            Assert.Multiple(() =>
            {
                Assert.That(
                    application.Endpoints.Items.Select(endpoint => endpoint.HandlerType),
                    Is.All.EqualTo(item.Contribution));
                Assert.That(
                    application.Endpoints.Items.Select(endpoint => endpoint.Descriptor.Path),
                    Is.SupersetOf(item.Routes));
                Assert.That(
                    application.Endpoints.Items.Select(endpoint => endpoint.Descriptor.Transport),
                    Is.All.EqualTo(HostEndpointTransport.Http));
                Assert.That(item.Routes, Is.All.Not.Null);
                Assert.That(item.Routes, Is.All.Not.Empty);
            });
        }
    }

    [Test]
    public void PublicEndpointContributionsImplementTheSidecarEndpointContract()
    {
        Assert.Multiple(() =>
        {
            Assert.That(typeof(IModuleHttpEndpointHandler).IsAssignableFrom(typeof(ContextEndpointContribution)), Is.True);
            Assert.That(typeof(IModuleHttpEndpointHandler).IsAssignableFrom(typeof(PermissionEndpointContribution)), Is.True);
            Assert.That(typeof(IModuleHttpEndpointHandler).IsAssignableFrom(typeof(AgentsEndpointContribution)), Is.True);
        });

        var contextGraph = CompileModule(new ContextModule());
        var permissionGraph = CompileModule(new TwoTierPermissionModule());
        var agentsGraph = CompileModule(new AgentsModule());

        Assert.Multiple(() =>
        {
            Assert.That(contextGraph.Services.Any(item => item.ServiceType == typeof(ContextEndpointContribution)), Is.True);
            Assert.That(permissionGraph.Services.Any(item => item.ServiceType == typeof(PermissionEndpointContribution)), Is.True);
            Assert.That(agentsGraph.Services.Any(item => item.ServiceType == typeof(AgentsEndpointContribution)), Is.True);
        });
    }

    [Test]
    public void ApiRouteMatricesPreserveTheFormerOwnerSurface()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ContextEndpointContribution.ChannelRoutes, Has.Count.EqualTo(13));
            Assert.That(ContextEndpointContribution.ChannelContextRoutes, Has.Count.EqualTo(12));
            Assert.That(ContextEndpointContribution.ThreadRoutes, Has.Count.EqualTo(5));
            Assert.That(ContextEndpointContribution.ThreadRoutes, Is.Unique);
            Assert.That(ContextEndpointContribution.CreateThreadRoute, Is.EqualTo("/sharpclaw/context/threads"));
            Assert.That(ContextEndpointContribution.ReadHistoryRoute, Is.EqualTo("/sharpclaw/context/history"));
            Assert.That(ContextEndpointContribution.CommitExchangeRoute, Is.EqualTo("/sharpclaw/context/exchanges"));
            Assert.That(AgentsEndpointContribution.AgentRoutes, Does.Contain("/sharpclaw/agents/list"));
            Assert.That(AgentsEndpointContribution.AgentRoutes, Does.Contain("/sharpclaw/agents/get"));
            Assert.That(AgentsEndpointContribution.AgentRoutes, Does.Contain("/sharpclaw/agents/delete"));
            Assert.That(AgentsEndpointContribution.AgentRoutes, Does.Contain("/sharpclaw/agents/role"));
            Assert.That(AgentsEndpointContribution.AgentRoutes, Does.Contain("/sharpclaw/agents/synchronize"));
            Assert.That(AgentsEndpointContribution.AgentRoutes, Does.Contain("/sharpclaw/agents/cost"));
            Assert.That(PermissionEndpointContribution.PolicyRoutes, Has.Count.EqualTo(4));
            Assert.That(PermissionEndpointContribution.RoleRoutes, Has.Count.EqualTo(5));
            Assert.That(PermissionEndpointContribution.PermissionSetRoutes, Has.Count.EqualTo(5));
        });
    }

    [Test]
    public void PublicApiActionsAreRegisteredForEachOwner()
    {
        var contextGraph = CompileModule(new ContextModule());
        var permissionGraph = CompileModule(new TwoTierPermissionModule());
        var agentsGraph = CompileModule(new AgentsModule());

        Assert.Multiple(() =>
        {
            Assert.That(contextGraph.Actions.Select(item => item.Descriptor.Key.Value), Does.Contain("context.api.dispatch"));
            Assert.That(permissionGraph.Actions.Select(item => item.Descriptor.Key.Value), Does.Contain("permission.api.dispatch"));
            Assert.That(agentsGraph.Actions.Select(item => item.Descriptor.Key.Value), Does.Contain("agents.api.dispatch"));
            Assert.That(AgentsApiOperations.ImportAgentJobs, Is.EqualTo(AgentsModule.ImportAgentJobsAction));
        });
    }

    [Test]
    public void TypedIngressActionsBindTheirPublishedSchemas()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ContextModule.ApiDescriptor.InputSchema, Is.EqualTo(new JsonSchemaReference(
                "sharpclaw.kernel.action.input.context.api.dispatch",
                1,
                "941361CD8AD62ECC21CD1B23957542A777266F05009DD3A8849608C8F52FD961")));
            Assert.That(ContextModule.ApiDescriptor.ResultSchema, Is.EqualTo(new JsonSchemaReference(
                "sharpclaw.kernel.action.result.context.api.dispatch",
                1,
                "8EB000590FB81F540B757EE45A1DB72EF84BF921444663E7AAF07B0F2711CB8D")));
            Assert.That(TwoTierPermissionModule.ApiDescriptor.InputSchema, Is.EqualTo(new JsonSchemaReference(
                "sharpclaw.kernel.action.input.permission.api.dispatch",
                1,
                "9730C734344C8CDCC030B54D093217D8AD4038346CC0AB54494A00FD1A346D43")));
            Assert.That(TwoTierPermissionModule.ApiDescriptor.ResultSchema, Is.EqualTo(new JsonSchemaReference(
                "sharpclaw.kernel.action.result.permission.api.dispatch",
                1,
                "6FC66027153DC70AF18F195B681CFA9EC51D26D0528ADC664BBC10395E07A379")));
            Assert.That(AgentsModule.ApiDescriptor.InputSchema, Is.EqualTo(new JsonSchemaReference(
                "sharpclaw.kernel.action.input.agents.api.dispatch",
                1,
                "27B5426804CE4372B54F88B8516A9E545DCF4023778CB8CD8BB9413309544628")));
            Assert.That(AgentsModule.ApiDescriptor.ResultSchema, Is.EqualTo(new JsonSchemaReference(
                "sharpclaw.kernel.action.result.agents.api.dispatch",
                1,
                "EBF621A68F0061626C140836F73212CB5D24ABF6D6FE9FAE994E6C3BA794FB65")));
            Assert.That(PermissionActionDescriptors.ContextAccess.InputSchema, Is.EqualTo(new JsonSchemaReference(
                "sharpclaw.kernel.action.input.permission.context-access",
                1,
                "EF52C526C7B77C146B2D16A61B3BB1728BC4F8500763C8EF1A21FC65B981283B")));
            Assert.That(PermissionActionDescriptors.AgentAccess.ResultSchema, Is.EqualTo(new JsonSchemaReference(
                "sharpclaw.kernel.action.result.permission.agent-access",
                1,
                "E2C4F31D2F6A8637E1AF2BA13B276A313BCC451E78E87EEE6B06F468D01C4287")));
        });
    }

    [Test]
    public void CliCommandsCoverTheOwnerAdministrationMatrices()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ContextCliHandler.Commands, Has.Count.EqualTo(29));
            Assert.That(PermissionCliHandler.Commands, Has.Count.EqualTo(18));
            Assert.That(AgentsCliHandler.Commands, Has.Count.EqualTo(12));
        });

        var contextApplication = new RecordingApplicationBuilder();
        var permissionApplication = new RecordingApplicationBuilder();
        var agentsApplication = new RecordingApplicationBuilder();
        new ContextModule().ConfigureApplication(contextApplication);
        new TwoTierPermissionModule().ConfigureApplication(permissionApplication);
        new AgentsModule().ConfigureApplication(agentsApplication);

        Assert.Multiple(() =>
        {
            Assert.That(contextApplication.Cli.Items, Has.Count.EqualTo(ContextCliHandler.Commands.Count));
            Assert.That(permissionApplication.Cli.Items, Has.Count.EqualTo(PermissionCliHandler.Commands.Count));
            Assert.That(agentsApplication.Cli.Items, Has.Count.EqualTo(AgentsCliHandler.Commands.Count));
        });
    }

    [Test]
    public async Task ModuleActionPipelineUsesTheActionContextSnapshot()
    {
        var dispatcher = new RecordingActionDispatcher();
        var pipeline = new ModuleActionPipeline(dispatcher);
        var caller = RequestPrincipal.Anonymous;
        var action = new ContextApiAction(
            ContextApiOperations.ListChannels,
            JsonSerializer.SerializeToElement(new { }));
        var snapshot = new ActionPipelineSnapshot("test", [], [], 16);
        var context = new ActionContext<ContextApiAction>(
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            1,
            DateTimeOffset.UtcNow.AddMinutes(1),
            ContextModule.ApiDescriptor.Key,
            ContextModule.ModuleIdValue,
            caller,
            action,
            ExtensionFeatureSet.Empty,
            snapshot);

        var result = await pipeline.RunRequiredAsync(
            ContextModule.ApiDescriptor,
            context,
            (value, _) => ValueTask.FromResult(JsonSerializer.SerializeToElement(value.Action.Operation)));

        Assert.Multiple(() =>
        {
            Assert.That(result.GetString(), Is.EqualTo(ContextApiOperations.ListChannels));
            Assert.That(dispatcher.RequiredCalls, Is.EqualTo(1));
            Assert.That(dispatcher.Snapshot, Is.SameAs(snapshot));
        });
    }

    [Test]
    public async Task ApplicationGatewaysUseTheHostActionEntry()
    {
        var host = new RecordingHostActionEntry();
        var caller = new RequestPrincipal("probe", IsAuthenticated: true);
        var hostContext = TestHostActionContext.Create(caller, HostActionEntryIngress.CrossModule);
        var payload = JsonSerializer.SerializeToElement(new { });
        var contextStorage = new InMemoryStorageGateway();
        var contextStore = new ContextStore(contextStorage, AllowAllEntry());
        var contextExecutor = new ContextApiActionExecutor(contextStore, AllowAllEntry());
        var permissionStorage = new InMemoryStorageGateway();
        var permissionStore = new PermissionPolicyStore(permissionStorage);
        var permissionExecutor = new PermissionApiActionExecutor(
            new TwoTierPermissionPolicy(permissionStore),
            permissionStore);
        var agentsCatalog = new AgentsCatalog(new InMemoryStorageGateway(), AllowAllEntry());
        var agentsExecutor = new AgentsApiActionExecutor(agentsCatalog, AllowAllEntry());

        await new ContextActionGateway(
                new HostModuleActionEntry(host),
                new ContextApiActionTerminal(contextExecutor))
            .ExecuteAsync(hostContext, ContextApiOperations.ListChannels, payload);
        await new PermissionActionGateway(
                new HostModuleActionEntry(host),
                new PermissionApiActionTerminal(permissionExecutor))
            .ExecuteAsync(hostContext, PermissionApiOperations.ListPolicies, payload);
        await new AgentsActionGateway(
                new HostModuleActionEntry(host),
                new AgentsApiActionTerminal(agentsExecutor))
            .ExecuteAsync(hostContext, AgentsApiOperations.ListAgents, payload);

        Assert.That(host.Keys, Is.EqualTo([
            ContextModule.ApiDescriptor.Key.Value,
            TwoTierPermissionModule.ApiDescriptor.Key.Value,
            AgentsModule.ApiDescriptor.Key.Value,
        ]));
        Assert.That(host.Contexts, Has.Exactly(3).Items);
        Assert.That(host.Contexts, Is.All.SameAs(hostContext));
        Assert.That(host.TerminalTypes, Is.EqualTo([
            typeof(ContextApiActionTerminal),
            typeof(PermissionApiActionTerminal),
            typeof(AgentsApiActionTerminal)]));
    }

    [Test]
    public async Task AgentsApiActionExecutorRoutesAgentJobImportOperation()
    {
        var now = DateTimeOffset.UtcNow;
        var source = NeutralRecord(now, "queued");
        var snapshot = new CanonicalJobsImportSnapshot(
            "api-import",
            now,
            [source],
            [new("legacy.agent", AgentJobHandlerKeys.Canonical, AgentJobPayloadCodecs.JsonV1)]);
        var caller = new RequestPrincipal(
            "importer",
            "Importer",
            new HashSet<string>(["admin"]),
            true);
        var action = new AgentsApiAction(
            AgentsApiOperations.ImportAgentJobs,
            JsonSerializer.SerializeToElement(snapshot));
        var context = new ActionContext<AgentsApiAction>(
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            1,
            now.AddMinutes(1),
            AgentsModule.ApiDescriptor.Key,
            AgentsModule.ModuleIdValue,
            caller,
            action,
            ExtensionFeatureSet.Empty,
            new ActionPipelineSnapshot("test", [], [], 16))
        {
            HostActionEntry = new RecordingHostActionEntry(),
        };
        var gateway = new InMemoryStorageGateway();
        var executor = new AgentsApiActionExecutor(
            new AgentsCatalog(gateway, AllowAllEntry()),
            AllowAllEntry());

        var result = await executor.ExecuteAsync(context);
        var imported = result.Deserialize<List<AgentJob>>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Multiple(() =>
        {
            Assert.That(imported, Has.Count.EqualTo(1));
            Assert.That(imported![0].Id, Is.EqualTo(source.SourceId));
            Assert.That(imported[0].RecoveryMode, Is.EqualTo(AgentJobRecoveryModes.CanonicalHandler));
        });
    }

    [Test]
    public async Task AgentsJobImportUsesTypedPermissionClearanceForAuthorizedWorker()
    {
        var now = DateTimeOffset.UtcNow;
        var worker = new RequestPrincipal(
            "22222222-2222-2222-2222-222222222222",
            Roles: new HashSet<string>(["admin"]),
            IsAuthenticated: true);
        var gateway = new InMemoryStorageGateway();
        var permissionStore = new PermissionPolicyStore(gateway);
        var policy = new TwoTierPermissionPolicy(permissionStore);
        await permissionStore.SaveAsync(new PermissionPolicyRecord(
            worker.SubjectId,
            [],
            ["manage_agent_jobs"],
            [],
            PermissionClearance.Independent,
            false,
            [],
            null,
            now));
        var storedPolicy = await permissionStore.GetAsync(worker.SubjectId);

        var host = new TypedPermissionHostActionEntry(policy, worker);
        var permission = new HostPermissionActionEntry(host);
        var catalog = new AgentsCatalog(gateway, permission);
        var executor = new AgentsApiActionExecutor(catalog, permission);
        var source = NeutralRecord(now, "queued");
        var snapshot = new CanonicalJobsImportSnapshot(
            "authorized-api-import",
            now,
            [source],
            [new("legacy.agent", AgentJobHandlerKeys.Canonical, AgentJobPayloadCodecs.JsonV1)]);
        var action = new AgentsApiAction(
            AgentsApiOperations.ImportAgentJobs,
            JsonSerializer.SerializeToElement(snapshot));

        var result = await executor.ExecuteAsync(
            CreateAgentsApiContext(worker, action, host, now));
        var imported = result.Deserialize<List<AgentJob>>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var state = await catalog.GetAgentJobImportStateAsync(snapshot.SnapshotId);

        Assert.Multiple(() =>
        {
            Assert.That(storedPolicy?.Capabilities, Does.Contain("manage_agent_jobs"));
            Assert.That(storedPolicy?.Clearance, Is.EqualTo(PermissionClearance.Independent));
            Assert.That(storedPolicy?.RequireSourceOptIn, Is.False);
            Assert.That(storedPolicy?.Roles, Is.Empty);
            Assert.That(host.LastCallerSubjectId, Is.EqualTo(worker.SubjectId));
            Assert.That(host.LastCallerRoles, Is.EquivalentTo(new[] { "admin" }));
            Assert.That(host.AgentAccessCalls, Is.EqualTo(1));
            Assert.That(host.LastDescriptorKey, Is.EqualTo(PermissionActionDescriptors.AgentAccess.Key.Value));
            Assert.That(host.LastAction?.Capability, Is.EqualTo("manage_agent_jobs"));
            Assert.That(host.LastDecision?.Allowed, Is.True);
            Assert.That(host.LastDecision?.Code, Is.EqualTo("administrator"));
            Assert.That(host.LastDecision?.Tier, Is.EqualTo(2));
            Assert.That(host.LastDecision?.Clearance, Is.EqualTo(PermissionClearance.Independent));
            Assert.That(imported, Has.Count.EqualTo(1));
            Assert.That(state?.Completed, Is.True);
        });
    }

    [Test]
    public async Task AgentsJobImportDeniesUngrantedWorkerBeforeAgentWrites()
    {
        var now = DateTimeOffset.UtcNow;
        var worker = new RequestPrincipal(
            "33333333-3333-3333-3333-333333333333",
            IsAuthenticated: true);
        var gateway = new InMemoryStorageGateway();
        var host = new TypedPermissionHostActionEntry(
            new TwoTierPermissionPolicy(new PermissionPolicyStore(gateway)),
            worker);
        var permission = new HostPermissionActionEntry(host);
        var catalog = new AgentsCatalog(gateway, permission);
        var executor = new AgentsApiActionExecutor(catalog, permission);
        var source = NeutralRecord(now, "queued");
        var snapshot = new CanonicalJobsImportSnapshot(
            "denied-api-import",
            now,
            [source],
            [new("legacy.agent", AgentJobHandlerKeys.Canonical, AgentJobPayloadCodecs.JsonV1)]);
        var action = new AgentsApiAction(
            AgentsApiOperations.ImportAgentJobs,
            JsonSerializer.SerializeToElement(snapshot));

        var exception = Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await executor.ExecuteAsync(CreateAgentsApiContext(worker, action, host, now)));
        var jobs = await catalog.ListAgentJobsAsync();
        var state = await catalog.GetAgentJobImportStateAsync(snapshot.SnapshotId);

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Is.EqualTo("The caller has no usable clearance."));
            Assert.That(host.LastCallerSubjectId, Is.EqualTo(worker.SubjectId));
            Assert.That(host.LastCallerRoles, Is.Null);
            Assert.That(host.AgentAccessCalls, Is.EqualTo(1));
            Assert.That(host.LastDescriptorKey, Is.EqualTo(PermissionActionDescriptors.AgentAccess.Key.Value));
            Assert.That(host.LastAction?.Capability, Is.EqualTo("manage_agent_jobs"));
            Assert.That(host.LastDecision?.Allowed, Is.False);
            Assert.That(host.LastDecision?.Code, Is.EqualTo("clearance_denied"));
            Assert.That(host.LastDecision?.Tier, Is.EqualTo(1));
            Assert.That(host.LastDecision?.Message, Is.EqualTo("The caller has no usable clearance."));
            Assert.That(host.LastDecision?.Clearance, Is.EqualTo(PermissionClearance.Unset));
            Assert.That(jobs, Is.Empty);
            Assert.That(state, Is.Null);
        });
    }

    [Test]
    public async Task ContextCliPassesTheIssuedAuthorityContextToTheTypedAction()
    {
        var gateway = new RecordingContextGateway();
        using var provider = new ServiceCollection()
            .AddScoped<IContextActionGateway>(_ => gateway)
            .BuildServiceProvider();
        var handler = new ContextCliHandler(provider.GetRequiredService<IServiceScopeFactory>());
        var hostContext = TestHostActionContext.Create(
            new RequestPrincipal(Guid.NewGuid().ToString("D"), IsAuthenticated: true),
            HostActionEntryIngress.Cli);

        var result = await handler.ExecuteAsync(
            new ModuleCliInvocation(
                Guid.NewGuid(),
                "ctx-channel-list",
                [],
                hostContext),
            default);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(gateway.Contexts, Has.Exactly(1).Items);
            Assert.That(gateway.Contexts.Single(), Is.SameAs(hostContext));
            Assert.That(gateway.Operations.Single(), Is.EqualTo(ContextApiOperations.ListChannels));
        });
    }

    [Test]
    public async Task ContextToolPassesTheIssuedAuthorityContextToTheTypedAction()
    {
        var gateway = new RecordingContextGateway();
        var handler = new ContextToolHandler(gateway);
        var hostContext = TestHostActionContext.Create(
            new RequestPrincipal(Guid.NewGuid().ToString("D"), IsAuthenticated: true),
            HostActionEntryIngress.Tool);
        using var arguments = JsonDocument.Parse($$"""{"channelId":"{{Guid.NewGuid():D}}"}""");

        var result = await handler.InvokeAsync(
            new ToolInvocation(
                Guid.NewGuid(),
                null,
                "call",
                ContextModule.ListThreadsTool,
                arguments.RootElement,
                hostContext),
            default);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False);
            Assert.That(gateway.Contexts, Has.Exactly(1).Items);
            Assert.That(gateway.Contexts.Single(), Is.SameAs(hostContext));
            Assert.That(gateway.Operations.Single(), Is.EqualTo(ContextApiOperations.ListThreads));
        });
    }

    [Test]
    public async Task ContextEndpointPassesTheIssuedAuthorityContextToTheTypedAction()
    {
        var hostContext = TestHostActionContext.Create(
            new RequestPrincipal(Guid.NewGuid().ToString("D"), IsAuthenticated: true),
            HostActionEntryIngress.Endpoint);
        ModuleEndpointRouteDescriptor route = ContextEndpointContribution.EndpointRoutes.Single(
            candidate => candidate.Path == ContextEndpointContribution.CreateThreadRoute &&
                         candidate.Method == "POST");
        var invocation = new HostEndpointInvocation(
            hostContext.InvocationId,
            route.Id,
            hostContext);
        var request = new HostEndpointRouteRequest(
            invocation,
            route.ToRouteIdentity(),
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string[]>(StringComparer.Ordinal),
            JsonSerializer.SerializeToUtf8Bytes(new { }));
        var hostEntry = new RecordingHostActionEntry();
        var handler = new ContextEndpointContribution(new ContextApiActionTerminal(null!));

        ModuleHttpEndpointResponse response = await handler.InvokeAsync(
            request,
            hostEntry,
            default);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(200));
            Assert.That(hostEntry.Contexts, Has.Exactly(1).Items);
            Assert.That(hostEntry.Contexts.Single(), Is.SameAs(hostContext));
            Assert.That(hostEntry.Keys.Single(), Is.EqualTo(ContextModule.ApiDescriptor.Key.Value));
            Assert.That(
                hostEntry.Actions.OfType<ContextApiAction>().Single().Operation,
                Is.EqualTo(ContextApiOperations.CreateThread));
        });
    }

    [Test]
    public async Task NeutralEndpointHandlersBindEveryRouteToItsOwnedAction()
    {
        await AssertEndpointRoutesAsync(
            new ContextEndpointContribution(new ContextApiActionTerminal(null!)),
            ContextEndpointContribution.EndpointRoutes,
            ContextEndpointOperations(),
            ContextModule.ApiDescriptor.Key.Value,
            action => ((ContextApiAction)action).Operation);
        await AssertEndpointRoutesAsync(
            new PermissionEndpointContribution(new PermissionApiActionTerminal(null!)),
            PermissionEndpointContribution.EndpointRoutes,
            PermissionEndpointOperations(),
            TwoTierPermissionModule.ApiDescriptor.Key.Value,
            action => ((PermissionApiAction)action).Operation);
        await AssertEndpointRoutesAsync(
            new AgentsEndpointContribution(new AgentsApiActionTerminal(null!)),
            AgentsEndpointContribution.EndpointRoutes,
            AgentsEndpointOperations(),
            AgentsModule.ApiDescriptor.Key.Value,
            action => ((AgentsApiAction)action).Operation);
    }

    [Test]
    public async Task NeutralEndpointHandlersRejectUnknownRoutesAndMalformedJsonBeforeActionEntry()
    {
        foreach (var owner in EndpointOwners())
        {
            ModuleEndpointRouteDescriptor registered = owner.Routes[0];
            var unknown = new ModuleEndpointRouteDescriptor(
                $"{registered.Id}.unknown",
                registered.Path,
                registered.Method,
                registered.Transport);
            var unknownEntry = new RecordingHostActionEntry();

            ModuleHttpEndpointResponse unknownResponse = await owner.Handler.InvokeAsync(
                CreateEndpointRequest(unknown, []),
                unknownEntry,
                default);

            var malformedEntry = new RecordingHostActionEntry();
            ModuleHttpEndpointResponse malformedResponse = await owner.Handler.InvokeAsync(
                CreateEndpointRequest(registered, [0x7B]),
                malformedEntry,
                default);

            Assert.Multiple(() =>
            {
                Assert.That(unknownResponse.StatusCode, Is.EqualTo(404));
                Assert.That(ReadErrorCode(unknownResponse), Is.EqualTo("endpoint_route_not_found"));
                Assert.That(unknownEntry.Actions, Is.Empty);
                Assert.That(malformedResponse.StatusCode, Is.EqualTo(400));
                Assert.That(ReadErrorCode(malformedResponse), Is.EqualTo("endpoint_invalid_json"));
                Assert.That(malformedEntry.Actions, Is.Empty);
            });
        }
    }

    [Test]
    public void NeutralEndpointHandlersPropagateCancellationBeforeActionEntry()
    {
        foreach (var owner in EndpointOwners())
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var hostEntry = new RecordingHostActionEntry();

            Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await owner.Handler.InvokeAsync(
                    CreateEndpointRequest(owner.Routes[0], []),
                    hostEntry,
                    cancellation.Token));

            Assert.That(hostEntry.Actions, Is.Empty);
        }
    }

    [Test]
    public async Task NeutralEndpointHandlersRedactInternalActionFailures()
    {
        const string secret = "internal-storage-path-c:\\private\\data";

        foreach (var owner in EndpointOwners())
        {
            var hostEntry = new RecordingHostActionEntry
            {
                ExceptionToThrow = new InvalidOperationException(secret),
            };

            ModuleHttpEndpointResponse response = await owner.Handler.InvokeAsync(
                CreateEndpointRequest(owner.Routes[0], []),
                hostEntry,
                default);

            using JsonDocument body = JsonDocument.Parse(response.Body);
            Assert.Multiple(() =>
            {
                Assert.That(response.StatusCode, Is.EqualTo(404));
                Assert.That(body.RootElement.GetProperty("error").GetString(),
                    Is.EqualTo("endpoint_resource_not_found"));
                Assert.That(body.RootElement.GetProperty("message").GetString(),
                    Does.Not.Contain(secret));
                Assert.That(hostEntry.Actions, Has.Exactly(1).Items);
            });
        }
    }

    [Test]
    public void ApplicationGatewaysDoNotRequestAHostSnapshot()
    {
        var gatewayTypes = new[]
        {
            typeof(ContextActionGateway),
            typeof(PermissionActionGateway),
            typeof(AgentsActionGateway),
        };

        Assert.Multiple(() =>
        {
            Assert.That(
                typeof(ModuleActionPipeline).GetConstructors().Single().GetParameters()
                    .Select(parameter => parameter.ParameterType),
                Is.EqualTo(new[] { typeof(IActionDispatcher) }));
            foreach (var gatewayType in gatewayTypes)
            {
                Assert.That(
                    gatewayType.GetConstructors().Single().GetParameters()
                        .Select(parameter => parameter.ParameterType),
                    Does.Contain(typeof(HostModuleActionEntry)));
                Assert.That(
                    gatewayType.GetConstructors().Single().GetParameters()
                        .Select(parameter => parameter.ParameterType),
                    Does.Not.Contain(typeof(ActionPipelineSnapshot)));
                Assert.That(
                    gatewayType.GetConstructors().Single().GetParameters()
                        .Select(parameter => parameter.ParameterType),
                    Does.Not.Contain(typeof(IModuleActionPipeline)));
            }
        });
    }

    [Test]
    public async Task SameLevelApprovalRequiresTheApprovedCapability()
    {
        var gateway = new InMemoryStorageGateway();
        var store = new PermissionPolicyStore(gateway);
        var policy = new TwoTierPermissionPolicy(store);
        var admin = new RequestPrincipal(Guid.NewGuid().ToString("D"), Roles: new HashSet<string>(["admin"]));
        var approver = new RequestPrincipal(Guid.NewGuid().ToString("D"));
        var subject = Guid.NewGuid().ToString("D");

        await store.SaveAsync(new PermissionPolicyRecord(
            approver.SubjectId,
            [],
            ["approve_permissions"],
            [],
            PermissionClearance.Independent,
            true,
            [],
            null,
            DateTimeOffset.UtcNow));
        await policy.GrantAsync(admin, new PermissionGrantAction(
            subject,
            "read_memory",
            "global",
            PermissionClearance.ApprovedBySameLevelUser));

        Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await policy.ApproveAsync(approver, new PermissionApproveAction(
                subject, "read_memory", "global")));

        await store.SaveAsync(new PermissionPolicyRecord(
            approver.SubjectId,
            [],
            ["approve_permissions", "read_memory"],
            [],
            PermissionClearance.Independent,
            true,
            [],
            null,
            DateTimeOffset.UtcNow));
        Assert.DoesNotThrowAsync(async () =>
            await policy.ApproveAsync(approver, new PermissionApproveAction(
                subject, "read_memory", "global")));
    }

    [Test]
    public async Task ContextScopeResolutionUsesChannelThenContextThenAgentRole()
    {
        var gateway = new InMemoryStorageGateway();
        var store = new PermissionPolicyStore(gateway);
        var policy = new TwoTierPermissionPolicy(store);
        var agentId = Guid.NewGuid();
        var caller = new RequestPrincipal(
            agentId.ToString("D"),
            Roles: new HashSet<string>(["support"]));
        await store.SaveAsync(new PermissionPolicyRecord(
            caller.SubjectId,
            ["support"],
            [ContextAccessCapabilities.ReadCrossThreadHistory],
            [],
            PermissionClearance.Independent,
            false,
            [],
            null,
            DateTimeOffset.UtcNow));

        var channel = Guid.NewGuid();
        var context = Guid.NewGuid();
        var channelDecision = await policy.EvaluateDetailedAsync(new ContextAccessRequest(
            caller,
            channel,
            agentId,
            [],
            agentId,
            [],
            false,
            context));
        var contextDecision = await policy.EvaluateDetailedAsync(new ContextAccessRequest(
            caller,
            channel,
            Guid.NewGuid(),
            [],
            agentId,
            [],
            false,
            context));
        var roleDecision = await policy.EvaluateDetailedAsync(new ContextAccessRequest(
            caller,
            channel,
            Guid.NewGuid(),
            [],
            Guid.NewGuid(),
            [],
            false,
            context));

        Assert.Multiple(() =>
        {
            Assert.That(channelDecision.Code, Does.StartWith("channel_"));
            Assert.That(contextDecision.Code, Does.StartWith("context_"));
            Assert.That(roleDecision.Code, Does.StartWith("agent-role_"));
        });
    }

    [Test]
    public async Task AssignedPermissionRoleSuppliesPersistedCapability()
    {
        var gateway = new InMemoryStorageGateway();
        var store = new PermissionPolicyStore(gateway);
        var policy = new TwoTierPermissionPolicy(store);
        var subject = Guid.NewGuid().ToString("D");
        await store.SaveRoleAsync(new PermissionRoleRecord(
            "support",
            "Support",
            null,
            ["read_memory"],
            PermissionClearance.ApprovedBySameLevelUser,
            [subject],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));

        var decision = await policy.EvaluateCapabilityAsync(
            new RequestPrincipal(subject),
            new PermissionEvaluateAction(subject, "read_memory", "global", false));

        Assert.That(decision.Allowed, Is.True);
        Assert.That(decision.Clearance, Is.EqualTo(PermissionClearance.ApprovedBySameLevelUser));
    }

    [Test]
    public void PermissionClearancePreservesFormerNumericIdentities()
    {
        var expected = new[]
        {
            (Value: 0, Clearance: PermissionClearance.Unset),
            (Value: 1, Clearance: PermissionClearance.ApprovedBySameLevelUser),
            (Value: 2, Clearance: PermissionClearance.ApprovedByWhitelistedUser),
            (Value: 3, Clearance: PermissionClearance.ApprovedByPermittedAgent),
            (Value: 4, Clearance: PermissionClearance.ApprovedByWhitelistedAgent),
            (Value: 5, Clearance: PermissionClearance.Independent),
            (Value: 6, Clearance: PermissionClearance.Restricted),
        };

        foreach (var item in expected)
        {
            var record = new PermissionPolicyRecord(
                "subject",
                [],
                [],
                [],
                item.Clearance,
                true,
                [],
                null,
                DateTimeOffset.UtcNow);
            var roundTrip = JsonSerializer.Deserialize<PermissionPolicyRecord>(
                JsonSerializer.Serialize(record));

            Assert.That((int)roundTrip!.Clearance, Is.EqualTo(item.Value));
        }

        Assert.That(PermissionDecision.Deny("denied", "denied", 1).Clearance,
            Is.EqualTo(PermissionClearance.Restricted));
    }

    [Test]
    public async Task ContextHistoryUsesOwnedStorageAndPermissionGate()
    {
        var gateway = new InMemoryStorageGateway();
        var policyStore = new PermissionPolicyStore(gateway);
        var permission = new TwoTierPermissionPolicy(policyStore);
        var agentId = Guid.NewGuid();
        var caller = new RequestPrincipal(agentId.ToString("D"), Roles: new HashSet<string>());
        var hostContext = TestHostActionContext.Create(caller, HostActionEntryIngress.CrossModule);
        await policyStore.SaveAsync(new PermissionPolicyRecord(
            caller.SubjectId,
            [], ["read_cross_thread_history", ContextAccessCapabilities.CreateThread], [],
            PermissionClearance.Independent,
            RequireSourceOptIn: true,
            [], null, DateTimeOffset.UtcNow));

        var store = new ContextStore(gateway, PolicyEntry(permission));
        var current = new ContextChannelRecord(
            Guid.NewGuid(), "Current", agentId, null, [], [], false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var source = new ContextChannelRecord(
            Guid.NewGuid(), "Source", agentId, null, [], [], false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await store.SaveChannelAsync(current);
        await store.SaveChannelAsync(source);
        var thread = await store.CreateThreadAsync(
            caller,
            source.Id,
            "Source thread",
            hostContext: hostContext);
        await store.AppendMessageAsync(new ContextMessageRecord(
            Guid.NewGuid(), thread.Id, source.Id, "user", "retained history", "tester",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var policyHost = new PolicyHostActionEntry(permission);
        var contextGateway = new DelegatingContextActionGateway(
            new ContextApiActionExecutor(store, new HostPermissionActionEntry(policyHost)),
            policyHost);
        var handler = new ContextToolHandler(contextGateway);
        using var arguments = JsonDocument.Parse($$"""{"channelId":"{{current.Id:D}}"}""");
        var result = await handler.InvokeAsync(new ToolInvocation(
            Guid.NewGuid(), null, "call", ContextModule.ListThreadsTool,
            arguments.RootElement, TestHostActionContext.Create(caller)), default);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.Content, Does.Contain(thread.Id.ToString("D")));

        using var readArguments = JsonDocument.Parse($$"""{"channelId":"{{current.Id:D}}","threadId":"{{thread.Id:D}}","maxMessages":10}""");
        var read = await handler.InvokeAsync(new ToolInvocation(
            Guid.NewGuid(), null, "call", ContextModule.ReadHistoryTool,
            readArguments.RootElement, TestHostActionContext.Create(caller)), default);
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
            caller.SubjectId, [], ["read_cross_thread_history", ContextAccessCapabilities.CreateThread], [],
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
        var hostContext = TestHostActionContext.Create(caller, HostActionEntryIngress.CrossModule);
        await policyStore.SaveAsync(new PermissionPolicyRecord(
            caller.SubjectId, [], ["read_cross_thread_history", ContextAccessCapabilities.CreateThread], [],
            PermissionClearance.Independent, true, [], null, DateTimeOffset.UtcNow));

        var store = new ContextStore(gateway, PolicyEntry(permission));
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
        var thread = await store.CreateThreadAsync(
            caller,
            source.Id,
            "Assigned thread",
            context.Id,
            hostContext: hostContext);

        var visible = await store.ListAccessibleThreadsAsync(
            caller,
            current.Id,
            hostContext: hostContext);

        Assert.That(visible.Select(item => item.ThreadId), Does.Contain(thread.Id));
    }

    [Test]
    public async Task ActionExecutorsUseOwnedModuleOperations()
    {
        var gateway = new InMemoryStorageGateway();
        var permissionStore = new PermissionPolicyStore(gateway);
        var permission = new TwoTierPermissionPolicy(permissionStore);
        var admin = new RequestPrincipal(Guid.NewGuid().ToString("D"), Roles: new HashSet<string>(["admin"]));
        var hostContext = TestHostActionContext.Create(admin, HostActionEntryIngress.CrossModule);
        var permissionExecutor = new PermissionActionExecutor(permission);
        Assert.That(await permissionExecutor.GrantAsync(admin,
            new PermissionGrantAction("subject", "read_memory", "global", PermissionClearance.Independent)), Is.True);
        Assert.That((await permissionExecutor.EvaluateAsync(
            new RequestPrincipal("subject"),
            new PermissionEvaluateAction("subject", "read_memory", "global", false))).Allowed, Is.True);

        var agents = new AgentsActionExecutor(new AgentsCatalog(gateway, PolicyEntry(permission)));
        var agent = await agents.CreateAsync(admin,
            new AgentsCreateAction("Executor Agent", Guid.NewGuid(), "provider", "model", null),
            hostContext: hostContext);
        var skill = await agents.SaveSkillAsync(admin,
            new AgentsSaveSkillAction(new SkillRecord(
                Guid.NewGuid(), "Executor Skill", null, "use the skill", [agent.Id],
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)),
            hostContext: hostContext);
        Assert.That(await agents.AccessSkillAsync(
                admin,
                new AgentsAccessSkillAction(skill.Id),
                hostContext: hostContext),
            Does.Contain("use the skill"));

        var contextStore = new ContextStore(gateway, PolicyEntry(permission));
        var channel = new ContextChannelRecord(
            Guid.NewGuid(), "Executor Channel", agent.Id, null, [], [], false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await contextStore.SaveChannelAsync(channel);
        var contextExecutor = new ContextActionExecutor(contextStore);
        var thread = await contextExecutor.CreateThreadAsync(admin,
            new ContextCreateThreadAction(channel.Id, "Executor Thread"),
            hostContext: hostContext);
        Assert.That(await contextExecutor.CommitExchangeAsync(admin,
            new ContextCommitExchangeAction(thread.Id, "question", "answer"),
            hostContext: hostContext), Is.True);
        Assert.That((await contextStore.ReadAllMessagesAsync(thread.Id)).Select(item => item.Content),
            Is.EquivalentTo(new[] { "question", "answer" }));
    }

    [Test]
    public async Task SameLevelGrantRequiresIndependentApproval()
    {
        var gateway = new InMemoryStorageGateway();
        var store = new PermissionPolicyStore(gateway);
        var policy = new TwoTierPermissionPolicy(store);
        var admin = new RequestPrincipal(Guid.NewGuid().ToString("D"), Roles: new HashSet<string>(["admin"]));
        var subject = new RequestPrincipal(Guid.NewGuid().ToString("D"));
        var approver = new RequestPrincipal(Guid.NewGuid().ToString("D"));
        await store.SaveAsync(new PermissionPolicyRecord(
            approver.SubjectId,
            [],
            ["manage_permissions", "approve_permissions", "read_memory"],
            [],
            PermissionClearance.ApprovedBySameLevelUser,
            true, [], null, DateTimeOffset.UtcNow));

        await policy.GrantAsync(admin, new PermissionGrantAction(
            subject.SubjectId, "read_memory", "global", PermissionClearance.ApprovedBySameLevelUser));
        var before = await policy.EvaluateCapabilityAsync(subject,
            new PermissionEvaluateAction(subject.SubjectId, "read_memory", "global", false));
        Assert.That(before.Allowed, Is.False);

        await policy.ApproveAsync(approver, new PermissionApproveAction(
            subject.SubjectId, "read_memory", "global"));
        var after = await policy.EvaluateCapabilityAsync(subject,
            new PermissionEvaluateAction(subject.SubjectId, "read_memory", "global", false));
        Assert.That(after.Allowed, Is.True);
    }

    [Test]
    public async Task AllFormerApprovalRoutesRequireTheirDeclaredAuthority()
    {
        var gateway = new InMemoryStorageGateway();
        var store = new PermissionPolicyStore(gateway);
        var policy = new TwoTierPermissionPolicy(store);
        var admin = new RequestPrincipal(Guid.NewGuid().ToString("D"), Roles: new HashSet<string>(["admin"]));
        var approver = new RequestPrincipal(Guid.NewGuid().ToString("D"));

        await store.SaveAsync(new PermissionPolicyRecord(
            approver.SubjectId,
            [],
            ["approve_permissions", "read_memory", "read_cross_thread_history"],
            [],
            PermissionClearance.Independent,
            true,
            [],
            null,
            DateTimeOffset.UtcNow));

        var clearances = new[]
        {
            PermissionClearance.ApprovedBySameLevelUser,
            PermissionClearance.ApprovedByWhitelistedUser,
            PermissionClearance.ApprovedByPermittedAgent,
            PermissionClearance.ApprovedByWhitelistedAgent,
        };

        foreach (var clearance in clearances)
        {
            var subject = Guid.NewGuid().ToString("D");
            var scope = $"channel:{Guid.NewGuid():N}";
            var targetPolicy = new PermissionPolicyRecord(
                subject,
                [],
                [],
                [],
                PermissionClearance.ApprovedBySameLevelUser,
                true,
                [],
                null,
                DateTimeOffset.UtcNow);
            targetPolicy = clearance switch
            {
                PermissionClearance.ApprovedByWhitelistedUser => targetPolicy with
                {
                    WhitelistedUserIds = [approver.SubjectId],
                },
                PermissionClearance.ApprovedByPermittedAgent => targetPolicy with
                {
                    PermittedAgentIds = [approver.SubjectId],
                },
                PermissionClearance.ApprovedByWhitelistedAgent => targetPolicy with
                {
                    WhitelistedAgentIds = [approver.SubjectId],
                },
                _ => targetPolicy,
            };
            await store.SaveAsync(targetPolicy);
            await policy.GrantAsync(admin, new PermissionGrantAction(
                subject,
                "read_memory",
                scope,
                clearance));

            await policy.ApproveAsync(approver, new PermissionApproveAction(subject, "read_memory", scope));

            var decision = await policy.EvaluateCapabilityAsync(
                new RequestPrincipal(subject),
                new PermissionEvaluateAction(subject, "read_memory", scope, false));
            Assert.That(decision.Allowed, Is.True, clearance.ToString());
        }
    }

    [Test]
    public async Task ScopedGrantDoesNotChangeGlobalClearanceOrOptInAfterRevoke()
    {
        var gateway = new InMemoryStorageGateway();
        var store = new PermissionPolicyStore(gateway);
        var policy = new TwoTierPermissionPolicy(store);
        var admin = new RequestPrincipal(Guid.NewGuid().ToString("D"), Roles: new HashSet<string>(["admin"]));
        var approver = new RequestPrincipal(Guid.NewGuid().ToString("D"));
        var subjectAgentId = Guid.NewGuid();
        var subject = new RequestPrincipal(subjectAgentId.ToString("D"));
        var firstChannel = Guid.NewGuid();
        var secondChannel = Guid.NewGuid();
        var firstScope = $"channel:{firstChannel:N}";
        var secondScope = $"channel:{secondChannel:N}";

        await store.SaveAsync(new PermissionPolicyRecord(
            subject.SubjectId,
            [],
            [],
            [],
            PermissionClearance.ApprovedBySameLevelUser,
            true,
            [],
            null,
            DateTimeOffset.UtcNow));
        await store.SaveAsync(new PermissionPolicyRecord(
            approver.SubjectId,
            [],
            ["approve_permissions", "read_memory", "read_cross_thread_history"],
            [],
            PermissionClearance.Independent,
            true,
            [],
            null,
            DateTimeOffset.UtcNow));
        await policy.GrantAsync(admin, new PermissionGrantAction(
            subject.SubjectId,
            ContextAccessCapabilities.ReadCrossThreadHistory,
            firstScope,
            PermissionClearance.Independent,
            RequireSourceOptIn: false));
        await policy.GrantAsync(admin, new PermissionGrantAction(
            subject.SubjectId,
            "CanReadCrossThreadHistory",
            secondScope,
            PermissionClearance.ApprovedBySameLevelUser,
            RequireSourceOptIn: true));
        await policy.ApproveAsync(approver, new PermissionApproveAction(
            subject.SubjectId,
            "CanReadCrossThreadHistory",
            secondScope));

        var first = await policy.EvaluateDetailedAsync(new ContextAccessRequest(
            subject,
            firstChannel,
            subjectAgentId,
            [],
            null,
            [],
            SourceChannelOptedIn: false,
            Capability: ContextAccessCapabilities.ReadCrossThreadHistory));
        var second = await policy.EvaluateDetailedAsync(new ContextAccessRequest(
            subject,
            secondChannel,
            subjectAgentId,
            [],
            null,
            [],
            SourceChannelOptedIn: false,
            Capability: "CanReadCrossThreadHistory"));

        Assert.That(first.Allowed, Is.True);
        Assert.That(second.Allowed, Is.False);
        Assert.That(second.Code, Is.EqualTo("source_opt_in_required"));

        var reloadedStore = new PermissionPolicyStore(gateway);
        var persistedPolicy = await reloadedStore.GetAsync(subject.SubjectId);
        var persistedGrant = await reloadedStore.GetGrantAsync(
            subject.SubjectId,
            ContextAccessCapabilities.ReadCrossThreadHistory,
            firstScope);
        Assert.Multiple(() =>
        {
            Assert.That(persistedPolicy!.Clearance, Is.EqualTo(PermissionClearance.ApprovedBySameLevelUser));
            Assert.That(persistedPolicy.RequireSourceOptIn, Is.True);
            Assert.That(persistedPolicy.Capabilities, Is.Empty);
            Assert.That(persistedGrant!.RequireSourceOptIn, Is.False);
        });

        await policy.RevokeAsync(admin, new PermissionRevokeAction(
            subject.SubjectId,
            ContextAccessCapabilities.ReadCrossThreadHistory,
            firstScope));

        var afterRevoke = await policy.EvaluateDetailedAsync(new ContextAccessRequest(
            subject,
            firstChannel,
            subjectAgentId,
            [],
            null,
            [],
            SourceChannelOptedIn: false,
            Capability: ContextAccessCapabilities.ReadCrossThreadHistory));
        var secondAfterRevoke = await policy.EvaluateDetailedAsync(new ContextAccessRequest(
            subject,
            secondChannel,
            subjectAgentId,
            [],
            null,
            [],
            SourceChannelOptedIn: false,
            Capability: "CanReadCrossThreadHistory"));

        Assert.Multiple(() =>
        {
            Assert.That(afterRevoke.Allowed, Is.False);
            Assert.That(afterRevoke.Code, Is.EqualTo("capability_denied"));
            Assert.That(afterRevoke.Clearance, Is.EqualTo(PermissionClearance.ApprovedBySameLevelUser));
            Assert.That(secondAfterRevoke.Code, Is.EqualTo("source_opt_in_required"));
        });
    }

    [Test]
    public async Task AgentsOwnProfileSkillAndMemoryPersistence()
    {
        var gateway = new InMemoryStorageGateway();
        var permissionStore = new PermissionPolicyStore(gateway);
        var permission = new TwoTierPermissionPolicy(permissionStore);
        var catalog = new AgentsCatalog(gateway, PolicyEntry(permission));
        var admin = new RequestPrincipal("admin", Roles: new HashSet<string>(["admin"]));
        var adminContext = TestHostActionContext.Create(admin, HostActionEntryIngress.CrossModule);
        var agent = await catalog.CreateAgentAsync(admin, new(
            "Test Agent", Guid.NewGuid(), "provider", "model", "prompt"),
            hostContext: adminContext);
        await catalog.SaveSkillAsync(admin, new SkillRecord(
            Guid.NewGuid(), "Skill", "Description", "Instruction", [agent.Id],
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            hostContext: adminContext);
        var owner = new RequestPrincipal(agent.Id.ToString("D"));
        var ownerContext = TestHostActionContext.Create(owner, HostActionEntryIngress.CrossModule);
        await permissionStore.SaveAsync(new PermissionPolicyRecord(
            owner.SubjectId, [], ["write_memory", "read_memory"], [],
            PermissionClearance.Independent, false, [], null, DateTimeOffset.UtcNow));
        var memory = await catalog.WriteMemoryAsync(owner, new(
            agent.Id, "preference", "Use concise answers", ["profile"]),
            hostContext: ownerContext);

        Assert.That((await catalog.ListAgentsAsync()).Single().Name, Is.EqualTo("Test Agent"));
        Assert.That((await catalog.ListSkillsAsync()).Single().SkillText, Is.EqualTo("Instruction"));
        Assert.That((await catalog.SearchMemoryAsync(
            owner,
            agent.Id,
            "concise",
            hostContext: ownerContext)).Single().Id, Is.EqualTo(memory.Id));
        Assert.That((await new AgentChatProfileResolver(catalog).ResolveAsync(
            new ChatTurnContext(Guid.NewGuid(), new ChatTurnInput("hi", Caller: owner),
                new ConversationSelection(Guid.NewGuid())), default)).ModelName, Is.EqualTo("model"));
    }

    [Test]
    public async Task AgentsOwnAgentJobStateAndProjectCanonicalCompletion()
    {
        var gateway = new InMemoryStorageGateway();
        var catalog = new AgentsCatalog(gateway, AllowAllEntry());
        var executor = new AgentsJobActionExecutor(catalog);
        var caller = new RequestPrincipal("caller", IsAuthenticated: true);
        var hostContext = TestHostActionContext.Create(caller, HostActionEntryIngress.CrossModule);
        var agentId = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var contextId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var job = new AgentJob(
            Guid.NewGuid(), agentId, "caller", "agent.respond", "conversation",
            "{\"script\":true}", "{\"prompt\":\"hello\"}", "D:\\work",
            "queued", "Independent", 11, 7, ["approver-1", "approver-2"],
            channelId, contextId, "permission-1", createdAt, createdAt, null, null,
            null, null, null);

        var recorded = await executor.RecordAsync(new(job, caller, hostContext));
        var canonicalJobId = Guid.NewGuid();
        var attached = await executor.AttachCanonicalJobAsync(
            new(recorded.Id, canonicalJobId, caller, hostContext));
        var completedAt = DateTimeOffset.UtcNow;
        var completed = await executor.CompleteAsync(new(
            recorded.Id,
            canonicalJobId,
            "completed",
            "{\"answer\":\"ok\"}",
            null,
            19,
            13,
            completedAt,
            caller,
            hostContext));
        var restartedCatalog = new AgentsCatalog(gateway, AllowAllEntry());
        var restored = await restartedCatalog.GetAgentJobAsync(recorded.Id);
        var listed = await restartedCatalog.ListAgentJobsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(attached.CanonicalJobId, Is.EqualTo(canonicalJobId));
            Assert.That(completed.Status, Is.EqualTo("completed"));
            Assert.That(restored, Is.Not.Null);
            Assert.That(listed.Select(item => item.Id), Does.Contain(recorded.Id));
            Assert.That(restored!.AgentId, Is.EqualTo(agentId));
            Assert.That(restored.CallerIdentity, Is.EqualTo("caller"));
            Assert.That(restored.ActionIdentity, Is.EqualTo("agent.respond"));
            Assert.That(restored.Resource, Is.EqualTo("conversation"));
            Assert.That(restored.ScriptJson, Is.EqualTo("{\"script\":true}"));
            Assert.That(restored.PayloadJson, Is.EqualTo("{\"prompt\":\"hello\"}"));
            Assert.That(restored.WorkingDirectory, Is.EqualTo("D:\\work"));
            Assert.That(restored.Clearance, Is.EqualTo("Independent"));
            Assert.That(restored.InputTokens, Is.EqualTo(19));
            Assert.That(restored.OutputTokens, Is.EqualTo(13));
            Assert.That(restored.ApprovalIdentities, Is.EquivalentTo(new[] { "approver-1", "approver-2" }));
            Assert.That(restored.ChannelId, Is.EqualTo(channelId));
            Assert.That(restored.ContextId, Is.EqualTo(contextId));
            Assert.That(restored.PermissionIdentity, Is.EqualTo("permission-1"));
            Assert.That(restored.CanonicalJobId, Is.EqualTo(canonicalJobId));
            Assert.That(restored.ResultJson, Is.EqualTo("{\"answer\":\"ok\"}"));
            Assert.That(restored.Error, Is.Null);
            Assert.That(restored.CompletedAt, Is.EqualTo(completedAt));
            Assert.That(restored.ResultAuthority, Is.EqualTo(AgentJob.CanonicalResultAuthority));
        });
    }

    [Test]
    public async Task AgentsRejectCanonicalIdentityMismatchAndIncompleteDefinitions()
    {
        var catalog = new AgentsCatalog(new InMemoryStorageGateway(), AllowAllEntry());
        var caller = new RequestPrincipal("caller", IsAuthenticated: true);
        var hostContext = TestHostActionContext.Create(caller, HostActionEntryIngress.CrossModule);
        var job = new AgentJob(
            Guid.NewGuid(), Guid.NewGuid(), "caller", "agent.respond", "conversation",
            "{}", "{}", "D:\\work", "queued", "Unset", 0, 0, [], null, null,
            "permission-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null, null, null);
        var recorded = await catalog.RecordAgentJobAsync(caller, job, hostContext: hostContext);
        var canonicalJobId = Guid.NewGuid();
        await catalog.AttachCanonicalJobAsync(
            caller,
            recorded.Id,
            canonicalJobId,
            hostContext: hostContext);

        Assert.Multiple(() =>
        {
            Assert.ThrowsAsync<InvalidOperationException>(() =>
                catalog.AttachCanonicalJobAsync(
                    caller,
                    recorded.Id,
                    Guid.NewGuid(),
                    hostContext: hostContext));
            Assert.ThrowsAsync<InvalidOperationException>(() =>
                catalog.ProjectCanonicalCompletionAsync(
                    caller, recorded.Id, Guid.NewGuid(), "failed", null, "unknown", 0, 0,
                    DateTimeOffset.UtcNow,
                    hostContext: hostContext));
            Assert.ThrowsAsync<ArgumentException>(() =>
                catalog.RecordAgentJobAsync(
                    caller,
                    job with { ActionIdentity = " " },
                    hostContext: hostContext));
        });
    }

    [Test]
    public void AgentJobImportConverterPreservesQueuedAndRecoveryBoundaries()
    {
        var now = DateTimeOffset.UtcNow;
        var canonicalJobId = Guid.NewGuid();
        var queued = NeutralRecord(now, "queued");
        var active = NeutralRecord(
            now,
            "active",
            canonicalJobId: canonicalJobId,
            startedAt: now.AddMinutes(-1),
            resultAuthority: AgentJob.CanonicalResultAuthority);
        var terminal = NeutralRecord(
            now,
            "completed",
            canonicalJobId: canonicalJobId,
            completedAt: now,
            resultJson: "{\"ok\":true}",
            resultAuthority: AgentJob.CanonicalResultAuthority);
        var snapshot = new CanonicalJobsImportSnapshot(
            "snapshot-1",
            now,
            [queued, active, terminal],
            [new("legacy.agent", AgentJobHandlerKeys.Canonical, AgentJobPayloadCodecs.JsonV1)]);

        var jobs = AgentsJobImportConverter.Convert(snapshot);
        var single = AgentsJobImportConverter.Convert(
            new JobActionInput<NeutralAgentJobRecord>(queued),
            new("legacy.agent", AgentJobHandlerKeys.Canonical, AgentJobPayloadCodecs.JsonV1));

        Assert.Multiple(() =>
        {
            Assert.That(jobs[0].RecoveryMode, Is.EqualTo(AgentJobRecoveryModes.CanonicalHandler));
            Assert.That(jobs[0].CanonicalJobId, Is.Null);
            Assert.That(jobs[0].PayloadJson, Is.EqualTo("{\"prompt\":\"hello\"}"));
            Assert.That(jobs[1].RecoveryMode, Is.EqualTo(AgentJobRecoveryModes.CanonicalRecovery));
            Assert.That(jobs[1].CanonicalJobId, Is.EqualTo(canonicalJobId));
            Assert.That(jobs[2].RecoveryMode, Is.EqualTo(AgentJobRecoveryModes.Terminal));
            Assert.That(jobs[2].ResultJson, Is.EqualTo("{\"ok\":true}"));
            Assert.That(jobs.All(job => job.HandlerKey == AgentJobHandlerKeys.Canonical), Is.True);
            Assert.That(jobs.All(job => job.PayloadCodec == AgentJobPayloadCodecs.JsonV1), Is.True);
            Assert.That(single.Id, Is.EqualTo(queued.SourceId));
            Assert.That(single.RecoveryMode, Is.EqualTo(AgentJobRecoveryModes.CanonicalHandler));
        });
    }

    [Test]
    public async Task AgentJobImportActionPersistsOnlyMappedRecords()
    {
        var now = DateTimeOffset.UtcNow;
        var gateway = new InMemoryStorageGateway();
        var catalog = new AgentsCatalog(gateway, AllowAllEntry());
        var executor = new AgentsJobActionExecutor(catalog);
        var source = NeutralRecord(now, "paused");
        var snapshot = new CanonicalJobsImportSnapshot(
            "snapshot-2",
            now,
            [source],
            [new("legacy.agent", AgentJobHandlerKeys.Canonical, AgentJobPayloadCodecs.JsonV1)]);

        var importer = new RequestPrincipal("importer", IsAuthenticated: true);
        var imported = await executor.ImportAsync(new(
            snapshot,
            importer,
            TestHostActionContext.Create(importer, HostActionEntryIngress.CrossModule)));
        var persisted = await new AgentsCatalog(gateway, AllowAllEntry())
            .GetAgentJobAsync(source.SourceId);

        Assert.Multiple(() =>
        {
            Assert.That(imported, Has.Count.EqualTo(1));
            Assert.That(imported[0].RecoveryMode, Is.EqualTo(AgentJobRecoveryModes.CanonicalHandler));
            Assert.That(persisted, Is.Not.Null);
            Assert.That(persisted!.Id, Is.EqualTo(source.SourceId));
            Assert.That(persisted.Status, Is.EqualTo("paused"));
        });
    }

    [Test]
    public async Task AgentJobImportAcceptsExactReplayAndPersistsCompletionMarker()
    {
        var now = DateTimeOffset.UtcNow;
        var gateway = new InMemoryStorageGateway();
        var catalog = new AgentsCatalog(gateway, AllowAllEntry());
        var source = NeutralRecord(now, "queued");
        var snapshot = new CanonicalJobsImportSnapshot(
            "snapshot-replay",
            now,
            [source],
            [new("legacy.agent", AgentJobHandlerKeys.Canonical, AgentJobPayloadCodecs.JsonV1)]);
        var caller = new RequestPrincipal("importer", IsAuthenticated: true);
        var hostContext = TestHostActionContext.Create(caller, HostActionEntryIngress.CrossModule);

        var first = await catalog.ImportAgentJobsAsync(caller, snapshot, hostContext: hostContext);
        var second = await catalog.ImportAgentJobsAsync(caller, snapshot, hostContext: hostContext);
        var state = await catalog.GetAgentJobImportStateAsync(snapshot.SnapshotId);

        Assert.Multiple(() =>
        {
            Assert.That(first, Has.Count.EqualTo(1));
            Assert.That(second, Has.Count.EqualTo(1));
            Assert.That(
                AgentsJobImportIntegrity.AreEquivalent(first[0], second[0]),
                Is.True);
            Assert.That(state, Is.Not.Null);
            Assert.That(state!.Completed, Is.True);
            Assert.That(state.ImportedRecordCount, Is.EqualTo(1));
            Assert.That(state.ExpectedRecordCount, Is.EqualTo(1));
            Assert.That(state.OrderedSourceIds, Is.EqualTo(new[] { source.SourceId }));
            Assert.That(state.SourceHashes, Is.EqualTo(snapshot.SourceHashes));
            Assert.That(state.AggregateHash, Is.EqualTo(snapshot.AggregateHash));
        });
    }

    [Test]
    public async Task ConcurrentExactAgentJobImportsConvergeAcrossCatalogs()
    {
        var now = DateTimeOffset.UtcNow;
        var gateway = new InMemoryStorageGateway();
        var markerCreatesEntered = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMarkerCreates = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var markerUpsertCount = 0;
        gateway.BeforeOperationAsync = async (_, storage, operation) =>
        {
            if (storage != AgentsCatalog.AgentJobImportsStorage
                || operation != ModuleStorageOperations.Upsert)
                return;
            var count = Interlocked.Increment(ref markerUpsertCount);
            if (count > 2)
                return;
            if (count == 2)
                markerCreatesEntered.TrySetResult(null);
            await releaseMarkerCreates.Task.WaitAsync(TimeSpan.FromSeconds(10));
        };

        var snapshot = new CanonicalJobsImportSnapshot(
            "snapshot-concurrent-replay",
            now,
            [NeutralRecord(now, "queued")],
            [new("legacy.agent", AgentJobHandlerKeys.Canonical, AgentJobPayloadCodecs.JsonV1)]);
        var caller = new RequestPrincipal("importer", IsAuthenticated: true);
        var hostContext = TestHostActionContext.Create(caller, HostActionEntryIngress.CrossModule);
        var catalogA = new AgentsCatalog(gateway, AllowAllEntry());
        var catalogB = new AgentsCatalog(gateway, AllowAllEntry());
        var imports = new[]
        {
            catalogA.ImportAgentJobsAsync(caller, snapshot, hostContext: hostContext),
            catalogB.ImportAgentJobsAsync(caller, snapshot, hostContext: hostContext),
        };

        try
        {
            await markerCreatesEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseMarkerCreates.TrySetResult(null);
        }

        var results = await Task.WhenAll(imports).WaitAsync(TimeSpan.FromSeconds(10));
        var state = await catalogA.GetAgentJobImportStateAsync(snapshot.SnapshotId);
        var jobs = await catalogA.ListAgentJobsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Length.EqualTo(2));
            Assert.That(results.All(result => result.Count == 1), Is.True);
            Assert.That(state, Is.Not.Null);
            Assert.That(state!.Completed, Is.True);
            Assert.That(state.MappingHash, Is.EqualTo(snapshot.MappingHash));
            Assert.That(jobs, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task ConcurrentDifferentAgentJobImportsFailClosedWithoutExtraRecords()
    {
        var now = DateTimeOffset.UtcNow;
        var gateway = new InMemoryStorageGateway();
        var markerCreatesEntered = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMarkerCreates = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var markerUpsertCount = 0;
        gateway.BeforeOperationAsync = async (_, storage, operation) =>
        {
            if (storage != AgentsCatalog.AgentJobImportsStorage
                || operation != ModuleStorageOperations.Upsert)
                return;
            var count = Interlocked.Increment(ref markerUpsertCount);
            if (count > 2)
                return;
            if (count == 2)
                markerCreatesEntered.TrySetResult(null);
            await releaseMarkerCreates.Task.WaitAsync(TimeSpan.FromSeconds(10));
        };

        var mappings = new[]
        {
            new AgentJobActionMapping("legacy.agent", AgentJobHandlerKeys.Canonical, AgentJobPayloadCodecs.JsonV1),
        };
        var snapshotA = new CanonicalJobsImportSnapshot(
            "snapshot-concurrent-conflict", now, [NeutralRecord(now, "queued")], mappings);
        var snapshotB = new CanonicalJobsImportSnapshot(
            "snapshot-concurrent-conflict", now, [NeutralRecord(now, "queued")], mappings);
        var caller = new RequestPrincipal("importer", IsAuthenticated: true);
        var hostContext = TestHostActionContext.Create(caller, HostActionEntryIngress.CrossModule);
        var catalogA = new AgentsCatalog(gateway, AllowAllEntry());
        var catalogB = new AgentsCatalog(gateway, AllowAllEntry());
        var imports = new[]
        {
            CaptureAsync(() => catalogA.ImportAgentJobsAsync(caller, snapshotA, hostContext: hostContext)),
            CaptureAsync(() => catalogB.ImportAgentJobsAsync(caller, snapshotB, hostContext: hostContext)),
        };

        try
        {
            await markerCreatesEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseMarkerCreates.TrySetResult(null);
        }

        var outcomes = await Task.WhenAll(imports).WaitAsync(TimeSpan.FromSeconds(10));
        var state = await catalogA.GetAgentJobImportStateAsync(snapshotA.SnapshotId);
        var jobs = await catalogA.ListAgentJobsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(outcomes.Count(outcome => outcome is null), Is.EqualTo(1));
            Assert.That(outcomes.Count(outcome => outcome is AgentJobImportException), Is.EqualTo(1));
            Assert.That(state, Is.Not.Null);
            Assert.That(state!.Completed, Is.True);
            Assert.That(jobs, Has.Count.EqualTo(1));
            Assert.That(
                jobs[0].Id == snapshotA.Records[0].SourceId
                    || jobs[0].Id == snapshotB.Records[0].SourceId,
                Is.True);
        });
    }

    [Test]
    public async Task AgentJobImportRejectsChangedMissingExtraAndReorderedActionMappings()
    {
        var now = DateTimeOffset.UtcNow;
        var gateway = new InMemoryStorageGateway();
        var catalog = new AgentsCatalog(gateway, AllowAllEntry());
        var source = NeutralRecord(now, "queued");
        var primary = new AgentJobActionMapping(
            "legacy.agent", AgentJobHandlerKeys.Canonical, AgentJobPayloadCodecs.JsonV1);
        var secondary = new AgentJobActionMapping(
            "legacy.unused", AgentJobHandlerKeys.Canonical, AgentJobPayloadCodecs.JsonV1);
        var snapshot = new CanonicalJobsImportSnapshot(
            "snapshot-mapping-authority", now, [source], [primary, secondary]);
        var caller = new RequestPrincipal("importer", IsAuthenticated: true);
        var hostContext = TestHostActionContext.Create(caller, HostActionEntryIngress.CrossModule);
        await catalog.ImportAgentJobsAsync(caller, snapshot, hostContext: hostContext);

        var changed = new CanonicalJobsImportSnapshot(
            snapshot.SnapshotId,
            snapshot.CapturedAt,
            [source],
            [primary, new("legacy.changed", AgentJobHandlerKeys.Canonical, AgentJobPayloadCodecs.JsonV1)]);
        var missing = new CanonicalJobsImportSnapshot(
            snapshot.SnapshotId, snapshot.CapturedAt, [source], [primary]);
        var extra = new CanonicalJobsImportSnapshot(
            snapshot.SnapshotId,
            snapshot.CapturedAt,
            [source],
            [primary, secondary, new("legacy.extra", AgentJobHandlerKeys.Canonical, AgentJobPayloadCodecs.JsonV1)]);
        var reordered = new CanonicalJobsImportSnapshot(
            snapshot.SnapshotId, snapshot.CapturedAt, [source], [secondary, primary]);

        Assert.Multiple(() =>
        {
            Assert.ThrowsAsync<AgentJobImportException>(() =>
                catalog.ImportAgentJobsAsync(caller, changed, hostContext: hostContext));
            Assert.ThrowsAsync<AgentJobImportException>(() =>
                catalog.ImportAgentJobsAsync(caller, missing, hostContext: hostContext));
            Assert.ThrowsAsync<AgentJobImportException>(() =>
                catalog.ImportAgentJobsAsync(caller, extra, hostContext: hostContext));
            Assert.ThrowsAsync<AgentJobImportException>(() =>
                catalog.ImportAgentJobsAsync(caller, reordered, hostContext: hostContext));
        });
    }

    [Test]
    public async Task AgentJobImportRejectsChangedSameIdentityAfterCompletion()
    {
        var now = DateTimeOffset.UtcNow;
        var gateway = new InMemoryStorageGateway();
        var catalog = new AgentsCatalog(gateway, AllowAllEntry());
        var source = NeutralRecord(now, "queued");
        var snapshot = new CanonicalJobsImportSnapshot(
            "snapshot-conflict",
            now,
            [source],
            [new("legacy.agent", AgentJobHandlerKeys.Canonical, AgentJobPayloadCodecs.JsonV1)]);
        var caller = new RequestPrincipal("importer", IsAuthenticated: true);
        var hostContext = TestHostActionContext.Create(caller, HostActionEntryIngress.CrossModule);
        await catalog.ImportAgentJobsAsync(caller, snapshot, hostContext: hostContext);

        var changed = new CanonicalJobsImportSnapshot(
            snapshot.SnapshotId,
            snapshot.CapturedAt,
            [source with { PayloadJson = """{"prompt":"changed"}""" }],
            snapshot.ActionMappings);

        Assert.ThrowsAsync<AgentJobImportException>(() =>
            catalog.ImportAgentJobsAsync(caller, changed, hostContext: hostContext));
        Assert.That(
            (await catalog.GetAgentJobAsync(source.SourceId))!.PayloadJson,
            Is.EqualTo("""{"prompt":"hello"}"""));
    }

    [Test]
    public async Task AgentJobImportRejectsMissingExtraAndReorderedReplay()
    {
        var now = DateTimeOffset.UtcNow;
        var gateway = new InMemoryStorageGateway();
        var catalog = new AgentsCatalog(gateway, AllowAllEntry());
        var sources = new[] { NeutralRecord(now, "queued"), NeutralRecord(now, "paused") };
        var mappings = new[]
        {
            new AgentJobActionMapping("legacy.agent", AgentJobHandlerKeys.Canonical, AgentJobPayloadCodecs.JsonV1),
        };
        var snapshot = new CanonicalJobsImportSnapshot("snapshot-shape", now, sources, mappings);
        var caller = new RequestPrincipal("importer", IsAuthenticated: true);
        var hostContext = TestHostActionContext.Create(caller, HostActionEntryIngress.CrossModule);
        await catalog.ImportAgentJobsAsync(caller, snapshot, hostContext: hostContext);

        var missing = new CanonicalJobsImportSnapshot(
            snapshot.SnapshotId,
            snapshot.CapturedAt,
            [sources[0]],
            mappings);
        var extra = new CanonicalJobsImportSnapshot(
            snapshot.SnapshotId,
            snapshot.CapturedAt,
            [.. sources, NeutralRecord(now, "queued")],
            mappings);
        var reordered = new CanonicalJobsImportSnapshot(
            snapshot.SnapshotId,
            snapshot.CapturedAt,
            sources.Reverse().ToArray(),
            mappings);

        Assert.Multiple(() =>
        {
            Assert.ThrowsAsync<AgentJobImportException>(() =>
                catalog.ImportAgentJobsAsync(caller, missing, hostContext: hostContext));
            Assert.ThrowsAsync<AgentJobImportException>(() =>
                catalog.ImportAgentJobsAsync(caller, extra, hostContext: hostContext));
            Assert.ThrowsAsync<AgentJobImportException>(() =>
                catalog.ImportAgentJobsAsync(caller, reordered, hostContext: hostContext));
        });
    }

    [Test]
    public async Task AgentJobImportResumesAfterInterruptedWrite()
    {
        var now = DateTimeOffset.UtcNow;
        var gateway = new InMemoryStorageGateway { FailAfterUpserts = 2 };
        var catalog = new AgentsCatalog(gateway, AllowAllEntry());
        var sources = new[] { NeutralRecord(now, "queued"), NeutralRecord(now, "paused") };
        var snapshot = new CanonicalJobsImportSnapshot(
            "snapshot-interrupted",
            now,
            sources,
            [new("legacy.agent", AgentJobHandlerKeys.Canonical, AgentJobPayloadCodecs.JsonV1)]);
        var caller = new RequestPrincipal("importer", IsAuthenticated: true);
        var hostContext = TestHostActionContext.Create(caller, HostActionEntryIngress.CrossModule);

        Assert.ThrowsAsync<IOException>(() =>
            catalog.ImportAgentJobsAsync(caller, snapshot, hostContext: hostContext));
        var interrupted = await catalog.GetAgentJobImportStateAsync(snapshot.SnapshotId);
        Assert.That(interrupted, Is.Not.Null);
        Assert.That(interrupted!.Completed, Is.False);
        Assert.That(await catalog.GetAgentJobAsync(sources[0].SourceId), Is.Not.Null);
        Assert.That(await catalog.GetAgentJobAsync(sources[1].SourceId), Is.Null);

        gateway.FailAfterUpserts = null;
        var resumed = await catalog.ImportAgentJobsAsync(caller, snapshot, hostContext: hostContext);
        var completed = await catalog.GetAgentJobImportStateAsync(snapshot.SnapshotId);

        Assert.Multiple(() =>
        {
            Assert.That(resumed, Has.Count.EqualTo(2));
            Assert.That(completed, Is.Not.Null);
            Assert.That(completed!.Completed, Is.True);
            Assert.That(completed.ImportedRecordCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void AgentJobImportFailsClosedForMissingMappingPayloadAndRecoveryData()
    {
        var now = DateTimeOffset.UtcNow;
        var source = NeutralRecord(now, "queued");
        var baseSnapshot = new CanonicalJobsImportSnapshot(
            "snapshot-3",
            now,
            [source],
            [new("legacy.agent", AgentJobHandlerKeys.Canonical, AgentJobPayloadCodecs.JsonV1)]);

        Assert.Multiple(() =>
        {
            Assert.Throws<AgentJobImportException>(() => AgentsJobImportConverter.Convert(
                baseSnapshot with { ActionMappings = [] }));
            Assert.Throws<AgentJobImportException>(() => AgentsJobImportConverter.Convert(
                baseSnapshot with
                {
                    Records = [source with { PayloadJson = "not-json" }],
                }));
            Assert.Throws<AgentJobImportException>(() => AgentsJobImportConverter.Convert(
                baseSnapshot with
                {
                    Records = [source with { Status = "active" }],
                }));
            Assert.Throws<AgentJobImportException>(() => AgentsJobImportConverter.Convert(
                baseSnapshot with
                {
                    ActionMappings = [new("legacy.agent", "untrusted.handler", AgentJobPayloadCodecs.JsonV1)],
                }));
            Assert.Throws<AgentJobImportException>(() => AgentsJobImportConverter.Convert(
                baseSnapshot with
                {
                    Records = [source with { ResultAuthority = "untrusted.results" }],
                }));
            Assert.Throws<AgentJobImportException>(() => AgentsJobImportConverter.Convert(
                baseSnapshot with
                {
                    Records = [source with { ContextId = Guid.Empty }],
                }));
        });
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> operation)
    {
        try
        {
            await operation();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static NeutralAgentJobRecord NeutralRecord(
        DateTimeOffset now,
        string status,
        Guid? canonicalJobId = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? completedAt = null,
        string? resultJson = null,
        string? error = null,
        string resultAuthority = "") =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "caller",
            "legacy.agent",
            "conversation",
            "{\"script\":true}",
            "{\"prompt\":\"hello\"}",
            "D:\\work",
            status,
            "Independent",
            4,
            5,
            ["approver"],
            Guid.NewGuid(),
            Guid.NewGuid(),
            "permission-1",
            now.AddMinutes(-2),
            now,
            startedAt,
            completedAt,
            canonicalJobId,
            resultJson,
            error,
            resultAuthority);

    private static ActionContext<AgentsApiAction> CreateAgentsApiContext(
        RequestPrincipal caller,
        AgentsApiAction action,
        IHostActionEntry hostEntry,
        DateTimeOffset now) =>
        new(
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            1,
            now.AddMinutes(1),
            AgentsModule.ApiDescriptor.Key,
            AgentsModule.ModuleIdValue,
            caller,
            action,
            ExtensionFeatureSet.Empty,
            new ActionPipelineSnapshot("test", [], [], 16))
        {
            HostActionEntry = hostEntry,
        };

    [Test]
    public async Task DirectChatFailsClosedWithoutHostActionContext()
    {
        var gateway = new InMemoryStorageGateway();
        var policyStore = new PermissionPolicyStore(gateway);
        var permission = new TwoTierPermissionPolicy(policyStore);
        var agentId = Guid.NewGuid();
        var caller = new RequestPrincipal(agentId.ToString("D"));
        await policyStore.SaveAsync(new PermissionPolicyRecord(
            caller.SubjectId,
            [],
            [
                ContextAccessCapabilities.CreateThread,
                ContextAccessCapabilities.ReadHistory,
                ContextAccessCapabilities.CommitExchange,
            ],
            [],
            PermissionClearance.ApprovedBySameLevelUser,
            false,
            [],
            null,
            DateTimeOffset.UtcNow));

        var store = new ContextStore(gateway, PolicyEntry(permission));
        var resolver = new ContextConversationResolver(store);
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await resolver.ResolveAsync(
                new ChatTurnInput("hello", Caller: caller),
                default));

        var anonymous = new RequestPrincipal("", IsAuthenticated: false);
        Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await resolver.ResolveAsync(new ChatTurnInput("blocked", Caller: anonymous), default));
    }

    [Test]
    public async Task PermissionPolicyRejectsDelegationOutsideHeldAuthority()
    {
        var gateway = new InMemoryStorageGateway();
        var store = new PermissionPolicyStore(gateway);
        var policy = new TwoTierPermissionPolicy(store);
        var admin = new RequestPrincipal(
            Guid.NewGuid().ToString("D"),
            Roles: new HashSet<string>(["admin"]));
        var delegator = new RequestPrincipal(Guid.NewGuid().ToString("D"));
        var subject = Guid.NewGuid().ToString("D");
        var channelScope = $"channel:{Guid.NewGuid():N}";

        await store.SaveAsync(new PermissionPolicyRecord(
            delegator.SubjectId,
            [],
            ["manage_permissions", "read_memory"],
            [],
            PermissionClearance.Independent,
            false,
            [],
            null,
            DateTimeOffset.UtcNow));
        await policy.GrantAsync(admin, new PermissionGrantAction(
            delegator.SubjectId,
            "read_memory",
            channelScope,
            PermissionClearance.Independent));
        await store.SaveAsync(new PermissionPolicyRecord(
            delegator.SubjectId,
            [],
            ["manage_permissions"],
            [],
            PermissionClearance.Independent,
            false,
            [],
            null,
            DateTimeOffset.UtcNow));

        Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await policy.GrantAsync(delegator, new PermissionGrantAction(
                subject,
                "read_memory",
                "global",
                PermissionClearance.ApprovedBySameLevelUser)));
        Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await policy.GrantAsync(delegator, new PermissionGrantAction(
                subject,
                "write_memory",
                channelScope,
                PermissionClearance.ApprovedBySameLevelUser)));
        Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await policy.GrantAsync(delegator, new PermissionGrantAction(
                subject,
                "read_memory",
                channelScope,
                PermissionClearance.Independent)));
        Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await policy.RevokeAsync(delegator, new PermissionRevokeAction(
                subject,
                "read_memory",
                "global")));
    }

    [Test]
    public async Task ScopedIndependentGrantDoesNotBecomeGlobalCapability()
    {
        var gateway = new InMemoryStorageGateway();
        var store = new PermissionPolicyStore(gateway);
        var policy = new TwoTierPermissionPolicy(store);
        var admin = new RequestPrincipal(
            Guid.NewGuid().ToString("D"),
            Roles: new HashSet<string>(["admin"]));
        var subject = new RequestPrincipal(Guid.NewGuid().ToString("D"));
        var channelScope = $"channel:{Guid.NewGuid():N}";

        await policy.GrantAsync(admin, new PermissionGrantAction(
            subject.SubjectId,
            "read_memory",
            channelScope,
            PermissionClearance.Independent));

        var global = await policy.EvaluateCapabilityAsync(
            subject,
            new PermissionEvaluateAction(subject.SubjectId, "read_memory", "global", false));
        var scoped = await policy.EvaluateCapabilityAsync(
            subject,
            new PermissionEvaluateAction(subject.SubjectId, "read_memory", channelScope, false));

        Assert.That(global.Allowed, Is.False);
        Assert.That(scoped.Allowed, Is.True);
    }

    [Test]
    public void RetainedModulesHaveNoEntityFrameworkPersistenceSurface()
    {
        var assemblies = new[]
        {
            typeof(ContextModule).Assembly,
            typeof(TwoTierPermissionModule).Assembly,
            typeof(AgentsModule).Assembly,
        };

        Assert.Multiple(() =>
        {
            foreach (var assembly in assemblies)
            {
                Assert.That(
                    assembly.GetReferencedAssemblies().Select(name => name.Name),
                    Does.Not.Contain("Microsoft.EntityFrameworkCore"));
                Assert.That(
                    assembly.GetTypes().Any(type => type.Name.Contains("DbContext", StringComparison.Ordinal)),
                    Is.False);
            }
        });
    }

    [Test]
    public async Task ContextSteeringRecordsUseExplicitTargetsAndScopedPermissions()
    {
        var fixture = await CreateSteeringFixtureAsync();
        var hostContext = TestHostActionContext.Create(fixture.Caller, HostActionEntryIngress.CrossModule);
        var action = new ContextRecordSteeringAction(
            fixture.Channel.Id,
            null,
            "  moduledevkit  ",
            "  steering  ",
            "  Use the channel architecture rules.  ",
            "  Keep the current provider.  ",
            "  ModuleDevKit  ");
        var context = CreateSteeringContext(
            hostContext,
            ContextSteeringActionDescriptors.Record.Key,
            action,
            fixture.Host);

        var record = await fixture.Executor.RecordAsync(context);
        var listed = await fixture.Executor.ListAsync(
            CreateSteeringContext(
                hostContext,
                ContextSteeringActionDescriptors.List.Key,
                new ContextListSteeringAction(fixture.Channel.Id, MaxRecords: 10),
                fixture.Host));

        Assert.Multiple(() =>
        {
            Assert.That(record.Id, Is.EqualTo(hostContext.IdempotencyKey));
            Assert.That(record.ChannelId, Is.EqualTo(fixture.Channel.Id));
            Assert.That(record.ThreadId, Is.Null);
            Assert.That(record.Source, Is.EqualTo("moduledevkit"));
            Assert.That(record.Category, Is.EqualTo("steering"));
            Assert.That(record.Summary, Is.EqualTo("Use the channel architecture rules."));
            Assert.That(record.Details, Is.EqualTo("Keep the current provider."));
            Assert.That(record.ClientType, Is.EqualTo("ModuleDevKit"));
            Assert.That(record.Caller.SubjectId, Is.EqualTo(fixture.Caller.SubjectId));
            Assert.That(record.CreatedAt, Is.Not.EqualTo(default(DateTimeOffset)));
            Assert.That(listed.Select(item => item.Id), Is.EqualTo([record.Id]));
            Assert.That(fixture.Gateway.Count(ContextStore.ModuleId, ContextStore.SteeringStorage), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ContextSteeringThreadTargetRequiresTheSpecifiedChannel()
    {
        var fixture = await CreateSteeringFixtureAsync();
        var thread = await fixture.Store.CreateThreadAsync(
            fixture.Caller,
            fixture.Channel.Id,
            "Steering thread",
            hostContext: TestHostActionContext.Create(fixture.Caller, HostActionEntryIngress.CrossModule));
        var hostContext = TestHostActionContext.Create(fixture.Caller, HostActionEntryIngress.CrossModule);
        var record = await fixture.Executor.RecordAsync(
            CreateSteeringContext(
                hostContext,
                ContextSteeringActionDescriptors.Record.Key,
                new ContextRecordSteeringAction(
                    fixture.Channel.Id,
                    thread.Id,
                    "moduledevkit",
                    "thread",
                    "Thread steering",
                    null,
                    "ModuleDevKit"),
                fixture.Host));
        var listed = await fixture.Executor.ListAsync(
            CreateSteeringContext(
                TestHostActionContext.Create(fixture.Caller, HostActionEntryIngress.CrossModule),
                ContextSteeringActionDescriptors.List.Key,
                new ContextListSteeringAction(fixture.Channel.Id, thread.Id, 10),
                fixture.Host));

        var otherChannel = new ContextChannelRecord(
            Guid.NewGuid(),
            "Other channel",
            Guid.Parse(fixture.Caller.SubjectId),
            null,
            [],
            [],
            false,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        await fixture.Store.SaveChannelAsync(otherChannel);
        var before = fixture.Gateway.UpsertCount;
        var wrongTarget = new ContextRecordSteeringAction(
            otherChannel.Id,
            thread.Id,
            "moduledevkit",
            "thread",
            "Wrong channel",
            null,
            "ModuleDevKit");

        Assert.Multiple(() =>
        {
            Assert.That(record.ThreadId, Is.EqualTo(thread.Id));
            Assert.That(listed.Select(item => item.Id), Is.EqualTo([record.Id]));
        });
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Executor.RecordAsync(
                CreateSteeringContext(
                    TestHostActionContext.Create(fixture.Caller, HostActionEntryIngress.CrossModule),
                    ContextSteeringActionDescriptors.Record.Key,
                    wrongTarget,
                    fixture.Host)));
        Assert.That(fixture.Gateway.UpsertCount, Is.EqualTo(before));
    }

    [Test]
    public async Task ContextSteeringReplayUsesHostIdempotencyAndRejectsChangedContent()
    {
        var fixture = await CreateSteeringFixtureAsync();
        var hostContext = TestHostActionContext.Create(fixture.Caller, HostActionEntryIngress.CrossModule);
        var action = new ContextRecordSteeringAction(
            fixture.Channel.Id,
            null,
            "moduledevkit",
            "replay",
            "Stable steering",
            "Details",
            "ModuleDevKit");
        var first = await fixture.Executor.RecordAsync(
            CreateSteeringContext(hostContext, ContextSteeringActionDescriptors.Record.Key, action, fixture.Host));
        var beforeReplay = fixture.Gateway.UpsertCount;
        var replay = await fixture.Executor.RecordAsync(
            CreateSteeringContext(hostContext, ContextSteeringActionDescriptors.Record.Key, action, fixture.Host));
        var changed = action with { Summary = "Changed steering" };

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Executor.RecordAsync(
                CreateSteeringContext(hostContext, ContextSteeringActionDescriptors.Record.Key, changed, fixture.Host)));

        Assert.Multiple(() =>
        {
            Assert.That(replay.Id, Is.EqualTo(first.Id));
            Assert.That(replay.CreatedAt, Is.EqualTo(first.CreatedAt));
            Assert.That(fixture.Gateway.UpsertCount, Is.EqualTo(beforeReplay));
            Assert.That(fixture.Gateway.Count(ContextStore.ModuleId, ContextStore.SteeringStorage), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ContextSteeringRejectsUnauthorizedAndCancelledWrites()
    {
        var fixture = await CreateSteeringFixtureAsync();
        var before = fixture.Gateway.UpsertCount;
        var unauthorized = new RequestPrincipal(
            Guid.NewGuid().ToString("D"),
            "Unassigned",
            new HashSet<string>(["agent"]),
            true);
        var unauthorizedAction = new ContextRecordSteeringAction(
            fixture.Channel.Id,
            null,
            "moduledevkit",
            "security",
            "Denied",
            null,
            "ModuleDevKit");

        Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await fixture.Executor.RecordAsync(
                CreateSteeringContext(
                    TestHostActionContext.Create(unauthorized, HostActionEntryIngress.CrossModule),
                    ContextSteeringActionDescriptors.Record.Key,
                    unauthorizedAction,
                    fixture.Host)));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await fixture.Executor.RecordAsync(
                CreateSteeringContext(
                    TestHostActionContext.Create(fixture.Caller, HostActionEntryIngress.CrossModule),
                    ContextSteeringActionDescriptors.Record.Key,
                    unauthorizedAction,
                    fixture.Host),
                cancellation.Token));

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Gateway.UpsertCount, Is.EqualTo(before));
            Assert.That(fixture.Gateway.Count(ContextStore.ModuleId, ContextStore.SteeringStorage), Is.Zero);
        });
    }

    [Test]
    public async Task ContextSteeringAcceptsFieldMaximumsAndRejectsOverLimitWrites()
    {
        var fixture = await CreateSteeringFixtureAsync();
        var acceptedActions = new[]
        {
            new ContextRecordSteeringAction(
                fixture.Channel.Id,
                null,
                new string('s', 128),
                "category",
                "summary",
                "details",
                "client"),
            new ContextRecordSteeringAction(
                fixture.Channel.Id,
                null,
                "source",
                new string('c', 128),
                "summary",
                "details",
                "client"),
            new ContextRecordSteeringAction(
                fixture.Channel.Id,
                null,
                "source",
                "category",
                new string('m', 8000),
                "details",
                "client"),
            new ContextRecordSteeringAction(
                fixture.Channel.Id,
                null,
                "source",
                "category",
                "summary",
                new string('d', 16000),
                "client"),
            new ContextRecordSteeringAction(
                fixture.Channel.Id,
                null,
                "source",
                "category",
                "summary",
                "details",
                new string('t', 128)),
        };
        var acceptedRecords = new List<ContextSteeringRecord>();

        foreach (var acceptedAction in acceptedActions)
        {
            acceptedRecords.Add(await fixture.Executor.RecordAsync(
                CreateSteeringContext(
                    TestHostActionContext.Create(fixture.Caller, HostActionEntryIngress.CrossModule),
                    ContextSteeringActionDescriptors.Record.Key,
                    acceptedAction,
                    fixture.Host)));
        }

        var beforeRejectedWrites = fixture.Gateway.UpsertCount;
        var overLimitActions = new[]
        {
            acceptedActions[0] with { Source = new string('s', 129) },
            acceptedActions[1] with { Category = new string('c', 129) },
            acceptedActions[2] with { Summary = new string('m', 8001) },
            acceptedActions[3] with { Details = new string('d', 16001) },
            acceptedActions[4] with { ClientType = new string('t', 129) },
        };

        foreach (var overLimitAction in overLimitActions)
        {
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await fixture.Executor.RecordAsync(
                    CreateSteeringContext(
                        TestHostActionContext.Create(fixture.Caller, HostActionEntryIngress.CrossModule),
                        ContextSteeringActionDescriptors.Record.Key,
                        overLimitAction,
                        fixture.Host)));
        }

        Assert.Multiple(() =>
        {
            Assert.That(acceptedRecords[0].Source, Has.Length.EqualTo(128));
            Assert.That(acceptedRecords[1].Category, Has.Length.EqualTo(128));
            Assert.That(acceptedRecords[2].Summary, Has.Length.EqualTo(8000));
            Assert.That(acceptedRecords[3].Details, Has.Length.EqualTo(16000));
            Assert.That(acceptedRecords[4].ClientType, Has.Length.EqualTo(128));
            Assert.That(fixture.Gateway.Count(ContextStore.ModuleId, ContextStore.SteeringStorage), Is.EqualTo(5));
            Assert.That(fixture.Gateway.UpsertCount, Is.EqualTo(beforeRejectedWrites));
        });
    }

    [Test]
    public async Task ContextSteeringAssemblyMergesBoundedChannelAndThreadRecords()
    {
        var fixture = await CreateSteeringFixtureAsync();
        var thread = await fixture.Store.CreateThreadAsync(
            fixture.Caller,
            fixture.Channel.Id,
            "Assembly thread",
            hostContext: TestHostActionContext.Create(fixture.Caller, HostActionEntryIngress.CrossModule));
        var channelRecords = new List<ContextSteeringRecord>();
        var threadRecords = new List<ContextSteeringRecord>();
        for (var index = 0; index < 5; index++)
        {
            channelRecords.Add(await fixture.Executor.RecordAsync(
                CreateSteeringContext(
                    TestHostActionContext.Create(fixture.Caller, HostActionEntryIngress.CrossModule),
                    ContextSteeringActionDescriptors.Record.Key,
                    new ContextRecordSteeringAction(
                        fixture.Channel.Id,
                        null,
                        "moduledevkit",
                        "channel",
                        $"Channel {index}",
                        null,
                        "ModuleDevKit"),
                    fixture.Host)));
            threadRecords.Add(await fixture.Executor.RecordAsync(
                CreateSteeringContext(
                    TestHostActionContext.Create(fixture.Caller, HostActionEntryIngress.CrossModule),
                    ContextSteeringActionDescriptors.Record.Key,
                    new ContextRecordSteeringAction(
                        fixture.Channel.Id,
                        thread.Id,
                        "moduledevkit",
                        "thread",
                        $"Thread {index}",
                        null,
                        "ModuleDevKit"),
                    fixture.Host)));
        }

        var assembled = await fixture.Store.LoadSteeringForThreadAsync(
            fixture.Caller,
            thread.Id,
            4,
            hostContext: TestHostActionContext.Create(fixture.Caller, HostActionEntryIngress.CrossModule));
        var newestChannel = channelRecords.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).First();
        var newestThread = threadRecords.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).First();
        var steeringQueries = fixture.Gateway.QueryLimits
            .Where(item => item.Storage == ContextStore.SteeringStorage)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(assembled, Has.Count.EqualTo(4));
            Assert.That(assembled.Select(item => item.Id), Does.Contain(newestChannel.Id));
            Assert.That(assembled.Select(item => item.Id), Does.Contain(newestThread.Id));
            Assert.That(assembled.Select(item => item.CreatedAt), Is.Ordered.Ascending);
            Assert.That(steeringQueries.Select(item => item.Limit), Is.EqualTo([4, 4]));
        });
    }

    [Test]
    public async Task ContextSteeringAssemblySelectsNewestGlobalUnion()
    {
        var fixture = await CreateSteeringFixtureAsync();
        var thread = await fixture.Store.CreateThreadAsync(
            fixture.Caller,
            fixture.Channel.Id,
            "Skewed assembly thread",
            hostContext: TestHostActionContext.Create(fixture.Caller, HostActionEntryIngress.CrossModule));
        var oldChannel = new ContextSteeringRecord(
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            fixture.Channel.Id,
            null,
            "moduledevkit",
            "channel",
            "Old channel steering",
            null,
            "ModuleDevKit",
            fixture.Caller,
            DateTimeOffset.Parse("2026-08-28T09:00:00+00:00"));
        SeedSteeringRecord(fixture.Gateway, oldChannel);
        var threadRecords = new[]
        {
            new ContextSteeringRecord(
                Guid.Parse("22222222-2222-4222-8222-222222222221"),
                fixture.Channel.Id,
                thread.Id,
                "moduledevkit",
                "thread",
                "New thread steering 0",
                null,
                "ModuleDevKit",
                fixture.Caller,
                DateTimeOffset.Parse("2026-08-28T10:00:00+00:00")),
            new ContextSteeringRecord(
                Guid.Parse("22222222-2222-4222-8222-222222222222"),
                fixture.Channel.Id,
                thread.Id,
                "moduledevkit",
                "thread",
                "New thread steering 1",
                null,
                "ModuleDevKit",
                fixture.Caller,
                DateTimeOffset.Parse("2026-08-28T11:00:00+00:00")),
            new ContextSteeringRecord(
                Guid.Parse("22222222-2222-4222-8222-222222222223"),
                fixture.Channel.Id,
                thread.Id,
                "moduledevkit",
                "thread",
                "New thread steering 2",
                null,
                "ModuleDevKit",
                fixture.Caller,
                DateTimeOffset.Parse("2026-08-28T12:00:00+00:00")),
        };
        foreach (var record in threadRecords)
            SeedSteeringRecord(fixture.Gateway, record);

        var assembled = await fixture.Store.LoadSteeringForThreadAsync(
            fixture.Caller,
            thread.Id,
            2,
            hostContext: TestHostActionContext.Create(fixture.Caller, HostActionEntryIngress.CrossModule));
        var steeringQueries = fixture.Gateway.QueryLimits
            .Where(item => item.Storage == ContextStore.SteeringStorage)
            .TakeLast(2)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(assembled, Has.Count.EqualTo(2));
            Assert.That(assembled.Select(item => item.Id), Does.Not.Contain(oldChannel.Id));
            Assert.That(assembled.All(item => item.ThreadId == thread.Id), Is.True);
            Assert.That(assembled.Select(item => item.Id), Is.EqualTo(threadRecords.Skip(1).Select(item => item.Id)));
            Assert.That(assembled.Select(item => item.CreatedAt), Is.Ordered.Ascending);
            Assert.That(steeringQueries.Select(item => item.Limit), Is.EqualTo([2, 2]));
        });
    }

    [Test]
    public async Task ContextSteeringScopeOrderAppliesIdTieBreakBeforeLimit()
    {
        var fixture = await CreateSteeringFixtureAsync();
        var createdAt = DateTimeOffset.Parse("2026-08-28T13:00:00+00:00");
        var records = new[] { 2, 4, 1, 3 }
            .Select(value => new ContextSteeringRecord(
                Guid.Parse($"33333333-3333-4333-8333-33333333333{value}"),
                fixture.Channel.Id,
                null,
                "moduledevkit",
                "tie-order",
                $"Tie record {value}",
                null,
                "ModuleDevKit",
                fixture.Caller,
                createdAt))
            .ToArray();
        foreach (var record in records)
            SeedSteeringRecord(fixture.Gateway, record);

        var selected = await fixture.Store.ListSteeringAsync(
            fixture.Caller,
            new ContextListSteeringAction(fixture.Channel.Id, null, 2),
            hostContext: TestHostActionContext.Create(fixture.Caller, HostActionEntryIngress.CrossModule));
        var steeringQueries = fixture.Gateway.QueryLimits
            .Where(item => item.Storage == ContextStore.SteeringStorage)
            .ToArray();
        var expectedIds = records
            .OrderByDescending(record => record.Id.ToString("N"), StringComparer.Ordinal)
            .Take(2)
            .Select(record => record.Id)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(selected.Select(record => record.Id), Is.EqualTo(expectedIds));
            Assert.That(selected.Select(record => record.CreatedAt), Is.All.EqualTo(createdAt));
            Assert.That(steeringQueries.Select(item => item.Limit), Is.EqualTo([2]));
        });
    }

    [Test]
    public async Task ContextSteeringConcurrentIdenticalReplayConvergesToStoredWinner()
    {
        var fixture = await CreateSteeringFixtureAsync();
        var hostContext = TestHostActionContext.Create(fixture.Caller, HostActionEntryIngress.CrossModule);
        var action = new ContextRecordSteeringAction(
            fixture.Channel.Id,
            null,
            "moduledevkit",
            "concurrent",
            "Concurrent steering",
            "Stable details",
            "ModuleDevKit");
        var bothUpsertsEntered = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUpserts = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var upsertsEntered = 0;
        fixture.Gateway.BeforeOperationAsync = async (module, storage, operation) =>
        {
            if (module != ContextStore.ModuleId
                || storage != ContextStore.SteeringStorage
                || operation != ModuleStorageOperations.Upsert)
                return;

            if (Interlocked.Increment(ref upsertsEntered) == 2)
                bothUpsertsEntered.TrySetResult(null);
            await releaseUpserts.Task.WaitAsync(TimeSpan.FromSeconds(5));
        };

        var firstTask = fixture.Executor.RecordAsync(
            CreateSteeringContext(
                hostContext,
                ContextSteeringActionDescriptors.Record.Key,
                action,
                fixture.Host));
        var secondTask = fixture.Executor.RecordAsync(
            CreateSteeringContext(
                hostContext,
                ContextSteeringActionDescriptors.Record.Key,
                action,
                fixture.Host));

        try
        {
            await bothUpsertsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            releaseUpserts.TrySetResult(null);
            var results = await Task.WhenAll(firstTask, secondTask)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Multiple(() =>
            {
                Assert.That(results, Has.Length.EqualTo(2));
                Assert.That(results[0].Id, Is.EqualTo(results[1].Id));
                Assert.That(results[0].CreatedAt, Is.EqualTo(results[1].CreatedAt));
                Assert.That(results[0].Summary, Is.EqualTo(results[1].Summary));
                Assert.That(
                    ContextStore.FormatSteering(results[0]),
                    Is.EqualTo(ContextStore.FormatSteering(results[1])));
                Assert.That(fixture.Gateway.Count(
                    ContextStore.ModuleId,
                    ContextStore.SteeringStorage), Is.EqualTo(1));
            });
        }
        finally
        {
            releaseUpserts.TrySetResult(null);
            fixture.Gateway.BeforeOperationAsync = null;
        }
    }

    private static void SeedSteeringRecord(
        InMemoryStorageGateway gateway,
        ContextSteeringRecord record) =>
        gateway.Seed(
            ContextStore.ModuleId,
            ContextStore.SteeringStorage,
            record.Id.ToString("N"),
            record,
            new
            {
                channelId = record.ChannelId.ToString("N"),
                threadId = record.ThreadId?.ToString("N"),
                scope = record.ThreadId is null ? "channel" : "thread",
                source = record.Source,
                category = record.Category,
                createdAt = record.CreatedAt,
                createdAtId = $"{record.CreatedAt.UtcDateTime.Ticks:D19}:{record.Id:N}",
            });

    [Test]
    public async Task ContextSteeringAssemblyPayloadSortsCallerRolesDeterministically()
    {
        var fixture = await CreateSteeringFixtureAsync();
        var firstCaller = new RequestPrincipal(
            fixture.Caller.SubjectId,
            fixture.Caller.DisplayName,
            new HashSet<string>(["zulu", "alpha"]),
            true);
        var secondCaller = new RequestPrincipal(
            fixture.Caller.SubjectId,
            fixture.Caller.DisplayName,
            new HashSet<string>(["alpha", "zulu"]),
            true);
        var record = new ContextSteeringRecord(
            Guid.NewGuid(),
            fixture.Channel.Id,
            null,
            "source",
            "category",
            "summary",
            null,
            "ModuleDevKit",
            firstCaller,
            DateTimeOffset.Parse("2026-08-28T10:00:00+00:00"));

        var first = ContextStore.FormatSteering(record);
        var second = ContextStore.FormatSteering(record with { Caller = secondCaller });

        Assert.That(first, Is.EqualTo(second));
        Assert.That(first.IndexOf("alpha", StringComparison.Ordinal), Is.LessThan(first.IndexOf("zulu", StringComparison.Ordinal)));
    }

    private static async Task<SteeringFixture> CreateSteeringFixtureAsync()
    {
        var gateway = new InMemoryStorageGateway();
        var permissionStore = new PermissionPolicyStore(gateway);
        var policy = new TwoTierPermissionPolicy(permissionStore);
        var callerId = Guid.NewGuid();
        var caller = new RequestPrincipal(
            callerId.ToString("D"),
            "Steering caller",
            new HashSet<string>(["zulu", "alpha"]),
            true);
        var now = DateTimeOffset.UtcNow;
        await permissionStore.SaveAsync(new PermissionPolicyRecord(
            caller.SubjectId,
            [],
            [
                ContextAccessCapabilities.CreateThread,
                ContextAccessCapabilities.ReadHistory,
                ContextAccessCapabilities.CommitExchange,
            ],
            [],
            PermissionClearance.Independent,
            false,
            [],
            null,
            now));
        var host = new PolicyHostActionEntry(policy);
        var permission = new HostPermissionActionEntry(host);
        var store = new ContextStore(gateway, permission);
        var channel = new ContextChannelRecord(
            Guid.NewGuid(),
            "Steering channel",
            callerId,
            null,
            [],
            [],
            false,
            now,
            now);
        await store.SaveChannelAsync(channel);
        return new SteeringFixture(
            gateway,
            permission,
            store,
            new ContextSteeringActionExecutor(store, permission),
            caller,
            channel,
            host);
    }

    private static ActionContext<TAction> CreateSteeringContext<TAction>(
        HostActionEntryRequestContext hostContext,
        SharpClawActionKey actionKey,
        TAction action,
        IHostActionEntry hostEntry) =>
        new(
            hostContext.InvocationId,
            hostContext.ParentInvocationId,
            hostContext.TraceId,
            hostContext.IdempotencyKey,
            hostContext.Depth,
            hostContext.Attempt,
            hostContext.Deadline,
            actionKey,
            ContextModule.ModuleIdValue,
            hostContext.Caller,
            action,
            hostContext.Features,
            new ActionPipelineSnapshot("test", [], [], 16))
        {
            HostActionEntry = hostEntry,
        };

    private sealed record SteeringFixture(
        InMemoryStorageGateway Gateway,
        HostPermissionActionEntry Permission,
        ContextStore Store,
        ContextSteeringActionExecutor Executor,
        RequestPrincipal Caller,
        ContextChannelRecord Channel,
        PolicyHostActionEntry Host);

    private static ModuleContributionGraph CompileModule(ISharpClawModule module) =>
        SharpClawModuleCompiler.Compile(
            module,
            options: new ModuleCompilationOptions
            {
                HostingMode = ModuleHostingMode.InProcess,
                RequireManifestRequests = false,
            });

    private static ActionDescriptor<TAction, TResult> GetAction<TAction, TResult>(
        ModuleContributionGraph graph,
        string key) =>
        graph.Actions
            .Select(item => item.TypedDescriptor)
            .OfType<ActionDescriptor<TAction, TResult>>()
            .Single(item => item.Key.Value == key);

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

    private sealed class RecordingApplicationBuilder : ISharpClawApplicationBuilder
    {
        public RecordingEndpoints Endpoints { get; } = new();
        IEndpointContributionBuilder ISharpClawApplicationBuilder.Endpoints => Endpoints;
        public RecordingCli Cli { get; } = new();
        ICliContributionBuilder ISharpClawApplicationBuilder.Cli => Cli;
        public RecordingUi Ui { get; } = new();
        IUiContributionBuilder ISharpClawApplicationBuilder.Ui => Ui;
    }

    private sealed class RecordingEndpoints : IEndpointContributionBuilder
    {
        public List<EndpointRegistration> Items { get; } = [];

        public void AddHttp<THandler>(ModuleEndpointRouteDescriptor descriptor)
            where THandler : class, IModuleHttpEndpointHandler =>
            Items.Add(new(typeof(THandler), descriptor));

        public void AddWebSocket<THandler>(ModuleEndpointRouteDescriptor descriptor)
            where THandler : class, IModuleWebSocketEndpointHandler =>
            Items.Add(new(typeof(THandler), descriptor));
    }

    private sealed record EndpointRegistration(
        Type HandlerType,
        ModuleEndpointRouteDescriptor Descriptor);

    private sealed class RecordingCli : ICliContributionBuilder
    {
        public List<ModuleCliCommandDescriptor> Items { get; } = [];

        public void Add<THandler>(ModuleCliCommandDescriptor descriptor)
            where THandler : IModuleCliHandler
        {
            Items.Add(descriptor);
        }
    }

    private sealed class RecordingUi : IUiContributionBuilder
    {
        public void Add<TContribution>()
        {
        }
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
        public List<string> Items { get; } = [];
        public IActionHookRegistrationBuilder For(SharpClawActionKey key) => new NoOpActionRegistration(Items, key.Value);
        public IActionHookRegistrationBuilder Category(string category) => new NoOpActionRegistration(Items, category);
        public IActionHookRegistrationBuilder AnyAction() => new NoOpActionRegistration(Items, "*");
    }

    private sealed class NoOpActionRegistration(
        List<string> items,
        string target) : IActionHookRegistrationBuilder
    {
        public void Use<TInterceptor>(HookOrdering ordering) =>
            items.Add($"{target}:{typeof(TInterceptor).Name}:{ordering.Id}");

        public void UseAny<TInterceptor>(HookOrdering ordering) =>
            items.Add($"{target}:any:{typeof(TInterceptor).Name}:{ordering.Id}");
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

    private sealed class RecordingActionDispatcher : IActionDispatcher
    {
        public int RequiredCalls { get; private set; }
        public ActionPipelineSnapshot? Snapshot { get; private set; }

        public ValueTask<IActionOutcome<TResult>> RunAsync<TAction, TResult>(
            ActionDescriptor<TAction, TResult> descriptor,
            TAction action,
            Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
            ActionPipelineSnapshot snapshot,
            CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public async ValueTask<TResult> RunRequiredAsync<TAction, TResult>(
            ActionDescriptor<TAction, TResult> descriptor,
            TAction action,
            Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
            ActionPipelineSnapshot snapshot,
            CancellationToken ct)
        {
            RequiredCalls++;
            Snapshot = snapshot;
            var context = new ActionContext<TAction>(
                Guid.NewGuid(),
                null,
                Guid.NewGuid(),
                Guid.NewGuid(),
                0,
                1,
                DateTimeOffset.UtcNow.AddMinutes(1),
                descriptor.Key,
                "test",
                RequestPrincipal.Anonymous,
                action,
                ExtensionFeatureSet.Empty,
                snapshot);
            return await terminal(context, ct);
        }

        public ValueTask<IActionOutcome<TResult>> RunExternalAsync<TAction, TResult>(
            ActionDescriptor<TAction, TResult> descriptor,
            TAction action,
            Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
            ActionPipelineSnapshot snapshot,
            SidecarExternalActionDispatchAuthority authority,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask<IActionOutcome<JsonElement>> RunExternalSerializedAsync(
            SidecarActionDefinition definition,
            SidecarActionDescriptorIdentity descriptor,
            JsonElement action,
            Func<ActionContext<JsonElement>, CancellationToken, ValueTask<JsonElement>> terminal,
            ActionPipelineSnapshot snapshot,
            SidecarExternalActionDispatchAuthority authority,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask<TResult> RunExternalRequiredAsync<TAction, TResult>(
            ActionDescriptor<TAction, TResult> descriptor,
            TAction action,
            Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
            ActionPipelineSnapshot snapshot,
            SidecarExternalActionDispatchAuthority authority,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private static async Task AssertEndpointRoutesAsync(
        IModuleHttpEndpointHandler handler,
        IReadOnlyList<ModuleEndpointRouteDescriptor> routes,
        IReadOnlyDictionary<(string Path, string Method), string> expectedOperations,
        string descriptorKey,
        Func<object, string> readOperation)
    {
        Assert.Multiple(() =>
        {
            Assert.That(routes, Has.Count.EqualTo(expectedOperations.Count));
            Assert.That(routes.All(route => route.IsWellFormed), Is.True);
            Assert.That(routes.Select(route => route.Id), Is.Unique);
            Assert.That(routes.Select(route => route.ToRouteIdentity()), Is.Unique);
        });

        foreach (ModuleEndpointRouteDescriptor route in routes)
        {
            Assert.That(
                expectedOperations.TryGetValue((route.Path, route.Method), out string? expectedOperation),
                Is.True,
                $"No expected operation is defined for {route.Method} {route.Path}.");
            var hostEntry = new RecordingHostActionEntry();
            HostEndpointRouteRequest request = CreateEndpointRequest(route, []);

            Assert.That(request.IsWellFormed(DateTimeOffset.UtcNow), Is.True);
            ModuleHttpEndpointResponse response = await handler.InvokeAsync(
                request,
                hostEntry,
                default);

            Assert.Multiple(() =>
            {
                Assert.That(response.IsWellFormed, Is.True);
                Assert.That(response.StatusCode, Is.EqualTo(200));
                Assert.That(hostEntry.Keys, Is.EqualTo([descriptorKey]));
                Assert.That(hostEntry.Contexts.Single(), Is.SameAs(request.Invocation.HostActionContext));
                Assert.That(readOperation(hostEntry.Actions.Single()), Is.EqualTo(expectedOperation));
            });
        }
    }

    private static HostEndpointRouteRequest CreateEndpointRequest(
        ModuleEndpointRouteDescriptor route,
        byte[] body)
    {
        HostActionEntryRequestContext hostContext = TestHostActionContext.Create(
            new RequestPrincipal(Guid.NewGuid().ToString("D"), IsAuthenticated: true),
            HostActionEntryIngress.Endpoint) with
        {
            Contribution = new HostActionEntryContribution(
                new HostActionEntryIngressBinding(HostActionEntryIngress.Endpoint, route.Id),
                new HostActionEntryLineage(
                    new SharpClawActionKey("test.endpoint.route"),
                    1,
                    "test-descriptor-hash",
                    "test.endpoint.request",
                    1,
                    "test-input-schema-hash",
                    null,
                    null)),
        };
        return new HostEndpointRouteRequest(
            new HostEndpointInvocation(hostContext.InvocationId, route.Id, hostContext),
            route.ToRouteIdentity(),
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string[]>(StringComparer.Ordinal),
            body);
    }

    private static string? ReadErrorCode(ModuleHttpEndpointResponse response)
    {
        using JsonDocument body = JsonDocument.Parse(response.Body);
        return body.RootElement.GetProperty("error").GetString();
    }

    private static (IModuleHttpEndpointHandler Handler, IReadOnlyList<ModuleEndpointRouteDescriptor> Routes)[]
        EndpointOwners() =>
        [
            (new ContextEndpointContribution(new ContextApiActionTerminal(null!)),
                ContextEndpointContribution.EndpointRoutes),
            (new PermissionEndpointContribution(new PermissionApiActionTerminal(null!)),
                PermissionEndpointContribution.EndpointRoutes),
            (new AgentsEndpointContribution(new AgentsApiActionTerminal(null!)),
                AgentsEndpointContribution.EndpointRoutes),
        ];

    private static IReadOnlyDictionary<(string Path, string Method), string> ContextEndpointOperations() =>
        new Dictionary<(string Path, string Method), string>
        {
            [(ContextEndpointContribution.CreateThreadRoute, "POST")] = ContextApiOperations.CreateThread,
            [(ContextEndpointContribution.ReadHistoryRoute, "POST")] = ContextApiOperations.ReadHistory,
            [(ContextEndpointContribution.CommitExchangeRoute, "POST")] = ContextApiOperations.CommitExchange,
            [(ContextEndpointContribution.ChannelRoutes[0], "GET")] = ContextApiOperations.ListChannels,
            [(ContextEndpointContribution.ChannelRoutes[1], "GET")] = ContextApiOperations.ListChannels,
            [(ContextEndpointContribution.ChannelRoutes[2], "GET")] = ContextApiOperations.GetChannel,
            [(ContextEndpointContribution.ChannelRoutes[3], "POST")] = ContextApiOperations.CreateChannel,
            [(ContextEndpointContribution.ChannelRoutes[4], "PUT")] = ContextApiOperations.UpdateChannel,
            [(ContextEndpointContribution.ChannelRoutes[5], "DELETE")] = ContextApiOperations.DeleteChannel,
            [(ContextEndpointContribution.ChannelRoutes[6], "POST")] = ContextApiOperations.AssignChannel,
            [(ContextEndpointContribution.ChannelRoutes[7], "POST")] = ContextApiOperations.UnassignChannel,
            [(ContextEndpointContribution.ChannelRoutes[8], "POST")] = ContextApiOperations.OptInChannel,
            [(ContextEndpointContribution.ChannelRoutes[9], "POST")] = ContextApiOperations.OptOutChannel,
            [(ContextEndpointContribution.ChannelRoutes[10], "GET")] = ContextApiOperations.ChannelPermissions,
            [(ContextEndpointContribution.ChannelRoutes[11], "POST")] = ContextApiOperations.SynchronizeChannel,
            [(ContextEndpointContribution.ChannelRoutes[12], "POST")] = ContextApiOperations.SynchronizeChannel,
            [(ContextEndpointContribution.ChannelContextRoutes[0], "GET")] = ContextApiOperations.ListContexts,
            [(ContextEndpointContribution.ChannelContextRoutes[1], "GET")] = ContextApiOperations.ListContexts,
            [(ContextEndpointContribution.ChannelContextRoutes[2], "GET")] = ContextApiOperations.GetContext,
            [(ContextEndpointContribution.ChannelContextRoutes[3], "POST")] = ContextApiOperations.CreateContext,
            [(ContextEndpointContribution.ChannelContextRoutes[4], "PUT")] = ContextApiOperations.UpdateContext,
            [(ContextEndpointContribution.ChannelContextRoutes[5], "DELETE")] = ContextApiOperations.DeleteContext,
            [(ContextEndpointContribution.ChannelContextRoutes[6], "POST")] = ContextApiOperations.AssignContext,
            [(ContextEndpointContribution.ChannelContextRoutes[7], "POST")] = ContextApiOperations.UnassignContext,
            [(ContextEndpointContribution.ChannelContextRoutes[8], "POST")] = ContextApiOperations.ActivateContext,
            [(ContextEndpointContribution.ChannelContextRoutes[9], "POST")] = ContextApiOperations.DeactivateContext,
            [(ContextEndpointContribution.ChannelContextRoutes[10], "GET")] = ContextApiOperations.ContextPermissions,
            [(ContextEndpointContribution.ChannelContextRoutes[11], "POST")] = ContextApiOperations.SynchronizeContext,
            [(ContextEndpointContribution.ThreadRoutes[0], "GET")] = ContextApiOperations.ListThreads,
            [(ContextEndpointContribution.ThreadRoutes[1], "GET")] = ContextApiOperations.GetThread,
            [(ContextEndpointContribution.ThreadRoutes[2], "POST")] = ContextApiOperations.CreateThread,
            [(ContextEndpointContribution.ThreadRoutes[3], "PUT")] = ContextApiOperations.UpdateThread,
            [(ContextEndpointContribution.ThreadRoutes[4], "DELETE")] = ContextApiOperations.DeleteThread,
        };

    private static IReadOnlyDictionary<(string Path, string Method), string> PermissionEndpointOperations() =>
        new Dictionary<(string Path, string Method), string>
        {
            [(PermissionEndpointContribution.EvaluateRoute, "POST")] = PermissionApiOperations.Evaluate,
            [(PermissionEndpointContribution.GrantRoute, "POST")] = PermissionApiOperations.Grant,
            [(PermissionEndpointContribution.RevokeRoute, "POST")] = PermissionApiOperations.Revoke,
            [(PermissionEndpointContribution.ApproveRoute, "POST")] = PermissionApiOperations.Approve,
            [(PermissionEndpointContribution.PolicyRoutes[0], "GET")] = PermissionApiOperations.ListPolicies,
            [(PermissionEndpointContribution.PolicyRoutes[1], "GET")] = PermissionApiOperations.GetPolicy,
            [(PermissionEndpointContribution.PolicyRoutes[2], "POST")] = PermissionApiOperations.SavePolicy,
            [(PermissionEndpointContribution.PolicyRoutes[3], "DELETE")] = PermissionApiOperations.DeletePolicy,
            [(PermissionEndpointContribution.RoleRoutes[0], "GET")] = PermissionApiOperations.ListRoles,
            [(PermissionEndpointContribution.RoleRoutes[1], "GET")] = PermissionApiOperations.GetRole,
            [(PermissionEndpointContribution.RoleRoutes[2], "POST")] = PermissionApiOperations.SaveRole,
            [(PermissionEndpointContribution.RoleRoutes[3], "DELETE")] = PermissionApiOperations.DeleteRole,
            [(PermissionEndpointContribution.RoleRoutes[4], "POST")] = PermissionApiOperations.AssignRole,
            [(PermissionEndpointContribution.PermissionSetRoutes[0], "GET")] = PermissionApiOperations.ListPermissionSets,
            [(PermissionEndpointContribution.PermissionSetRoutes[1], "GET")] = PermissionApiOperations.GetPermissionSet,
            [(PermissionEndpointContribution.PermissionSetRoutes[2], "POST")] = PermissionApiOperations.SavePermissionSet,
            [(PermissionEndpointContribution.PermissionSetRoutes[3], "DELETE")] = PermissionApiOperations.DeletePermissionSet,
            [(PermissionEndpointContribution.PermissionSetRoutes[4], "POST")] = PermissionApiOperations.AssignPermissionSet,
        };

    private static IReadOnlyDictionary<(string Path, string Method), string> AgentsEndpointOperations() =>
        new Dictionary<(string Path, string Method), string>
        {
            [(AgentsEndpointContribution.AgentRoutes[0], "GET")] = AgentsApiOperations.ListAgents,
            [(AgentsEndpointContribution.AgentRoutes[1], "GET")] = AgentsApiOperations.GetAgent,
            [(AgentsEndpointContribution.AgentRoutes[2], "POST")] = AgentsApiOperations.CreateAgent,
            [(AgentsEndpointContribution.AgentRoutes[3], "PUT")] = AgentsApiOperations.UpdateAgent,
            [(AgentsEndpointContribution.AgentRoutes[4], "DELETE")] = AgentsApiOperations.DeleteAgent,
            [(AgentsEndpointContribution.AgentRoutes[5], "POST")] = AgentsApiOperations.AssignRole,
            [(AgentsEndpointContribution.AgentRoutes[6], "POST")] = AgentsApiOperations.SynchronizeAgent,
            [(AgentsEndpointContribution.AgentRoutes[7], "GET")] = AgentsApiOperations.GetCost,
            [(AgentsEndpointContribution.SkillRoutes[0], "GET")] = AgentsApiOperations.ListSkills,
            [(AgentsEndpointContribution.SkillRoutes[1], "GET")] = AgentsApiOperations.GetSkill,
            [(AgentsEndpointContribution.SkillRoutes[2], "POST")] = AgentsApiOperations.SaveSkill,
            [(AgentsEndpointContribution.SkillRoutes[3], "DELETE")] = AgentsApiOperations.DeleteSkill,
            [(AgentsEndpointContribution.SkillRoutes[4], "POST")] = AgentsApiOperations.AccessSkill,
            [(AgentsEndpointContribution.MemoryRoutes[0], "POST")] = AgentsApiOperations.WriteMemory,
            [(AgentsEndpointContribution.MemoryRoutes[1], "POST")] = AgentsApiOperations.SearchMemory,
        };

    private sealed class RecordingHostActionEntry : IHostActionEntry, IModuleCrossSidecarActionEntry
    {
        public List<string> Keys { get; } = [];
        public List<HostActionEntryRequestContext> Contexts { get; } = [];
        public List<object> Actions { get; } = [];
        public List<Type> TerminalTypes { get; } = [];
        public PermissionDecision? PermissionResult { get; set; }
        public Exception? ExceptionToThrow { get; init; }

        public ValueTask<IActionOutcome<TResult>> InvokeAsync<TAction, TResult>(
            HostActionEntryRequest<TAction, TResult> request,
            IHostActionEntryTerminal<TAction, TResult> terminal,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Keys.Add(request.Descriptor.Key.Value);
            Contexts.Add(request.Context);
            Actions.Add(request.Action!);
            TerminalTypes.Add(terminal.GetType());
            if (ExceptionToThrow is not null)
                throw ExceptionToThrow;

            var result = typeof(TResult) == typeof(JsonElement)
                ? (TResult)(object)JsonSerializer.SerializeToElement(new { accepted = true })
                : typeof(TResult) == typeof(PermissionDecision)
                    ? (TResult)(object)(PermissionResult ?? PermissionDecision.Allow(
                        "test_allowed",
                        1,
                        PermissionClearance.Independent))
                    : default!;
            return ValueTask.FromResult<IActionOutcome<TResult>>(
                new HostActionOutcome<TResult>(ActionOutcomeKind.Completed, result));
        }

        public ValueTask<IActionOutcome<TResult>> InvokeNestedAsync<TParentAction, TAction, TResult>(
            HostActionEntryNestedRequest<TParentAction, TAction, TResult> request,
            IHostActionEntryTerminal<TAction, TResult> terminal,
            CancellationToken ct)
        {
            TerminalTypes.Add(terminal.GetType());
            var result = typeof(TResult) == typeof(JsonElement)
                ? (TResult)(object)JsonSerializer.SerializeToElement(new { accepted = true })
                : typeof(TResult) == typeof(PermissionDecision)
                    ? (TResult)(object)(PermissionResult ?? PermissionDecision.Allow(
                        "test_allowed",
                        1,
                        PermissionClearance.Independent))
                    : default!;
            return ValueTask.FromResult<IActionOutcome<TResult>>(
                new HostActionOutcome<TResult>(ActionOutcomeKind.Completed, result));
        }

        public ValueTask<IActionOutcome<TResult>> InvokeCrossSidecarAsync<TAction, TResult>(
            ModuleCrossSidecarActionEntryRequest<TAction, TResult> request,
            CancellationToken ct)
        {
            var result = typeof(TResult) == typeof(JsonElement)
                ? (TResult)(object)JsonSerializer.SerializeToElement(new { accepted = true })
                : typeof(TResult) == typeof(PermissionDecision)
                    ? (TResult)(object)(PermissionResult ?? PermissionDecision.Allow(
                        "test_allowed",
                        1,
                        PermissionClearance.Independent))
                    : default!;
            return ValueTask.FromResult<IActionOutcome<TResult>>(
                new HostActionOutcome<TResult>(ActionOutcomeKind.Completed, result));
        }
    }

    private sealed class HostActionOutcome<TResult>(
        ActionOutcomeKind kind,
        TResult result) : IActionOutcome<TResult>
    {
        public ActionOutcomeKind Kind { get; } = kind;
        public TResult Result { get; } = result;
        public ContinuationToken? Continuation => null;
        public ExecutionError? Error => null;
        public ActionUncertainty? Uncertainty => null;
    }

    private static HostPermissionActionEntry AllowAllEntry() =>
        new(new RecordingHostActionEntry());

    private static HostPermissionActionEntry PolicyEntry(TwoTierPermissionPolicy policy) =>
        new(new PolicyHostActionEntry(policy));

    private sealed class TypedPermissionHostActionEntry(
        TwoTierPermissionPolicy policy,
        RequestPrincipal caller) : IHostActionEntry, IModuleCrossSidecarActionEntry
    {
        private readonly PermissionAgentAccessActionTerminal _terminal =
            new(new PermissionActionExecutor(policy));

        public int AgentAccessCalls { get; private set; }

        public string? LastCallerSubjectId { get; private set; }

        public IReadOnlyList<string>? LastCallerRoles { get; private set; }

        public string? LastDescriptorKey { get; private set; }

        public PermissionAgentAccessAction? LastAction { get; private set; }

        public PermissionDecision? LastDecision { get; private set; }

        public ValueTask<IActionOutcome<TResult>> InvokeAsync<TAction, TResult>(
            HostActionEntryRequest<TAction, TResult> request,
            IHostActionEntryTerminal<TAction, TResult> terminal,
            CancellationToken ct) =>
            throw new NotSupportedException("The test host supports only cross-sidecar Permission calls.");

        public ValueTask<IActionOutcome<TResult>> InvokeNestedAsync<TParentAction, TAction, TResult>(
            HostActionEntryNestedRequest<TParentAction, TAction, TResult> request,
            IHostActionEntryTerminal<TAction, TResult> terminal,
            CancellationToken ct) =>
            throw new NotSupportedException("The test host supports only cross-sidecar Permission calls.");

        public async ValueTask<IActionOutcome<TResult>> InvokeCrossSidecarAsync<TAction, TResult>(
            ModuleCrossSidecarActionEntryRequest<TAction, TResult> request,
            CancellationToken ct)
        {
            if (request.Action is not PermissionAgentAccessAction action
                || typeof(TResult) != typeof(PermissionDecision)
                || !ReferenceEquals(request.Descriptor, PermissionActionDescriptors.AgentAccess))
            {
                throw new InvalidOperationException(
                    $"The test host does not support '{request.Descriptor.Key.Value}'.");
            }

            AgentAccessCalls++;
            LastCallerSubjectId = caller.SubjectId;
            LastCallerRoles = caller.Roles?.ToArray();
            LastDescriptorKey = request.Descriptor.Key.Value;
            LastAction = action;
            var now = DateTimeOffset.UtcNow;
            var context = new ActionContext<PermissionAgentAccessAction>(
                Guid.NewGuid(),
                null,
                Guid.NewGuid(),
                Guid.NewGuid(),
                0,
                1,
                now.AddMinutes(1),
                PermissionActionDescriptors.AgentAccess.Key,
                TwoTierPermissionModule.ModuleIdValue,
                caller,
                action,
                ExtensionFeatureSet.Empty,
                new ActionPipelineSnapshot("test", [], [], 16));
            LastDecision = await _terminal.InvokeAsync(context, ct);
            return new HostActionOutcome<TResult>(
                ActionOutcomeKind.Completed,
                (TResult)(object)LastDecision);
        }
    }

    private sealed class PolicyHostActionEntry(TwoTierPermissionPolicy policy) : IHostActionEntry, IModuleCrossSidecarActionEntry
    {
        public async ValueTask<IActionOutcome<TResult>> InvokeAsync<TAction, TResult>(
            HostActionEntryRequest<TAction, TResult> request,
            IHostActionEntryTerminal<TAction, TResult> terminal,
            CancellationToken ct)
        {
            var result = request.Action switch
            {
                PermissionContextAccessAction action =>
                    await policy.EvaluateDetailedAsync(
                        action.Request with { Principal = request.Context.Caller },
                        ct),
                PermissionAgentAccessAction action =>
                    await policy.EvaluateAgentDetailedAsync(
                        request.Context.Caller,
                        action.Capability,
                        action.TargetAgentId,
                        ct),
                _ => throw new InvalidOperationException(
                    $"The test host does not support '{request.Descriptor.Key.Value}'."),
            };

            return new HostActionOutcome<TResult>(
                ActionOutcomeKind.Completed,
                (TResult)(object)result);
        }

        public async ValueTask<IActionOutcome<TResult>> InvokeNestedAsync<TParentAction, TAction, TResult>(
            HostActionEntryNestedRequest<TParentAction, TAction, TResult> request,
            IHostActionEntryTerminal<TAction, TResult> terminal,
            CancellationToken ct)
        {
            var result = request.Action switch
            {
                PermissionContextAccessAction action =>
                    await policy.EvaluateDetailedAsync(
                        action.Request with { Principal = request.ParentContext.Caller },
                        ct),
                PermissionAgentAccessAction action =>
                    await policy.EvaluateAgentDetailedAsync(
                        request.ParentContext.Caller,
                        action.Capability,
                        action.TargetAgentId,
                        ct),
                _ => throw new InvalidOperationException(
                    $"The test host does not support '{request.ActionKey.Value}'."),
            };

            return new HostActionOutcome<TResult>(
                ActionOutcomeKind.Completed,
                (TResult)(object)result);
        }

        public async ValueTask<IActionOutcome<TResult>> InvokeCrossSidecarAsync<TAction, TResult>(
            ModuleCrossSidecarActionEntryRequest<TAction, TResult> request,
            CancellationToken ct)
        {
            var result = request.Action switch
            {
                PermissionContextAccessAction action =>
                    await policy.EvaluateDetailedAsync(action.Request, ct),
                PermissionAgentAccessAction action =>
                    PermissionDecision.Allow(
                        "test_allowed",
                        1,
                        PermissionClearance.Independent),
                _ => throw new InvalidOperationException(
                    $"The test host does not support '{request.Descriptor.Key.Value}'."),
            };

            return new HostActionOutcome<TResult>(
                ActionOutcomeKind.Completed,
                (TResult)(object)result);
        }
    }

    private sealed class DelegatingContextActionGateway(
        ContextApiActionExecutor executor,
        IHostActionEntry hostEntry) : IContextActionGateway
    {
        public ValueTask<JsonElement> ExecuteAsync(
            HostActionEntryRequestContext hostContext,
            string operation,
            JsonElement payload,
            CancellationToken ct = default)
        {
            var action = new ContextApiAction(operation, payload);
            var context = new ActionContext<ContextApiAction>(
                hostContext.InvocationId,
                hostContext.ParentInvocationId,
                hostContext.TraceId,
                hostContext.IdempotencyKey,
                hostContext.Depth,
                hostContext.Attempt,
                hostContext.Deadline,
                ContextModule.ApiDescriptor.Key,
                ContextModule.ModuleIdValue,
                hostContext.Caller,
                action,
                hostContext.Features,
                new ActionPipelineSnapshot("test", [], [], 16))
            {
                HostActionEntry = hostEntry,
            };
            return executor.ExecuteAsync(context, ct);
        }
    }

    private sealed class RecordingContextGateway : IContextActionGateway
    {
        public List<HostActionEntryRequestContext> Contexts { get; } = [];
        public List<string> Operations { get; } = [];

        public ValueTask<JsonElement> ExecuteAsync(
            HostActionEntryRequestContext hostContext,
            string operation,
            JsonElement payload,
            CancellationToken ct = default)
        {
            Contexts.Add(hostContext);
            Operations.Add(operation);
            return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { accepted = true }));
        }
    }

    private sealed class InMemoryStorageGateway : IModuleStorageGateway
    {
        private sealed record Entry(JsonElement Value, JsonElement Indexes, long Revision);
        private readonly object _sync = new();
        private readonly Dictionary<(string Module, string Storage, string Key), Entry> _records = [];
        private readonly List<(string Storage, int Limit)> _queryLimits = [];
        private int _upsertCount;

        public int? FailAfterUpserts { get; set; }
        public Func<string, string, string, Task>? BeforeOperationAsync { get; set; }
        public int UpsertCount
        {
            get
            {
                lock (_sync)
                    return _upsertCount;
            }
        }

        public IReadOnlyList<(string Storage, int Limit)> QueryLimits
        {
            get
            {
                lock (_sync)
                    return _queryLimits.ToArray();
            }
        }

        public int Count(string moduleId, string storageName)
        {
            lock (_sync)
                return _records.Count(item => item.Key.Module == moduleId && item.Key.Storage == storageName);
        }

        public void Seed(
            string moduleId,
            string storageName,
            string key,
            object value,
            object indexes,
            long revision = 1)
        {
            lock (_sync)
            {
                _records[(moduleId, storageName, key)] = new(
                    JsonSerializer.SerializeToElement(value),
                    JsonSerializer.SerializeToElement(indexes),
                    revision);
            }
        }

        public IReadOnlyList<ModuleStorageContractDescriptor> ListContracts() => [];

        public async Task<JsonElement> InvokeAsync(
            string moduleId,
            string storageName,
            string operation,
            JsonElement parameters,
            CancellationToken ct = default)
        {
            if (BeforeOperationAsync is not null)
                await BeforeOperationAsync(moduleId, storageName, operation);
            var prefix = (moduleId, storageName);
            return operation switch
            {
                ModuleStorageOperations.Get => Get(prefix, parameters),
                ModuleStorageOperations.Upsert => Upsert(prefix, parameters),
                ModuleStorageOperations.Delete => Delete(prefix, parameters),
                ModuleStorageOperations.List => List(prefix),
                ModuleStorageOperations.Query => Query(prefix, parameters),
                ModuleStorageOperations.BatchUpsert => BatchUpsert(prefix, parameters),
                ModuleStorageOperations.BatchDelete => BatchDelete(prefix, parameters),
                _ => throw new NotSupportedException(operation),
            };
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
            lock (_sync)
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
        }

        private JsonElement Upsert((string Module, string Storage) prefix, JsonElement parameters)
        {
            lock (_sync)
            {
                if (FailAfterUpserts is { } limit && _upsertCount >= limit)
                    throw new IOException("Injected storage interruption.");
                _upsertCount++;
                var key = parameters.GetProperty("key").GetString()!;
                var id = (prefix.Module, prefix.Storage, key);
                if (parameters.TryGetProperty("expectedRevision", out var expectedRevision)
                    && expectedRevision.ValueKind == JsonValueKind.Number)
                {
                    var currentRevision = _records.TryGetValue(id, out var existingEntry)
                        ? existingEntry.Revision
                        : 0;
                    if (currentRevision != expectedRevision.GetInt64())
                        throw new InvalidOperationException("The expected storage revision is stale.");
                }
                var revision = _records.TryGetValue(id, out var current) ? current.Revision + 1 : 1;
                var indexes = parameters.TryGetProperty("indexes", out var index) ? index.Clone() : JsonSerializer.SerializeToElement(new { });
                _records[id] = new(parameters.GetProperty("value").Clone(), indexes, revision);
                return JsonSerializer.SerializeToElement(new { saved = true, revision });
            }
        }

        private JsonElement Delete((string Module, string Storage) prefix, JsonElement parameters)
        {
            lock (_sync)
            {
                var key = parameters.GetProperty("key").GetString()!;
                return JsonSerializer.SerializeToElement(new { deleted = _records.Remove((prefix.Module, prefix.Storage, key)) });
            }
        }

        private JsonElement List((string Module, string Storage) prefix)
        {
            lock (_sync)
            {
                return JsonSerializer.SerializeToElement(new
                {
                    records = _records.Where(item => item.Key.Module == prefix.Module && item.Key.Storage == prefix.Storage)
                        .Select(item => new { key = item.Key.Key, value = item.Value.Value, revision = item.Value.Revision, indexes = item.Value.Indexes }),
                });
            }
        }

        private JsonElement Query((string Module, string Storage) prefix, JsonElement parameters)
        {
            lock (_sync)
            {
                var queryLimit = -1;
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
                {
                    queryLimit = count;
                    records = records.Take(count).ToList();
                }
                _queryLimits.Add((prefix.Storage, queryLimit));
                return JsonSerializer.SerializeToElement(new
                {
                    records = records.Select(item => new { key = item.key, value = item.entry.Value, revision = item.entry.Revision, indexes = item.entry.Indexes }),
                });
            }
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
