using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;
using SharpClaw.Modules.AgentOrchestration.Contracts;
using SharpClaw.Modules.TwoTierPermission;

namespace SharpClaw.AgentOrchestration.Tests;

[TestFixture]
public sealed class PermissionAuthoringTests
{
    [Test]
    public void OnePolicyRegistrationPublishesTheCompleteProviderBoundary()
    {
        var graph = Compile(new ReplacementPermissionModule());

        Assert.Multiple(() =>
        {
            Assert.That(graph.Contracts.Count, Is.EqualTo(1));
            Assert.That(graph.Contracts.Single().ContractName,
                Is.EqualTo(AgentOrchestrationPermission.ContractName));
            Assert.That(graph.Contracts.Single().IsExport, Is.True);
            Assert.That(graph.Actions.Select(item => item.Descriptor.Key.Value), Is.EquivalentTo(
                new[]
                {
                    PermissionActionDescriptors.ContextAccess.Key.Value,
                    PermissionActionDescriptors.AgentAccess.Key.Value,
                }));
            Assert.That(graph.ActionEntries.Select(item => item.TerminalId), Is.EquivalentTo(
                new[]
                {
                    AgentOrchestrationPermission.ContextTerminalId,
                    AgentOrchestrationPermission.AgentTerminalId,
                }));
            Assert.That(graph.ActionEntries.Select(item => item.TerminalType), Is.EquivalentTo(
                new[]
                {
                    typeof(PermissionContextPolicyTerminal),
                    typeof(PermissionAgentPolicyTerminal),
                }));
        });

        var services = new ServiceCollection();
        foreach (var service in graph.Services)
            ((ICollection<ServiceDescriptor>)services).Add(service);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.That(
            scope.ServiceProvider.GetRequiredService<IAgentOrchestrationPermissionPolicy>(),
            Is.SameAs(scope.ServiceProvider.GetRequiredService<ReplacementPermissionPolicy>()));
    }

    [Test]
    public void ReplacementProviderMatchesTheBundledPermissionBoundary()
    {
        var replacement = Compile(new ReplacementPermissionModule());
        var bundled = Compile(new TwoTierPermissionModule());

        Assert.Multiple(() =>
        {
            Assert.That(
                replacement.Contracts.Select(item => (item.ContractName, item.ServiceType, item.IsExport)),
                Is.EqualTo(bundled.Contracts.Select(item =>
                    (item.ContractName, item.ServiceType, item.IsExport))));
            Assert.That(
                replacement.ActionEntries.Select(item => (item.Descriptor, item.TerminalId)),
                Is.EquivalentTo(bundled.ActionEntries
                    .Where(item => item.Descriptor.Key.Value.StartsWith("permission.", StringComparison.Ordinal)
                        && item.Descriptor.Key.Value != TwoTierPermissionModule.ApiDescriptor.Key.Value)
                    .Select(item => (item.Descriptor, item.TerminalId))));
        });
    }

    [Test]
    public async Task PolicyTerminalUsesTheAuthenticatedActionContext()
    {
        var policy = new ReplacementPermissionPolicy();
        var terminal = new PermissionContextPolicyTerminal(policy);
        var caller = new RequestPrincipal(
            "replacement-owner",
            Roles: new HashSet<string>(["custom-permission"]),
            IsAuthenticated: true);
        var channelId = Guid.NewGuid();
        var action = new PermissionContextAccessAction(new ContextAccessRequest(
            channelId,
            null,
            [],
            null,
            [],
            false));

        var decision = await terminal.InvokeAsync(CreateContext(
            caller,
            PermissionActionDescriptors.ContextAccess.Key,
            action));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Allowed, Is.True);
            Assert.That(policy.LastCaller, Is.EqualTo(caller));
            Assert.That(policy.LastChannelId, Is.EqualTo(channelId));
            Assert.That(typeof(ContextAccessRequest).GetProperty("Principal"), Is.Null);
        });
    }

    [Test]
    public void PolicyTerminalPreservesCancellation()
    {
        var policy = new ReplacementPermissionPolicy();
        var terminal = new PermissionAgentPolicyTerminal(policy);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var action = new PermissionAgentAccessAction("custom.manage", Guid.NewGuid());

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await terminal.InvokeAsync(
                CreateContext(
                    new RequestPrincipal("replacement-owner", IsAuthenticated: true),
                    PermissionActionDescriptors.AgentAccess.Key,
                    action),
                cancellation.Token));
        Assert.That(policy.AgentEvaluations, Is.Zero);
    }

    [Test]
    public void ConsumerRegistrationSelectsOnlyTheRequiredCheck()
    {
        var graph = Compile(new PermissionConsumerModule());

        Assert.Multiple(() =>
        {
            Assert.That(graph.Contracts.Count, Is.EqualTo(1));
            Assert.That(graph.Contracts.Single().ContractName,
                Is.EqualTo(AgentOrchestrationPermission.ContractName));
            Assert.That(graph.Contracts.Single().IsExport, Is.False);
            Assert.That(graph.Actions, Is.Empty);
            Assert.That(graph.ActionEntries, Is.Empty);
            Assert.That(graph.ActionHooks.Select(item => item.HandlerType), Is.EqualTo(
                new[] { typeof(PermissionAgentRelayHook) }));
            Assert.That(graph.ActionHooks.All(item =>
                item.ActionKey == PermissionActionDescriptors.AgentAccess.Key), Is.True);
            Assert.That(PermissionActionDescriptors.AgentAccess.Capabilities,
                Is.EqualTo(
                    ActionInterceptionCapabilities.Inspect |
                    ActionInterceptionCapabilities.Observe));
        });
    }

    [TestCase(AgentOrchestrationPermissionUse.None)]
    [TestCase((AgentOrchestrationPermissionUse)8)]
    public void InvalidConsumerSelectionFailsDuringModuleConfiguration(
        AgentOrchestrationPermissionUse use)
    {
        var exception = Assert.Throws<ModuleGraphCompilationException>(() => Compile(
            new InvalidPermissionConsumerModule(use)));

        Assert.That(exception!.Errors.Single().Code, Is.EqualTo("module_configuration_failed"));
    }

    private static ModuleContributionGraph Compile(ISharpClawModule module) =>
        SharpClawModuleCompiler.Compile(
            module,
            options: new ModuleCompilationOptions
            {
                HostingMode = ModuleHostingMode.OutOfProcess,
                RequireManifestRequests = false,
            });

    private static ActionContext<TAction> CreateContext<TAction>(
        RequestPrincipal caller,
        SharpClawActionKey key,
        TAction action) =>
        new(
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            1,
            DateTimeOffset.UtcNow.AddMinutes(1),
            key,
            ReplacementPermissionModule.ModuleId,
            caller,
            action,
            ExtensionFeatureSet.Empty,
            new ActionPipelineSnapshot("permission-authoring-test", [], [], 16));

    private sealed class ReplacementPermissionModule : ISharpClawModule
    {
        public const string ModuleId = "replacement_permission";

        public ModuleIdentity Identity { get; } = new(
            ModuleId,
            "Replacement Permission",
            "replacement_permission");

        public void Configure(ISharpClawModuleBuilder module) =>
            module.AddAgentOrchestrationPermissionPolicy<ReplacementPermissionPolicy>();

        public ValueTask StartAsync(ModuleStartContext context, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class PermissionConsumerModule : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new(
            "permission_consumer",
            "Permission Consumer",
            "permission_consumer");

        public void Configure(ISharpClawModuleBuilder module)
        {
            module.UseAgentOrchestrationPermission(AgentOrchestrationPermissionUse.Agents);
        }

        public ValueTask StartAsync(ModuleStartContext context, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class InvalidPermissionConsumerModule(
        AgentOrchestrationPermissionUse use) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new(
            "invalid_permission_consumer",
            "Invalid Permission Consumer",
            "invalid_permission_consumer");

        public void Configure(ISharpClawModuleBuilder module) =>
            module.UseAgentOrchestrationPermission(use);

        public ValueTask StartAsync(ModuleStartContext context, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class ReplacementPermissionPolicy : IAgentOrchestrationPermissionPolicy
    {
        public RequestPrincipal? LastCaller { get; private set; }

        public Guid? LastChannelId { get; private set; }

        public int AgentEvaluations { get; private set; }

        public ValueTask<AccessDecision> EvaluateContextAsync(
            ActionContext<PermissionContextAccessAction> context,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastCaller = context.Caller;
            LastChannelId = context.Action.Request.ChannelId;
            var allowed = context.Caller.Roles?.Contains("custom-permission") == true;
            return ValueTask.FromResult(allowed
                ? AccessDecision.Allow("custom_policy")
                : AccessDecision.Deny("custom_policy", "The custom policy denies access."));
        }

        public ValueTask<AccessDecision> EvaluateAgentAsync(
            ActionContext<PermissionAgentAccessAction> context,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            AgentEvaluations++;
            return ValueTask.FromResult(AccessDecision.Allow("custom_policy"));
        }
    }

}
