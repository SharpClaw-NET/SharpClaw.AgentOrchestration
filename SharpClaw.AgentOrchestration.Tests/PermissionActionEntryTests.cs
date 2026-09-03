using NUnit.Framework;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.AgentOrchestration.Tests;

[TestFixture]
public sealed class PermissionActionEntryTests
{
    [Test]
    public async Task ContextAccessUsesTheHostEntryWithANeutralRequest()
    {
        var host = new RecordingHostActionEntry
        {
            Result = AccessDecision.Allow("context_allowed"),
        };
        var entry = new HostPermissionActionEntry(host);
        var caller = new RequestPrincipal("agent-1", IsAuthenticated: true);
        var hostContext = TestHostActionContext.Create(caller, HostActionEntryIngress.CrossModule);
        var request = new ContextAccessRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            [Guid.NewGuid()],
            Guid.NewGuid(),
            [Guid.NewGuid()],
            true,
            Guid.NewGuid(),
            ContextAccessCapabilities.ReadHistory);

        var decision = await entry.EvaluateContextAsync(hostContext, request);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Allowed, Is.True);
            Assert.That(decision.Code, Is.EqualTo("context_allowed"));
            Assert.That(host.ContextCrossSidecarRequest, Is.Not.Null);
            Assert.That(host.ContextCrossSidecarRequest!.Descriptor, Is.SameAs(PermissionActionDescriptors.ContextAccess));
            Assert.That(host.ContextCrossSidecarRequest.Action.Request.ChannelId, Is.EqualTo(request.ChannelId));
            Assert.That(typeof(ContextAccessRequest).GetProperty("Principal"), Is.Null);
            Assert.That(host.CrossSidecarKeys, Is.EqualTo(["permission.context-access"]));
        });
    }

    [Test]
    public async Task AgentAccessMapsACompletedDenial()
    {
        var host = new RecordingHostActionEntry
        {
            Result = AccessDecision.Deny(
                "capability_denied",
                "The capability is not assigned."),
        };
        var entry = new HostPermissionActionEntry(host);
        var caller = new RequestPrincipal("agent-2", IsAuthenticated: true);
        var hostContext = TestHostActionContext.Create(caller, HostActionEntryIngress.CrossModule);
        var targetAgentId = Guid.NewGuid();

        var decision = await entry.EvaluateAgentAsync(
            hostContext,
            "manage_agents",
            targetAgentId);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Code, Is.EqualTo("capability_denied"));
            Assert.That(host.AgentCrossSidecarRequest, Is.Not.Null);
            Assert.That(host.AgentCrossSidecarRequest!.Descriptor, Is.SameAs(PermissionActionDescriptors.AgentAccess));
            Assert.That(host.AgentCrossSidecarRequest.Action.Capability, Is.EqualTo("manage_agents"));
            Assert.That(host.AgentCrossSidecarRequest.Action.TargetAgentId, Is.EqualTo(targetAgentId));
            Assert.That(host.CrossSidecarKeys, Is.EqualTo(["permission.agent-access"]));
        });
    }

    [TestCase(ActionOutcomeKind.Deferred)]
    [TestCase(ActionOutcomeKind.Failed)]
    [TestCase(ActionOutcomeKind.Uncertain)]
    public void NonTerminalPermissionOutcomesFailClosed(ActionOutcomeKind outcomeKind)
    {
        var host = new RecordingHostActionEntry
        {
            OutcomeKind = outcomeKind,
            Error = new ExecutionError("permission_failed", "The permission action failed.", false, new Dictionary<string, string>()),
            Uncertainty = new ActionUncertainty(
                "permission_uncertain",
                "The permission action has uncertain execution.",
                ActionExecutionStage.TerminalReturned,
                string.Empty,
                null!,
                DateTimeOffset.UtcNow),
        };
        var entry = new HostPermissionActionEntry(host);
        var hostContext = TestHostActionContext.Create(new RequestPrincipal("agent-3", IsAuthenticated: true));

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await entry.EvaluateAgentAsync(
                hostContext,
                "read_agents",
                null));
    }

    [Test]
    public void CancelledPermissionOutcomeFailsClosedAsCancellation()
    {
        var host = new RecordingHostActionEntry { OutcomeKind = ActionOutcomeKind.Cancelled };
        var entry = new HostPermissionActionEntry(host);
        var hostContext = TestHostActionContext.Create(new RequestPrincipal("agent-4", IsAuthenticated: true));

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await entry.EvaluateAgentAsync(
                hostContext,
                "read_agents",
                null));
    }

    [Test]
    public async Task HostEntryUsesTheRegisteredCrossSidecarPermissionRoute()
    {
        var host = new RecordingHostActionEntry
        {
            Result = AccessDecision.Allow("context_allowed"),
        };
        var entry = new HostPermissionActionEntry(host);
        var caller = new RequestPrincipal(
            "agent-authority",
            "Authority Agent",
            new HashSet<string>(["admin", "operator"]),
            true);
        var hostContext = TestHostActionContext.Create(caller, HostActionEntryIngress.Cli);

        await entry.EvaluateAgentAsync(hostContext, "manage_agents", Guid.NewGuid());

        Assert.Multiple(() =>
        {
            Assert.That(host.AgentCrossSidecarRequest, Is.Not.Null);
            Assert.That(host.AgentCrossSidecarRequest!.Descriptor, Is.SameAs(PermissionActionDescriptors.AgentAccess));
            Assert.That(host.AgentCrossSidecarRequest.Action.Capability, Is.EqualTo("manage_agents"));
            Assert.That(host.CrossSidecarKeys, Is.EqualTo(["permission.agent-access"]));
        });
    }

    [Test]
    public async Task ParentContextPermissionUsesTheCrossSidecarEntry()
    {
        var host = new RecordingHostActionEntry
        {
            Result = AccessDecision.Allow("cross_sidecar_allowed"),
        };
        var entry = new HostPermissionActionEntry(host);
        var caller = new RequestPrincipal("cross-sidecar-agent", IsAuthenticated: true);
        var parent = new ActionContext<string>(
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            1,
            DateTimeOffset.UtcNow.AddMinutes(1),
            new SharpClawActionKey("test.parent"),
            "test.module",
            caller,
            "parent",
            ExtensionFeatureSet.Empty,
            new ActionPipelineSnapshot("test", [], [], 16))
        {
            HostActionEntry = host,
        };

        var contextDecision = await entry.EvaluateContextAsync(
            parent,
            new ContextAccessRequest(
                Guid.NewGuid(),
                null,
                [],
                null,
                [],
                true));
        var agentDecision = await entry.EvaluateAgentAsync(parent, "read_agents", Guid.NewGuid());

        Assert.Multiple(() =>
        {
            Assert.That(contextDecision.Code, Is.EqualTo("cross_sidecar_allowed"));
            Assert.That(agentDecision.Code, Is.EqualTo("cross_sidecar_allowed"));
            Assert.That(host.CrossSidecarKeys, Is.EqualTo([
                "permission.context-access",
                "permission.agent-access"]));
            Assert.That(host.ContextCrossSidecarRequest, Is.Not.Null);
            Assert.That(host.AgentCrossSidecarRequest, Is.Not.Null);
        });
    }

    private sealed class RecordingHostActionEntry : IHostActionEntry, IModuleCrossSidecarActionEntry
    {
        public ActionOutcomeKind OutcomeKind { get; set; } = ActionOutcomeKind.Completed;

        public AccessDecision? Result { get; set; }

        public ExecutionError? Error { get; set; }

        public ActionUncertainty? Uncertainty { get; set; }

        public ModuleCrossSidecarActionEntryRequest<PermissionContextAccessAction, AccessDecision>? ContextCrossSidecarRequest { get; private set; }

        public ModuleCrossSidecarActionEntryRequest<PermissionAgentAccessAction, AccessDecision>? AgentCrossSidecarRequest { get; private set; }

        public List<string> CrossSidecarKeys { get; } = [];

        public ValueTask<IActionOutcome<TResult>> InvokeAsync<TAction, TResult>(
            HostActionEntryRequest<TAction, TResult> request,
            IHostActionEntryTerminal<TAction, TResult> terminal,
            CancellationToken ct)
        {
            return ValueTask.FromResult<IActionOutcome<TResult>>(
                new TestActionOutcome<TResult>(
                    OutcomeKind,
                    Result is null ? default! : (TResult)(object)Result,
                    Error,
                    Uncertainty));
        }

        public ValueTask<IActionOutcome<TResult>> InvokeNestedAsync<TParentAction, TAction, TResult>(
            HostActionEntryNestedRequest<TParentAction, TAction, TResult> request,
            IHostActionEntryTerminal<TAction, TResult> terminal,
            CancellationToken ct) =>
            ValueTask.FromResult<IActionOutcome<TResult>>(
                new TestActionOutcome<TResult>(
                    OutcomeKind,
                    Result is null ? default! : (TResult)(object)Result,
                    Error,
                    Uncertainty));

        public ValueTask<IActionOutcome<TResult>> InvokeCrossSidecarAsync<TAction, TResult>(
            ModuleCrossSidecarActionEntryRequest<TAction, TResult> request,
            CancellationToken ct)
        {
            CrossSidecarKeys.Add(request.Descriptor.Key.Value);
            if (request.Action is PermissionContextAccessAction contextAction)
                ContextCrossSidecarRequest = new ModuleCrossSidecarActionEntryRequest<PermissionContextAccessAction, AccessDecision>(
                    PermissionActionDescriptors.ContextAccess,
                    contextAction);
            else if (request.Action is PermissionAgentAccessAction agentAction)
                AgentCrossSidecarRequest = new ModuleCrossSidecarActionEntryRequest<PermissionAgentAccessAction, AccessDecision>(
                    PermissionActionDescriptors.AgentAccess,
                    agentAction);
            return ValueTask.FromResult<IActionOutcome<TResult>>(
                new TestActionOutcome<TResult>(
                    OutcomeKind,
                    Result is null ? default! : (TResult)(object)Result,
                    Error,
                    Uncertainty));
        }
    }

    private sealed class TestActionOutcome<TResult>(
        ActionOutcomeKind kind,
        TResult result,
        ExecutionError? error,
        ActionUncertainty? uncertainty) : IActionOutcome<TResult>
    {
        public ActionOutcomeKind Kind { get; } = kind;

        public TResult Result { get; } = result;

        public ContinuationToken? Continuation => null;

        public ExecutionError? Error { get; } = error;

        public ActionUncertainty? Uncertainty { get; } = uncertainty;
    }
}
