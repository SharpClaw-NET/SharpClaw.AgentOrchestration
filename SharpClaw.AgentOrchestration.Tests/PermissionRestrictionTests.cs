using System.Text.Json;
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
            Assert.That(graph.ActionHooks[0].IsUntyped, Is.True);
            Assert.That(graph.ActionHooks[0].ActionType, Is.Null);
            Assert.That(graph.ActionHooks[0].ResultType, Is.Null);
            Assert.That(graph.ActionHooks[0].RequestedCapabilities, Is.EqualTo(
                ActionInterceptionCapabilities.Inspect |
                ActionInterceptionCapabilities.Wrap |
                ActionInterceptionCapabilities.Observe));
            Assert.That(
                graph.ActionHooks[0].RequestedCapabilities.HasFlag(
                    ActionInterceptionCapabilities.ReplaceResult),
                Is.False);
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
        var control = new RecordingControl(UntypedTestOutcome.Completed(denial));
        var restriction = new TenantRestriction(AuthorizationRestriction.Preserve());

        var outcome = await new AuthorizationRestrictionHook<TenantRestriction>(restriction)
            .InvokeAsync(CreateContext(), control, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Kind, Is.EqualTo(ActionOutcomeKind.Completed));
            Assert.That(outcome.Result!.Value.Deserialize<AuthorizationDecision>(), Is.EqualTo(denial));
            Assert.That(control.ProceedCalls, Is.EqualTo(1));
            Assert.That(control.FailCalls, Is.Zero);
        });
    }

    [Test]
    public async Task DenialStopsProviderAndReturnsDeniedDecision()
    {
        var control = new RecordingControl(UntypedTestOutcome.Completed(AuthorizationDecision.Allow()));
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
            return ValueTask.FromResult<IUntypedActionOutcome>(
                UntypedTestOutcome.Completed(AuthorizationDecision.Allow("provider_allowed")));
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
        var control = new RecordingControl(UntypedTestOutcome.Completed(AuthorizationDecision.Allow()));
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

    [Test]
    public async Task SerializedRestrictionReceivesExactAuthorityAndLineage()
    {
        var parentInvocationId = Guid.NewGuid();
        var traceId = Guid.NewGuid();
        var idempotencyKey = Guid.NewGuid();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        var caller = new RequestPrincipal(
            "tenant-user",
            "Tenant User",
            new HashSet<string>(["tenant-reader"], StringComparer.Ordinal),
            IsAuthenticated: true);
        var features = new ExtensionFeatureSet(
        [
            new ExtensionFeature(
                "tenant",
                1,
                "tenant-policy",
                64,
                JsonSerializer.SerializeToElement("alpha")),
        ]);
        var request = new AuthorizationRequest(
            "agents.read",
            new AuthorizationResource("agent", Guid.NewGuid().ToString("D")));
        var context = CreateContext() with
        {
            ParentInvocationId = parentInvocationId,
            TraceId = traceId,
            IdempotencyKey = idempotencyKey,
            Depth = 3,
            Attempt = 2,
            Deadline = deadline,
            Caller = caller,
            Features = features,
            Input = JsonSerializer.SerializeToElement(request),
        };
        var restriction = new TenantRestriction(AuthorizationRestriction.Preserve());

        var outcome = await new AuthorizationRestrictionHook<TenantRestriction>(restriction)
            .InvokeAsync(
                context,
                new RecordingControl(UntypedTestOutcome.Completed(AuthorizationDecision.Allow())),
                CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Kind, Is.EqualTo(ActionOutcomeKind.Completed));
            Assert.That(restriction.LastContext, Is.Not.Null);
            Assert.That(restriction.LastContext!.Request, Is.EqualTo(request));
            Assert.That(restriction.LastContext.Caller, Is.SameAs(caller));
            Assert.That(restriction.LastContext.Features, Is.SameAs(features));
            Assert.That(restriction.LastContext.InvocationId, Is.EqualTo(context.InvocationId));
            Assert.That(restriction.LastContext.ParentInvocationId, Is.EqualTo(parentInvocationId));
            Assert.That(restriction.LastContext.TraceId, Is.EqualTo(traceId));
            Assert.That(restriction.LastContext.IdempotencyKey, Is.EqualTo(idempotencyKey));
            Assert.That(restriction.LastContext.Depth, Is.EqualTo(3));
            Assert.That(restriction.LastContext.Attempt, Is.EqualTo(2));
            Assert.That(restriction.LastContext.Deadline, Is.EqualTo(deadline));
        });
    }

    [Test]
    public async Task InvalidSerializedRequestFailsBeforeRestrictionAndProvider()
    {
        var restriction = new TenantRestriction(AuthorizationRestriction.Preserve());
        var control = new RecordingControl(
            UntypedTestOutcome.Completed(AuthorizationDecision.Allow()));
        var context = CreateContext() with
        {
            Input = JsonSerializer.SerializeToElement(new AuthorizationRequest(
                "INVALID",
                new AuthorizationResource("agent", "resource"))),
        };

        var outcome = await new AuthorizationRestrictionHook<TenantRestriction>(restriction)
            .InvokeAsync(context, control, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Kind, Is.EqualTo(ActionOutcomeKind.Failed));
            Assert.That(outcome.Error!.Code, Is.EqualTo("authorization_restriction_invalid_input"));
            Assert.That(restriction.Evaluations, Is.Zero);
            Assert.That(control.ProceedCalls, Is.Zero);
            Assert.That(control.FailCalls, Is.EqualTo(1));
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

    private static UntypedActionContext CreateContext() =>
        new(
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            1,
            DateTimeOffset.UtcNow.AddMinutes(1),
            "authorization_provider",
            new RequestPrincipal("caller", IsAuthenticated: true),
            ExtensionFeatureSet.Empty,
            "authorization-restriction-test",
            new UntypedActionDescriptor(
                AuthorizationProtocol.Evaluate.Key,
                AuthorizationProtocol.Evaluate.Version,
                AuthorizationProtocol.Evaluate.Category,
                AuthorizationProtocol.Evaluate.Capabilities,
                AuthorizationProtocol.Evaluate.InputSchema!,
                AuthorizationProtocol.Evaluate.ResultSchema!,
                AuthorizationProtocol.Evaluate.ContainsSensitiveData),
            JsonSerializer.SerializeToElement(CreateRequest()));

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

        public AuthorizationRestrictionContext? LastContext { get; private set; }

        public ValueTask<AuthorizationRestriction> EvaluateAsync(
            AuthorizationRestrictionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Evaluations++;
            LastContext = context;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingControl : IUntypedActionControl
    {
        private readonly Func<CancellationToken, ValueTask<IUntypedActionOutcome>> _proceed;

        public RecordingControl(IUntypedActionOutcome outcome)
            : this(_ => ValueTask.FromResult(outcome))
        {
        }

        public RecordingControl(
            Func<CancellationToken, ValueTask<IUntypedActionOutcome>> proceed) =>
            _proceed = proceed;

        public int ProceedCalls { get; private set; }

        public int FailCalls { get; private set; }

        public ValueTask<IUntypedActionOutcome> ProceedAsync(CancellationToken cancellationToken)
        {
            ProceedCalls++;
            return _proceed(cancellationToken);
        }

        public ValueTask<IUntypedActionOutcome> ProceedWithInputAsync(
            JsonElement replacement,
            string reason,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public IUntypedActionOutcome ReplaceResult(
            JsonElement result,
            string reason) => throw new NotSupportedException();

        public IUntypedActionOutcome Cancel(string code, string message) =>
            UntypedTestOutcome.Cancelled();

        public IUntypedActionOutcome Fail(ExecutionError error)
        {
            FailCalls++;
            return UntypedTestOutcome.Failed(error);
        }

        public ValueTask<IUntypedActionOutcome> DeferAsync(
            ActionDeferRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<IUntypedActionOutcome> RepeatAsync(
            JsonElement replacement,
            string reason,
            TimeSpan? backoff,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class OutcomeHostActionEntry(IUntypedActionOutcome outcome)
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
            ValueTask.FromResult((IActionOutcome<TResult>)(object)ToTypedOutcome(outcome));

        private static TestOutcome ToTypedOutcome(IUntypedActionOutcome value) => value.Kind switch
        {
            ActionOutcomeKind.Completed => TestOutcome.Completed(
                value.Result!.Value.Deserialize<AuthorizationDecision>()!),
            ActionOutcomeKind.Failed => TestOutcome.Failed(value.Error!),
            ActionOutcomeKind.Cancelled => TestOutcome.Cancelled(),
            _ => throw new NotSupportedException(),
        };
    }

    private sealed class UntypedTestOutcome : IUntypedActionOutcome
    {
        private UntypedTestOutcome(
            ActionOutcomeKind kind,
            JsonElement? result = null,
            ExecutionError? error = null)
        {
            Kind = kind;
            Result = result;
            Error = error;
        }

        public ActionOutcomeKind Kind { get; }
        public JsonElement? Result { get; }
        public ContinuationToken? Continuation => null;
        public ExecutionError? Error { get; }
        public ActionUncertainty? Uncertainty => null;

        public static UntypedTestOutcome Completed(AuthorizationDecision result) =>
            new(ActionOutcomeKind.Completed, JsonSerializer.SerializeToElement(result));

        public static UntypedTestOutcome Failed(ExecutionError error) =>
            new(ActionOutcomeKind.Failed, error: error);

        public static UntypedTestOutcome Cancelled() => new(ActionOutcomeKind.Cancelled);
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
