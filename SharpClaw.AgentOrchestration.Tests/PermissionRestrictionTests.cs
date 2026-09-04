using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;

namespace SharpClaw.AgentOrchestration.Tests;

[TestFixture]
public sealed class PermissionRestrictionTests
{
    [Test]
    public void RestrictionPublishesOnlyOneRestrictionHook()
    {
        var graph = Compile(new RestrictionPackage());

        Assert.Multiple(() =>
        {
            Assert.That(graph.Contracts, Has.Count.EqualTo(1));
            Assert.That(graph.Contracts[0].ContractName, Is.EqualTo(AuthorizationProtocol.ContractName));
            Assert.That(graph.Contracts[0].IsExport, Is.False);
            Assert.That(graph.Actions, Is.Empty);
            Assert.That(graph.ActionEntries, Is.Empty);
            Assert.That(graph.ActionHooks, Has.Count.EqualTo(1));
            Assert.That(graph.ActionHooks[0].ActionKey, Is.EqualTo(AuthorizationProtocol.Evaluate.Key));
            Assert.That(graph.ActionHooks[0].HookId, Is.EqualTo("authorization.restriction.tenant-boundary"));
            Assert.That(graph.ActionHooks[0].RequestedCapabilities, Is.EqualTo(
                ActionInterceptionCapabilities.Inspect |
                ActionInterceptionCapabilities.Wrap));
        });

        var services = new ServiceCollection();
        foreach (var service in graph.Services)
            ((ICollection<ServiceDescriptor>)services).Add(service);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.That(scope.ServiceProvider.GetRequiredService<TenantRestriction>(), Is.Not.Null);
        Assert.That(
            scope.ServiceProvider.GetRequiredService<AuthorizationRestrictionHook<TenantRestriction>>(),
            Is.Not.Null);
    }

    [Test]
    public async Task PreserveCannotConvertProviderDenialToAllowance()
    {
        var denial = AuthorizationDecision.Deny("provider_denied", "The provider denies access.");
        var control = new RecordingControl(TestOutcome.Completed(denial));
        var restriction = new TenantRestriction(AuthorizationRestriction.Preserve());

        var outcome = await new AuthorizationRestrictionHook<TenantRestriction>(restriction)
            .InvokeAsync(CreateContext(), control, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Kind, Is.EqualTo(ActionOutcomeKind.Completed));
            Assert.That(outcome.Result, Is.EqualTo(denial));
            Assert.That(outcome.Result!.Allowed, Is.False);
            Assert.That(control.ProceedCalls, Is.EqualTo(1));
            Assert.That(control.FailCalls, Is.Zero);
        });
    }

    [Test]
    public async Task DenialStopsProviderAndReturnsDeniedDecision()
    {
        var control = new RecordingControl(TestOutcome.Completed(AuthorizationDecision.Allow()));
        var restriction = new TenantRestriction(AuthorizationRestriction.Deny(
            "tenant_denied",
            "The tenant restriction denies access."));
        var outcome = await new AuthorizationRestrictionHook<TenantRestriction>(restriction)
            .InvokeAsync(CreateContext(), control, CancellationToken.None);
        var entry = new HostAuthorizationEntry(new OutcomeHostActionEntry(outcome));

        var decision = await entry.EvaluateAsync(
            TestHostActionContext.Create(new RequestPrincipal("caller", IsAuthenticated: true)),
            CreateRequest());

        Assert.Multiple(() =>
        {
            Assert.That(control.ProceedCalls, Is.Zero);
            Assert.That(control.FailCalls, Is.EqualTo(1));
            Assert.That(decision, Is.EqualTo(AuthorizationDecision.Deny(
                "tenant_denied",
                "The tenant restriction denies access.")));
        });
    }

    [Test]
    public async Task IndependentRestrictionsComposeAsIntersection()
    {
        var providerCalls = 0;
        var context = CreateContext();
        var denyHook = new AuthorizationRestrictionHook<TenantRestriction>(
            new TenantRestriction(AuthorizationRestriction.Deny(
                "region_denied",
                "The region restriction denies access.")));
        var providerControl = new RecordingControl(_ =>
        {
            providerCalls++;
            return ValueTask.FromResult<IActionOutcome<AuthorizationDecision>>(
                TestOutcome.Completed(AuthorizationDecision.Allow("provider_allowed")));
        });
        var outerControl = new RecordingControl(token =>
            denyHook.InvokeAsync(context, providerControl, token));

        var outcome = await new AuthorizationRestrictionHook<TenantRestriction>(
                new TenantRestriction(AuthorizationRestriction.Preserve()))
            .InvokeAsync(context, outerControl, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Kind, Is.EqualTo(ActionOutcomeKind.Failed));
            Assert.That(outcome.Error!.Code, Is.EqualTo("authorization_restricted:region_denied"));
            Assert.That(providerCalls, Is.Zero);
            Assert.That(outerControl.ProceedCalls, Is.EqualTo(1));
            Assert.That(providerControl.FailCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public void CancellationStopsBeforeRestrictionEvaluation()
    {
        var restriction = new TenantRestriction(AuthorizationRestriction.Preserve());
        var control = new RecordingControl(TestOutcome.Completed(AuthorizationDecision.Allow()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await new AuthorizationRestrictionHook<TenantRestriction>(restriction)
                .InvokeAsync(CreateContext(), control, cancellation.Token));
        Assert.Multiple(() =>
        {
            Assert.That(restriction.Evaluations, Is.Zero);
            Assert.That(control.ProceedCalls, Is.Zero);
        });
    }

    [TestCase("")]
    [TestCase("INVALID")]
    [TestCase("contains space")]
    public void InvalidRestrictionIdentityFailsDuringConfiguration(string restrictionId)
    {
        var exception = Assert.Throws<ModuleGraphCompilationException>(() =>
            Compile(new InvalidRestrictionPackage(restrictionId)));
        Assert.That(exception!.Errors.Single().Code, Is.EqualTo("module_configuration_failed"));
    }

    private static ModuleContributionGraph Compile(ISharpClawModule package) =>
        SharpClawModuleCompiler.Compile(
            package,
            options: new ModuleCompilationOptions
            {
                HostingMode = ModuleHostingMode.OutOfProcess,
                RequireManifestRequests = false,
            });

    private static AuthorizationRequest CreateRequest() =>
        new("agents.read", new AuthorizationResource("agent", Guid.NewGuid().ToString("D")));

    private static ActionContext<AuthorizationRequest> CreateContext() =>
        new(
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            1,
            DateTimeOffset.UtcNow.AddMinutes(1),
            AuthorizationProtocol.Evaluate.Key,
            "authorization_provider",
            new RequestPrincipal("caller", IsAuthenticated: true),
            CreateRequest(),
            ExtensionFeatureSet.Empty,
            new ActionPipelineSnapshot("authorization-restriction-test", [], [], 16));

    private sealed class RestrictionPackage : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new(
            "tenant_restriction",
            "Tenant Restriction",
            "tenant_restriction");

        public void ConfigureServices(IServiceCollection services) =>
            services.AddAuthorizationRestriction<TenantRestriction>("tenant-boundary");
    }

    private sealed class InvalidRestrictionPackage(string restrictionId) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new(
            "invalid_restriction",
            "Invalid Restriction",
            "invalid_restriction");

        public void ConfigureServices(IServiceCollection services) =>
            services.AddAuthorizationRestriction<TenantRestriction>(restrictionId);
    }

    private sealed class TenantRestriction(
        AuthorizationRestriction result = default) : IAuthorizationRestriction
    {
        public int Evaluations { get; private set; }

        public ValueTask<AuthorizationRestriction> EvaluateAsync(
            ActionContext<AuthorizationRequest> context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Evaluations++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingControl : IActionControl<AuthorizationRequest, AuthorizationDecision>
    {
        private readonly Func<CancellationToken, ValueTask<IActionOutcome<AuthorizationDecision>>> _proceed;

        public RecordingControl(IActionOutcome<AuthorizationDecision> outcome)
            : this(_ => ValueTask.FromResult(outcome))
        {
        }

        public RecordingControl(
            Func<CancellationToken, ValueTask<IActionOutcome<AuthorizationDecision>>> proceed) =>
            _proceed = proceed;

        public int ProceedCalls { get; private set; }

        public int FailCalls { get; private set; }

        public ValueTask<IActionOutcome<AuthorizationDecision>> ProceedAsync(CancellationToken cancellationToken)
        {
            ProceedCalls++;
            return _proceed(cancellationToken);
        }

        public ValueTask<IActionOutcome<AuthorizationDecision>> ProceedWithInputAsync(
            ActionReplacement<AuthorizationRequest> replacement,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public IActionOutcome<AuthorizationDecision> ReplaceResult(
            AuthorizationDecision result,
            string reason) => throw new NotSupportedException();

        public IActionOutcome<AuthorizationDecision> Cancel(string code, string message) =>
            TestOutcome.Cancelled();

        public IActionOutcome<AuthorizationDecision> Fail(ExecutionError error)
        {
            FailCalls++;
            return TestOutcome.Failed(error);
        }

        public ValueTask<IActionOutcome<AuthorizationDecision>> DeferAsync(
            ActionDeferRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<IActionOutcome<AuthorizationDecision>> RepeatAsync(
            ActionRepeatRequest<AuthorizationRequest> request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class OutcomeHostActionEntry(IActionOutcome<AuthorizationDecision> outcome)
        : IHostActionEntry, IModuleCrossSidecarActionEntry
    {
        public ValueTask<IActionOutcome<TResult>> InvokeAsync<TAction, TResult>(
            HostActionEntryRequest<TAction, TResult> request,
            IHostActionEntryTerminal<TAction, TResult> terminal,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<IActionOutcome<TResult>> InvokeNestedAsync<TParentAction, TAction, TResult>(
            HostActionEntryNestedRequest<TParentAction, TAction, TResult> request,
            IHostActionEntryTerminal<TAction, TResult> terminal,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<IActionOutcome<TResult>> InvokeCrossSidecarAsync<TAction, TResult>(
            ModuleCrossSidecarActionEntryRequest<TAction, TResult> request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult((IActionOutcome<TResult>)(object)outcome);
    }

    private sealed class TestOutcome : IActionOutcome<AuthorizationDecision>
    {
        private TestOutcome(
            ActionOutcomeKind kind,
            AuthorizationDecision? result = null,
            ExecutionError? error = null)
        {
            Kind = kind;
            Result = result;
            Error = error;
        }

        public ActionOutcomeKind Kind { get; }
        public AuthorizationDecision? Result { get; }
        public ContinuationToken? Continuation => null;
        public ExecutionError? Error { get; }
        public ActionUncertainty? Uncertainty => null;

        public static TestOutcome Completed(AuthorizationDecision result) =>
            new(ActionOutcomeKind.Completed, result);

        public static TestOutcome Failed(ExecutionError error) =>
            new(ActionOutcomeKind.Failed, error: error);

        public static TestOutcome Cancelled() => new(ActionOutcomeKind.Cancelled);
    }
}
