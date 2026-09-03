using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.AgentOrchestration.Tests;

[TestFixture]
public sealed class PermissionRestrictionTests
{
    [Test]
    public void RestrictionRegistrationPublishesOnlyRestrictionHooks()
    {
        var graph = Compile(new RestrictionModule());

        Assert.Multiple(() =>
        {
            Assert.That(graph.Contracts.Count, Is.EqualTo(1));
            Assert.That(graph.Contracts.Single().ContractName,
                Is.EqualTo(AgentOrchestrationPermission.ContractName));
            Assert.That(graph.Contracts.Single().IsExport, Is.False);
            Assert.That(graph.Actions, Is.Empty);
            Assert.That(graph.ActionEntries, Is.Empty);
            Assert.That(graph.ActionHooks.Select(item => item.ActionKey?.Value), Is.EquivalentTo(
                new[]
                {
                    PermissionActionDescriptors.ContextAccess.Key.Value,
                    PermissionActionDescriptors.AgentAccess.Key.Value,
                }));
            Assert.That(graph.ActionHooks.Select(item => item.HookId), Is.EquivalentTo(
                new[]
                {
                    "permission.restriction.tenant-boundary.context",
                    "permission.restriction.tenant-boundary.agent",
                }));
            Assert.That(graph.ActionHooks.All(item =>
                item.RequestedCapabilities == (
                    ActionInterceptionCapabilities.Inspect |
                    ActionInterceptionCapabilities.Wrap)), Is.True);
            Assert.That(graph.ActionHooks.All(item =>
                !item.RequestedCapabilities.HasFlag(ActionInterceptionCapabilities.ReplaceInput) &&
                !item.RequestedCapabilities.HasFlag(ActionInterceptionCapabilities.ReplaceResult) &&
                !item.RequestedCapabilities.HasFlag(ActionInterceptionCapabilities.Repeat) &&
                !item.RequestedCapabilities.HasFlag(ActionInterceptionCapabilities.Defer)), Is.True);
        });

        var services = new ServiceCollection();
        foreach (var service in graph.Services)
            ((ICollection<ServiceDescriptor>)services).Add(service);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Multiple(() =>
        {
            Assert.That(scope.ServiceProvider.GetRequiredService<TenantRestriction>(), Is.Not.Null);
            Assert.That(
                scope.ServiceProvider.GetRequiredService<
                    PermissionContextRestrictionHook<TenantRestriction>>(),
                Is.Not.Null);
            Assert.That(
                scope.ServiceProvider.GetRequiredService<
                    PermissionAgentRestrictionHook<TenantRestriction>>(),
                Is.Not.Null);
        });
    }

    [Test]
    public void RestrictionManifestMustRequestOnlyInspectAndWrap()
    {
        var module = new RestrictionModule();
        var manifest = new ModuleManifest(
            module.Identity.Id,
            module.Identity.DisplayName,
            "0.5.0-beta.1",
            module.Identity.ToolPrefix,
            "TenantRestriction.dll",
            "0.5.0-beta.1",
            Runtime: ModuleManifestRuntimeInfo.DotNet,
            ModuleType: typeof(RestrictionModule).FullName,
            HostMode: ModuleManifestRuntimeInfo.HostModeSidecar,
            Requires:
            [
                new ModuleManifestContractRef(
                    AgentOrchestrationPermission.ContractName,
                    typeof(PermissionModuleContract).FullName),
            ],
            RequestedHooks:
            [
                new ModuleManifestHookRequest(
                    PermissionActionDescriptors.ContextAccess.Key.Value,
                    ["Inspect", "Wrap"]),
                new ModuleManifestHookRequest(
                    PermissionActionDescriptors.AgentAccess.Key.Value,
                    ["Inspect", "Wrap"]),
            ]);

        var graph = SharpClawModuleCompiler.Compile(
            module,
            manifest,
            new ModuleCompilationOptions
            {
                HostingMode = ModuleHostingMode.OutOfProcess,
                RequireManifestRequests = true,
            });

        Assert.That(graph.ActionHooks, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task PreserveCannotConvertTheProviderDenialToAnAllowance()
    {
        var providerDenial = AccessDecision.Deny(
            "provider_denied",
            "The provider denies access.");
        var control = new RecordingControl<PermissionAgentAccessAction>(
            TestOutcome<AccessDecision>.Completed(providerDenial));
        var restriction = new TenantRestriction
        {
            AgentResult = PermissionRestriction.Preserve(),
        };
        var context = CreateContext(
            new RequestPrincipal("caller-a", IsAuthenticated: true),
            new PermissionAgentAccessAction("agents.read", Guid.NewGuid()));

        var outcome = await new PermissionAgentRestrictionHook<TenantRestriction>(restriction)
            .InvokeAsync(context, control, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Kind, Is.EqualTo(ActionOutcomeKind.Completed));
            Assert.That(outcome.Result, Is.SameAs(providerDenial));
            Assert.That(outcome.Result!.Allowed, Is.False);
            Assert.That(control.ProceedCalls, Is.EqualTo(1));
            Assert.That(control.FailCalls, Is.Zero);
        });
    }

    [Test]
    public async Task DenialStopsTheProviderTerminalAndMapsToAnAccessDecision()
    {
        var control = new RecordingControl<PermissionContextAccessAction>(
            TestOutcome<AccessDecision>.Completed(AccessDecision.Allow()));
        var restriction = new TenantRestriction
        {
            ContextResult = PermissionRestriction.Deny(
                "tenant_denied",
                "The tenant restriction denies access."),
        };
        var context = CreateContext(
            new RequestPrincipal("caller-b", IsAuthenticated: true),
            new PermissionContextAccessAction(CreateRequest()));

        var outcome = await new PermissionContextRestrictionHook<TenantRestriction>(restriction)
            .InvokeAsync(context, control, CancellationToken.None);
        var entry = new HostPermissionActionEntry(new OutcomeHostActionEntry(outcome));
        var decision = await entry.EvaluateContextAsync(
            TestHostActionContext.Create(context.Caller),
            context.Action.Request);

        Assert.Multiple(() =>
        {
            Assert.That(control.ProceedCalls, Is.Zero);
            Assert.That(control.FailCalls, Is.EqualTo(1));
            Assert.That(outcome.Kind, Is.EqualTo(ActionOutcomeKind.Failed));
            Assert.That(outcome.Error!.Code,
                Is.EqualTo("permission_restricted:tenant_denied"));
            Assert.That(decision, Is.EqualTo(AccessDecision.Deny(
                "tenant_denied",
                "The tenant restriction denies access.")));
        });
    }

    [Test]
    public async Task IndependentRestrictionsComposeAsAnIntersection()
    {
        var providerCalls = 0;
        var providerDecision = AccessDecision.Allow("provider_allowed");
        var context = CreateContext(
            new RequestPrincipal("caller-c", IsAuthenticated: true),
            new PermissionAgentAccessAction("agents.manage", Guid.NewGuid()));
        var deny = new TenantRestriction
        {
            AgentResult = PermissionRestriction.Deny(
                "region_denied",
                "The regional restriction denies access."),
        };
        var preserve = new TenantRestriction
        {
            AgentResult = PermissionRestriction.Preserve(),
        };
        var denyHook = new PermissionAgentRestrictionHook<TenantRestriction>(deny);
        var denyControl = new RecordingControl<PermissionAgentAccessAction>(
            _ =>
            {
                providerCalls++;
                return ValueTask.FromResult<IActionOutcome<AccessDecision>>(
                    TestOutcome<AccessDecision>.Completed(providerDecision));
            });
        var preserveControl = new RecordingControl<PermissionAgentAccessAction>(
            ct => denyHook.InvokeAsync(context, denyControl, ct));

        var outcome = await new PermissionAgentRestrictionHook<TenantRestriction>(preserve)
            .InvokeAsync(context, preserveControl, CancellationToken.None);
        var entry = new HostPermissionActionEntry(new OutcomeHostActionEntry(outcome));
        var decision = await entry.EvaluateAgentAsync(
            TestHostActionContext.Create(context.Caller),
            context.Action.Capability,
            context.Action.TargetAgentId);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Code, Is.EqualTo("region_denied"));
            Assert.That(preserveControl.ProceedCalls, Is.EqualTo(1));
            Assert.That(denyControl.ProceedCalls, Is.Zero);
            Assert.That(providerCalls, Is.Zero);
        });
    }

    [Test]
    public async Task RestrictionReceivesUnchangedCallerAndFeatures()
    {
        var caller = new RequestPrincipal(
            "caller-d",
            "Caller D",
            new HashSet<string>(["tenant-reader"]),
            true);
        var features = new ExtensionFeatureSet([
            new ExtensionFeature(
                "tenant.scope",
                1,
                "host",
                256,
                JsonSerializer.SerializeToElement(new { tenant = "north" })),
        ]);
        var restriction = new TenantRestriction();
        var context = CreateContext(
            caller,
            new PermissionContextAccessAction(CreateRequest()),
            features);
        var control = new RecordingControl<PermissionContextAccessAction>(
            TestOutcome<AccessDecision>.Completed(AccessDecision.Allow()));

        await new PermissionContextRestrictionHook<TenantRestriction>(restriction)
            .InvokeAsync(context, control, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(restriction.LastCaller, Is.SameAs(caller));
            Assert.That(restriction.LastFeatures, Is.SameAs(features));
            Assert.That(restriction.ContextEvaluations, Is.EqualTo(1));
        });
    }

    [Test]
    public void CallerCancellationStopsBeforeRestrictionEvaluation()
    {
        var restriction = new TenantRestriction();
        var context = CreateContext(
            new RequestPrincipal("caller-e", IsAuthenticated: true),
            new PermissionAgentAccessAction("agents.read", null));
        var control = new RecordingControl<PermissionAgentAccessAction>(
            TestOutcome<AccessDecision>.Completed(AccessDecision.Allow()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await new PermissionAgentRestrictionHook<TenantRestriction>(restriction)
                .InvokeAsync(context, control, cancellation.Token));
        Assert.Multiple(() =>
        {
            Assert.That(restriction.AgentEvaluations, Is.Zero);
            Assert.That(control.ProceedCalls, Is.Zero);
            Assert.That(control.FailCalls, Is.Zero);
        });
    }

    [Test]
    public void NonRestrictionFailureDoesNotBecomeADenial()
    {
        var outcome = TestOutcome<AccessDecision>.Failed(new ExecutionError(
            "permission_provider_failed",
            "The permission provider failed."));
        var entry = new HostPermissionActionEntry(new OutcomeHostActionEntry(outcome));

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await entry.EvaluateAgentAsync(
                TestHostActionContext.Create(
                    new RequestPrincipal("caller-f", IsAuthenticated: true)),
                "agents.read",
                null));
    }

    [TestCase("")]
    [TestCase("Tenant")]
    [TestCase("tenant:scope")]
    public void RestrictionIdentifiersRejectUnstableValues(string restrictionId)
    {
        var exception = Assert.Throws<ModuleGraphCompilationException>(() =>
            Compile(new InvalidRestrictionModule(restrictionId)));

        Assert.That(exception!.Errors.Single().Code,
            Is.EqualTo("module_configuration_failed"));
    }

    private static ModuleContributionGraph Compile(ISharpClawModule module) =>
        SharpClawModuleCompiler.Compile(
            module,
            options: new ModuleCompilationOptions
            {
                HostingMode = ModuleHostingMode.OutOfProcess,
                RequireManifestRequests = false,
            });

    private static ContextAccessRequest CreateRequest() => new(
        Guid.NewGuid(),
        null,
        [],
        null,
        [],
        false);

    private static ActionContext<TAction> CreateContext<TAction>(
        RequestPrincipal caller,
        TAction action,
        ExtensionFeatureSet? features = null) =>
        new(
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            1,
            DateTimeOffset.UtcNow.AddMinutes(1),
            action is PermissionContextAccessAction
                ? PermissionActionDescriptors.ContextAccess.Key
                : PermissionActionDescriptors.AgentAccess.Key,
            RestrictionModule.ModuleId,
            caller,
            action,
            features ?? ExtensionFeatureSet.Empty,
            new ActionPipelineSnapshot("permission-restriction-test", [], [], 16));

    private sealed class RestrictionModule : ISharpClawModule
    {
        public const string ModuleId = "tenant_restriction";

        public ModuleIdentity Identity { get; } = new(
            ModuleId,
            "Tenant Restriction",
            "tenant_restriction");

        public void Configure(ISharpClawModuleBuilder module) =>
            module.AddAgentOrchestrationPermissionRestriction<TenantRestriction>(
                "tenant-boundary");

        public ValueTask StartAsync(ModuleStartContext context, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class InvalidRestrictionModule(string restrictionId) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new(
            "invalid_restriction",
            "Invalid Restriction",
            "invalid_restriction");

        public void Configure(ISharpClawModuleBuilder module) =>
            module.AddAgentOrchestrationPermissionRestriction<TenantRestriction>(restrictionId);

        public ValueTask StartAsync(ModuleStartContext context, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class TenantRestriction : IAgentOrchestrationPermissionRestriction
    {
        public PermissionRestriction ContextResult { get; init; } =
            PermissionRestriction.Preserve();

        public PermissionRestriction AgentResult { get; init; } =
            PermissionRestriction.Preserve();

        public RequestPrincipal? LastCaller { get; private set; }

        public ExtensionFeatureSet? LastFeatures { get; private set; }

        public int ContextEvaluations { get; private set; }

        public int AgentEvaluations { get; private set; }

        public ValueTask<PermissionRestriction> RestrictContextAsync(
            ActionContext<PermissionContextAccessAction> context,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ContextEvaluations++;
            LastCaller = context.Caller;
            LastFeatures = context.Features;
            return ValueTask.FromResult(ContextResult);
        }

        public ValueTask<PermissionRestriction> RestrictAgentAsync(
            ActionContext<PermissionAgentAccessAction> context,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            AgentEvaluations++;
            LastCaller = context.Caller;
            LastFeatures = context.Features;
            return ValueTask.FromResult(AgentResult);
        }
    }

    private sealed class RecordingControl<TAction> : IActionControl<TAction, AccessDecision>
    {
        private readonly Func<CancellationToken, ValueTask<IActionOutcome<AccessDecision>>> _proceed;

        public RecordingControl(IActionOutcome<AccessDecision> outcome)
            : this(_ => ValueTask.FromResult(outcome))
        {
        }

        public RecordingControl(
            Func<CancellationToken, ValueTask<IActionOutcome<AccessDecision>>> proceed) =>
            _proceed = proceed;

        public int ProceedCalls { get; private set; }

        public int FailCalls { get; private set; }

        public ValueTask<IActionOutcome<AccessDecision>> ProceedAsync(CancellationToken ct)
        {
            ProceedCalls++;
            return _proceed(ct);
        }

        public ValueTask<IActionOutcome<AccessDecision>> ProceedWithInputAsync(
            ActionReplacement<TAction> replacement,
            CancellationToken ct) =>
            throw new AssertionException("A restriction cannot replace action input.");

        public IActionOutcome<AccessDecision> ReplaceResult(
            AccessDecision result,
            string reason) =>
            throw new AssertionException("A restriction cannot replace an access decision.");

        public IActionOutcome<AccessDecision> Cancel(string code, string message) =>
            throw new AssertionException("A restriction uses one stable denial boundary.");

        public IActionOutcome<AccessDecision> Fail(ExecutionError error)
        {
            FailCalls++;
            return TestOutcome<AccessDecision>.Failed(error);
        }

        public ValueTask<IActionOutcome<AccessDecision>> DeferAsync(
            ActionDeferRequest request,
            CancellationToken ct) =>
            throw new AssertionException("A restriction cannot defer a permission decision.");

        public ValueTask<IActionOutcome<AccessDecision>> RepeatAsync(
            ActionRepeatRequest<TAction> request,
            CancellationToken ct) =>
            throw new AssertionException("A restriction cannot repeat a permission decision.");
    }

    private sealed class OutcomeHostActionEntry(IActionOutcome<AccessDecision> outcome)
        : IHostActionEntry, IModuleCrossSidecarActionEntry
    {
        public ValueTask<IActionOutcome<TResult>> InvokeAsync<TAction, TResult>(
            HostActionEntryRequest<TAction, TResult> request,
            IHostActionEntryTerminal<TAction, TResult> terminal,
            CancellationToken ct) =>
            Return<TResult>();

        public ValueTask<IActionOutcome<TResult>> InvokeNestedAsync<TParentAction, TAction, TResult>(
            HostActionEntryNestedRequest<TParentAction, TAction, TResult> request,
            IHostActionEntryTerminal<TAction, TResult> terminal,
            CancellationToken ct) =>
            Return<TResult>();

        public ValueTask<IActionOutcome<TResult>> InvokeCrossSidecarAsync<TAction, TResult>(
            ModuleCrossSidecarActionEntryRequest<TAction, TResult> request,
            CancellationToken ct) =>
            Return<TResult>();

        private ValueTask<IActionOutcome<TResult>> Return<TResult>() =>
            ValueTask.FromResult<IActionOutcome<TResult>>(
                new TestOutcome<TResult>(
                    outcome.Kind,
                    outcome.Result is null ? default! : (TResult)(object)outcome.Result,
                    outcome.Error));
    }

    private sealed class TestOutcome<TResult>(
        ActionOutcomeKind kind,
        TResult result,
        ExecutionError? error = null) : IActionOutcome<TResult>
    {
        public ActionOutcomeKind Kind { get; } = kind;

        public TResult Result { get; } = result;

        public ContinuationToken? Continuation => null;

        public ExecutionError? Error { get; } = error;

        public ActionUncertainty? Uncertainty => null;

        public static TestOutcome<TResult> Completed(TResult result) =>
            new(ActionOutcomeKind.Completed, result);

        public static TestOutcome<TResult> Failed(ExecutionError error) =>
            new(ActionOutcomeKind.Failed, default!, error);
    }
}
