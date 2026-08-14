using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
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
                new[] { "policies", "grants", "approvals", "roles", "permission_sets" }));
            Assert.That(agentsBuilder.Storage.Items.Select(item => item.StorageName), Is.EquivalentTo(
                new[] { "agents", "skills", "memory", "costs", "synchronization", "agent_jobs", "agent_job_imports" }));
            Assert.That(permissionBuilder.Contracts.Exports.Select(item => item.ContractName),
                Does.Contain("sharpclaw.permission"));
            Assert.That(agentsBuilder.Contracts.Requires.Select(item => item.ContractName),
                Is.EquivalentTo(new[] { "sharpclaw.context", "sharpclaw.permission" }));
            Assert.That(contextBuilder.Services.Any(item => item.ServiceType == typeof(IContextActionExecutor)), Is.True);
            Assert.That(permissionBuilder.Services.Any(item => item.ServiceType == typeof(IPermissionActionExecutor)), Is.True);
            Assert.That(agentsBuilder.Services.Any(item => item.ServiceType == typeof(IAgentsActionExecutor)), Is.True);
            Assert.That(agentsBuilder.Services.Any(item => item.ServiceType == typeof(IAgentsJobActionExecutor)), Is.True);
            Assert.That(contextBuilder.Actions.Items.OfType<ActionDescriptor<ContextCreateThreadAction, ContextThreadRecord>>().Single().SafePoints,
                Is.Not.Empty);
            Assert.That(
                contextBuilder.Actions.Items
                    .OfType<ActionDescriptor<ContextCommitExchangeAction, bool>>()
                    .Single()
                    .Capabilities,
                Is.EqualTo(ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel));
            Assert.That(permissionBuilder.Actions.Items.OfType<ActionDescriptor<PermissionGrantAction, bool>>().Single().SafePoints,
                Is.Not.Empty);
            Assert.That(agentsBuilder.Actions.Items.OfType<ActionDescriptor<AgentsSaveSkillAction, SkillRecord>>().Single().SafePoints,
                Is.Not.Empty);
            Assert.That(agentsBuilder.Actions.Items.OfType<ActionDescriptor<AgentsRecordJobAction, AgentJob>>().Single().Key.Value,
                Is.EqualTo(AgentsModule.RecordAgentJobAction));
            Assert.That(agentsBuilder.Actions.Items.OfType<ActionDescriptor<AgentsAttachCanonicalJobAction, AgentJob>>().Single().Key.Value,
                Is.EqualTo(AgentsModule.AttachCanonicalJobAction));
            Assert.That(agentsBuilder.Actions.Items.OfType<ActionDescriptor<AgentsCompleteJobAction, AgentJob>>().Single().Key.Value,
                Is.EqualTo(AgentsModule.CompleteAgentJobAction));
            Assert.That(agentsBuilder.Actions.Items
                .OfType<ActionDescriptor<AgentsImportJobsAction, IReadOnlyList<AgentJob>>>()
                .Single().Key.Value, Is.EqualTo(AgentsModule.ImportAgentJobsAction));
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
            Assert.That(contextBuilder.Events.Items.OfType<EventDescriptor<ContextThreadChangedEvent>>(), Has.Exactly(1).Items);
            Assert.That(permissionBuilder.Events.Items.OfType<EventDescriptor<PermissionChangedEvent>>(), Has.Exactly(1).Items);
            Assert.That(agentsBuilder.Events.Items.OfType<EventDescriptor<MemoryChangedEvent>>(), Has.Exactly(1).Items);
            Assert.That(contextBuilder.Hooks.Items.Any(item => item.StartsWith("context.conversation.commit", StringComparison.Ordinal)), Is.True);
            Assert.That(permissionBuilder.Hooks.Items.Any(item => item.StartsWith("permission.grant", StringComparison.Ordinal)), Is.True);
            Assert.That(agentsBuilder.Hooks.Items.Any(item => item.StartsWith("agents.create", StringComparison.Ordinal)), Is.True);
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
                Assert.That(root.GetProperty("requestedHooks").GetArrayLength(), Is.GreaterThan(0));
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
                Assert.That(application.Endpoints.Items, Does.Contain(item.Contribution));
                Assert.That(item.Contribution.GetMethod("Map"), Is.Not.Null);
                Assert.That(item.Routes, Is.All.Not.Null);
                Assert.That(item.Routes, Is.All.Not.Empty);
            });
        }
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
        var contextBuilder = new RecordingBuilder();
        var permissionBuilder = new RecordingBuilder();
        var agentsBuilder = new RecordingBuilder();

        new ContextModule().Configure(contextBuilder);
        new TwoTierPermissionModule().Configure(permissionBuilder);
        new AgentsModule().Configure(agentsBuilder);

        Assert.Multiple(() =>
        {
            Assert.That(contextBuilder.Actions.Items.OfType<ActionDescriptor<ContextApiAction, JsonElement>>()
                .Select(item => item.Key.Value), Does.Contain("context.api.dispatch"));
            Assert.That(permissionBuilder.Actions.Items.OfType<ActionDescriptor<PermissionApiAction, JsonElement>>()
                .Select(item => item.Key.Value), Does.Contain("permission.api.dispatch"));
            Assert.That(agentsBuilder.Actions.Items.OfType<ActionDescriptor<AgentsApiAction, JsonElement>>()
                .Select(item => item.Key.Value), Does.Contain("agents.api.dispatch"));
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
        var action = new ContextApiAction(
            ContextApiOperations.ListChannels,
            JsonSerializer.SerializeToElement(new { }),
            RequestPrincipal.Anonymous);
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
            action.Caller,
            action,
            ExtensionFeatureSet.Empty,
            snapshot);

        var result = await pipeline.RunRequiredAsync(
            ContextModule.ApiDescriptor,
            context,
            (value, _) => ValueTask.FromResult(JsonSerializer.SerializeToElement(value.Operation)));

        Assert.Multiple(() =>
        {
            Assert.That(result.GetString(), Is.EqualTo(ContextApiOperations.ListChannels));
            Assert.That(dispatcher.RequiredCalls, Is.EqualTo(1));
            Assert.That(dispatcher.Snapshot, Is.SameAs(snapshot));
        });
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
        await policyStore.SaveAsync(new PermissionPolicyRecord(
            caller.SubjectId,
            [], ["read_cross_thread_history", ContextAccessCapabilities.CreateThread], [],
            PermissionClearance.Independent,
            RequireSourceOptIn: true,
            [], null, DateTimeOffset.UtcNow));

        var store = new ContextStore(gateway, permission);
        var current = new ContextChannelRecord(
            Guid.NewGuid(), "Current", agentId, null, [], [], false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var source = new ContextChannelRecord(
            Guid.NewGuid(), "Source", agentId, null, [], [], false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await store.SaveChannelAsync(current);
        await store.SaveChannelAsync(source);
        var thread = await store.CreateThreadAsync(caller, source.Id, "Source thread");
        await store.AppendMessageAsync(new ContextMessageRecord(
            Guid.NewGuid(), thread.Id, source.Id, "user", "retained history", "tester",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var contextGateway = new ContextActionGateway(
            new ContextApiActionExecutor(store));
        var handler = new ContextToolHandler(contextGateway);
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
        await policyStore.SaveAsync(new PermissionPolicyRecord(
            caller.SubjectId, [], ["read_cross_thread_history", ContextAccessCapabilities.CreateThread], [],
            PermissionClearance.Independent, true, [], null, DateTimeOffset.UtcNow));

        var store = new ContextStore(gateway, permission);
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
        var thread = await store.CreateThreadAsync(caller, source.Id, "Assigned thread", context.Id);

        var visible = await store.ListAccessibleThreadsAsync(caller, current.Id);

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

        var contextStore = new ContextStore(gateway, permission);
        var channel = new ContextChannelRecord(
            Guid.NewGuid(), "Executor Channel", agent.Id, null, [], [], false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await contextStore.SaveChannelAsync(channel);
        var contextExecutor = new ContextActionExecutor(contextStore);
        var thread = await contextExecutor.CreateThreadAsync(admin,
            new ContextCreateThreadAction(channel.Id, "Executor Thread"));
        Assert.That(await contextExecutor.CommitExchangeAsync(admin,
            new ContextCommitExchangeAction(thread.Id, "question", "answer")), Is.True);
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

    [Test]
    public async Task AgentsOwnAgentJobStateAndProjectCanonicalCompletion()
    {
        var gateway = new InMemoryStorageGateway();
        var catalog = new AgentsCatalog(gateway, new AllowAllAgentAccessPolicy());
        var executor = new AgentsJobActionExecutor(catalog);
        var caller = new RequestPrincipal("caller", IsAuthenticated: true);
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

        var recorded = await executor.RecordAsync(new(job, caller));
        var canonicalJobId = Guid.NewGuid();
        var attached = await executor.AttachCanonicalJobAsync(
            new(recorded.Id, canonicalJobId, caller));
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
            caller));
        var restartedCatalog = new AgentsCatalog(gateway, new AllowAllAgentAccessPolicy());
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
        var catalog = new AgentsCatalog(new InMemoryStorageGateway(), new AllowAllAgentAccessPolicy());
        var caller = new RequestPrincipal("caller", IsAuthenticated: true);
        var job = new AgentJob(
            Guid.NewGuid(), Guid.NewGuid(), "caller", "agent.respond", "conversation",
            "{}", "{}", "D:\\work", "queued", "Unset", 0, 0, [], null, null,
            "permission-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null, null, null);
        var recorded = await catalog.RecordAgentJobAsync(caller, job);
        var canonicalJobId = Guid.NewGuid();
        await catalog.AttachCanonicalJobAsync(caller, recorded.Id, canonicalJobId);

        Assert.Multiple(() =>
        {
            Assert.ThrowsAsync<InvalidOperationException>(() =>
                catalog.AttachCanonicalJobAsync(caller, recorded.Id, Guid.NewGuid()));
            Assert.ThrowsAsync<InvalidOperationException>(() =>
                catalog.ProjectCanonicalCompletionAsync(
                    caller, recorded.Id, Guid.NewGuid(), "failed", null, "unknown", 0, 0,
                    DateTimeOffset.UtcNow));
            Assert.ThrowsAsync<ArgumentException>(() =>
                catalog.RecordAgentJobAsync(caller, job with { ActionIdentity = " " }));
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
        var catalog = new AgentsCatalog(gateway, new AllowAllAgentAccessPolicy());
        var executor = new AgentsJobActionExecutor(catalog);
        var source = NeutralRecord(now, "paused");
        var snapshot = new CanonicalJobsImportSnapshot(
            "snapshot-2",
            now,
            [source],
            [new("legacy.agent", AgentJobHandlerKeys.Canonical, AgentJobPayloadCodecs.JsonV1)]);

        var imported = await executor.ImportAsync(new(
            snapshot,
            new RequestPrincipal("importer", IsAuthenticated: true)));
        var persisted = await new AgentsCatalog(gateway, new AllowAllAgentAccessPolicy())
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
        var catalog = new AgentsCatalog(gateway, new AllowAllAgentAccessPolicy());
        var source = NeutralRecord(now, "queued");
        var snapshot = new CanonicalJobsImportSnapshot(
            "snapshot-replay",
            now,
            [source],
            [new("legacy.agent", AgentJobHandlerKeys.Canonical, AgentJobPayloadCodecs.JsonV1)]);
        var caller = new RequestPrincipal("importer", IsAuthenticated: true);

        var first = await catalog.ImportAgentJobsAsync(caller, snapshot);
        var second = await catalog.ImportAgentJobsAsync(caller, snapshot);
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
        var catalogA = new AgentsCatalog(gateway, new AllowAllAgentAccessPolicy());
        var catalogB = new AgentsCatalog(gateway, new AllowAllAgentAccessPolicy());
        var imports = new[]
        {
            catalogA.ImportAgentJobsAsync(caller, snapshot),
            catalogB.ImportAgentJobsAsync(caller, snapshot),
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
        var catalogA = new AgentsCatalog(gateway, new AllowAllAgentAccessPolicy());
        var catalogB = new AgentsCatalog(gateway, new AllowAllAgentAccessPolicy());
        var imports = new[]
        {
            CaptureAsync(() => catalogA.ImportAgentJobsAsync(caller, snapshotA)),
            CaptureAsync(() => catalogB.ImportAgentJobsAsync(caller, snapshotB)),
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
        var catalog = new AgentsCatalog(gateway, new AllowAllAgentAccessPolicy());
        var source = NeutralRecord(now, "queued");
        var primary = new AgentJobActionMapping(
            "legacy.agent", AgentJobHandlerKeys.Canonical, AgentJobPayloadCodecs.JsonV1);
        var secondary = new AgentJobActionMapping(
            "legacy.unused", AgentJobHandlerKeys.Canonical, AgentJobPayloadCodecs.JsonV1);
        var snapshot = new CanonicalJobsImportSnapshot(
            "snapshot-mapping-authority", now, [source], [primary, secondary]);
        var caller = new RequestPrincipal("importer", IsAuthenticated: true);
        await catalog.ImportAgentJobsAsync(caller, snapshot);

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
            Assert.ThrowsAsync<AgentJobImportException>(() => catalog.ImportAgentJobsAsync(caller, changed));
            Assert.ThrowsAsync<AgentJobImportException>(() => catalog.ImportAgentJobsAsync(caller, missing));
            Assert.ThrowsAsync<AgentJobImportException>(() => catalog.ImportAgentJobsAsync(caller, extra));
            Assert.ThrowsAsync<AgentJobImportException>(() => catalog.ImportAgentJobsAsync(caller, reordered));
        });
    }

    [Test]
    public async Task AgentJobImportRejectsChangedSameIdentityAfterCompletion()
    {
        var now = DateTimeOffset.UtcNow;
        var gateway = new InMemoryStorageGateway();
        var catalog = new AgentsCatalog(gateway, new AllowAllAgentAccessPolicy());
        var source = NeutralRecord(now, "queued");
        var snapshot = new CanonicalJobsImportSnapshot(
            "snapshot-conflict",
            now,
            [source],
            [new("legacy.agent", AgentJobHandlerKeys.Canonical, AgentJobPayloadCodecs.JsonV1)]);
        var caller = new RequestPrincipal("importer", IsAuthenticated: true);
        await catalog.ImportAgentJobsAsync(caller, snapshot);

        var changed = new CanonicalJobsImportSnapshot(
            snapshot.SnapshotId,
            snapshot.CapturedAt,
            [source with { PayloadJson = """{"prompt":"changed"}""" }],
            snapshot.ActionMappings);

        Assert.ThrowsAsync<AgentJobImportException>(() =>
            catalog.ImportAgentJobsAsync(caller, changed));
        Assert.That(
            (await catalog.GetAgentJobAsync(source.SourceId))!.PayloadJson,
            Is.EqualTo("""{"prompt":"hello"}"""));
    }

    [Test]
    public async Task AgentJobImportRejectsMissingExtraAndReorderedReplay()
    {
        var now = DateTimeOffset.UtcNow;
        var gateway = new InMemoryStorageGateway();
        var catalog = new AgentsCatalog(gateway, new AllowAllAgentAccessPolicy());
        var sources = new[] { NeutralRecord(now, "queued"), NeutralRecord(now, "paused") };
        var mappings = new[]
        {
            new AgentJobActionMapping("legacy.agent", AgentJobHandlerKeys.Canonical, AgentJobPayloadCodecs.JsonV1),
        };
        var snapshot = new CanonicalJobsImportSnapshot("snapshot-shape", now, sources, mappings);
        var caller = new RequestPrincipal("importer", IsAuthenticated: true);
        await catalog.ImportAgentJobsAsync(caller, snapshot);

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
                catalog.ImportAgentJobsAsync(caller, missing));
            Assert.ThrowsAsync<AgentJobImportException>(() =>
                catalog.ImportAgentJobsAsync(caller, extra));
            Assert.ThrowsAsync<AgentJobImportException>(() =>
                catalog.ImportAgentJobsAsync(caller, reordered));
        });
    }

    [Test]
    public async Task AgentJobImportResumesAfterInterruptedWrite()
    {
        var now = DateTimeOffset.UtcNow;
        var gateway = new InMemoryStorageGateway { FailAfterUpserts = 2 };
        var catalog = new AgentsCatalog(gateway, new AllowAllAgentAccessPolicy());
        var sources = new[] { NeutralRecord(now, "queued"), NeutralRecord(now, "paused") };
        var snapshot = new CanonicalJobsImportSnapshot(
            "snapshot-interrupted",
            now,
            sources,
            [new("legacy.agent", AgentJobHandlerKeys.Canonical, AgentJobPayloadCodecs.JsonV1)]);
        var caller = new RequestPrincipal("importer", IsAuthenticated: true);

        Assert.ThrowsAsync<IOException>(() =>
            catalog.ImportAgentJobsAsync(caller, snapshot));
        var interrupted = await catalog.GetAgentJobImportStateAsync(snapshot.SnapshotId);
        Assert.That(interrupted, Is.Not.Null);
        Assert.That(interrupted!.Completed, Is.False);
        Assert.That(await catalog.GetAgentJobAsync(sources[0].SourceId), Is.Not.Null);
        Assert.That(await catalog.GetAgentJobAsync(sources[1].SourceId), Is.Null);

        gateway.FailAfterUpserts = null;
        var resumed = await catalog.ImportAgentJobsAsync(caller, snapshot);
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

    [Test]
    public async Task DirectChatAndStoreCommitRequireContextAuthorization()
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

        var store = new ContextStore(gateway, permission);
        var resolver = new ContextConversationResolver(store);
        var selection = await resolver.ResolveAsync(
            new ChatTurnInput("hello", Caller: caller),
            default);
        Assert.That(selection.Created, Is.True);

        var anonymous = new RequestPrincipal("", IsAuthenticated: false);
        Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await resolver.ResolveAsync(new ChatTurnInput("blocked", Caller: anonymous), default));

        var denied = new RequestPrincipal(Guid.NewGuid().ToString("D"));
        Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await resolver.ResolveAsync(
                new ChatTurnInput("blocked", selection.ConversationId, denied),
                default));

        var turn = new ChatTurnContext(
            Guid.NewGuid(),
            new ChatTurnInput("hello", selection.ConversationId, caller),
            selection);
        var contributor = new ContextHistoryContributor(store);
        var contribution = await contributor.ContributeAsync(
            new ChatContextRequest(
                selection.ConversationId,
                new ChatProfile("test", Guid.NewGuid()),
                [],
                turn),
            default);
        Assert.That(contribution.Messages, Is.Empty);

        await store.CommitExchangeAsync(
            new ChatExchange(
                turn,
                "hello",
                new ChatCompletionResult { Content = "answer" }),
            default);
        Assert.That((await store.ReadAllMessagesAsync(selection.ConversationId)).Select(item => item.Content),
            Is.EquivalentTo(new[] { "hello", "answer" }));

        var deniedExchange = new ChatExchange(
            turn with
            {
                Input = turn.Input with { Caller = denied },
            },
            "blocked",
            new ChatCompletionResult { Content = "blocked" });
        Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await store.CommitExchangeAsync(deniedExchange, default));
        Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await store.LoadHistoryAsync(selection.ConversationId, default));
        Assert.That((await store.ReadAllMessagesAsync(selection.ConversationId)).Count, Is.EqualTo(2));
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
        public List<Type> Items { get; } = [];

        public void Add<TContribution>() => Items.Add(typeof(TContribution));
    }

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
            Func<TAction, CancellationToken, ValueTask<TResult>> terminal,
            ActionPipelineSnapshot snapshot,
            CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public async ValueTask<TResult> RunRequiredAsync<TAction, TResult>(
            ActionDescriptor<TAction, TResult> descriptor,
            TAction action,
            Func<TAction, CancellationToken, ValueTask<TResult>> terminal,
            ActionPipelineSnapshot snapshot,
            CancellationToken ct)
        {
            RequiredCalls++;
            Snapshot = snapshot;
            return await terminal(action, ct);
        }
    }

    private sealed class AllowAllAgentAccessPolicy : IAgentAccessPolicy
    {
        public ValueTask<ContextAccessDecision> EvaluateAgentAsync(
            RequestPrincipal principal,
            string capability,
            Guid? targetAgentId,
            CancellationToken ct = default) =>
            ValueTask.FromResult(ContextAccessDecision.Allow());
    }

    private sealed class InMemoryStorageGateway : IModuleStorageGateway
    {
        private sealed record Entry(JsonElement Value, JsonElement Indexes, long Revision);
        private readonly object _sync = new();
        private readonly Dictionary<(string Module, string Storage, string Key), Entry> _records = [];
        private int _upsertCount;

        public int? FailAfterUpserts { get; set; }
        public Func<string, string, string, Task>? BeforeOperationAsync { get; set; }

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
