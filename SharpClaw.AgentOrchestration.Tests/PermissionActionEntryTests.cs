using NUnit.Framework;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.AgentOrchestration.Tests;

[TestFixture]
public sealed class PermissionActionEntryTests
{
    [Test]
    public async Task ContextAccessUsesTheHostEntryAndBindsTheCaller()
    {
        var host = new RecordingHostActionEntry
        {
            Result = PermissionDecision.Allow("context_allowed", 2, PermissionClearance.Independent),
        };
        var entry = new HostPermissionActionEntry(host);
        var caller = new RequestPrincipal("agent-1", IsAuthenticated: true);
        var hostContext = TestHostActionContext.Create(caller, HostActionEntryIngress.CrossModule);
        var request = new ContextAccessRequest(
            RequestPrincipal.Anonymous,
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
            Assert.That(host.ContextRequest, Is.Not.Null);
            Assert.That(host.ContextRequest!.Descriptor, Is.SameAs(PermissionActionDescriptors.ContextAccess));
            Assert.That(host.ContextRequest.Caller, Is.EqualTo(caller));
            Assert.That(host.ContextRequest.Action.Caller, Is.EqualTo(caller));
            Assert.That(host.ContextRequest.Action.Request.Principal, Is.EqualTo(caller));
            Assert.That(host.ContextRequest.Action.Request.ChannelId, Is.EqualTo(request.ChannelId));
        });
    }

    [Test]
    public async Task AgentAccessMapsACompletedDenial()
    {
        var host = new RecordingHostActionEntry
        {
            Result = PermissionDecision.Deny(
                "capability_denied",
                "The capability is not assigned.",
                1),
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
            Assert.That(host.AgentRequest, Is.Not.Null);
            Assert.That(host.AgentRequest!.Descriptor, Is.SameAs(PermissionActionDescriptors.AgentAccess));
            Assert.That(host.AgentRequest.Caller, Is.EqualTo(caller));
            Assert.That(host.AgentRequest.Action.Caller, Is.EqualTo(caller));
            Assert.That(host.AgentRequest.Action.Capability, Is.EqualTo("manage_agents"));
            Assert.That(host.AgentRequest.Action.TargetAgentId, Is.EqualTo(targetAgentId));
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
    public async Task HostEntryPreservesTheIssuedAuthorityContext()
    {
        var host = new RecordingHostActionEntry
        {
            Result = PermissionDecision.Allow("context_allowed", 2, PermissionClearance.Independent),
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
            Assert.That(host.AgentRequest, Is.Not.Null);
            Assert.That(host.AgentRequest!.Context, Is.SameAs(hostContext));
            Assert.That(host.AgentRequest.Caller, Is.EqualTo(caller));
            Assert.That(host.AgentRequest.Action.Caller, Is.EqualTo(caller));
            Assert.That(host.AgentRequest.Features, Is.SameAs(hostContext.Features));
            Assert.That(host.AgentRequest.TraceId, Is.EqualTo(hostContext.TraceId));
            Assert.That(host.AgentRequest.IdempotencyKey, Is.EqualTo(hostContext.IdempotencyKey));
            Assert.That(host.AgentRequest.Deadline, Is.EqualTo(hostContext.Deadline));
            Assert.That(host.AgentRequest.Context.CapabilityId, Is.EqualTo(hostContext.CapabilityId));
            Assert.That(host.AgentRequest.Context.CapabilityHandle, Is.EqualTo(hostContext.CapabilityHandle));
            Assert.That(host.AgentRequest.Context.Ingress, Is.EqualTo(hostContext.Ingress));
        });
    }

    private sealed class RecordingHostActionEntry : IHostActionEntry
    {
        public ActionOutcomeKind OutcomeKind { get; set; } = ActionOutcomeKind.Completed;

        public PermissionDecision? Result { get; set; }

        public ExecutionError? Error { get; set; }

        public ActionUncertainty? Uncertainty { get; set; }

        public HostActionEntryRequest<PermissionContextAccessAction, PermissionDecision>? ContextRequest { get; private set; }

        public HostActionEntryRequest<PermissionAgentAccessAction, PermissionDecision>? AgentRequest { get; private set; }

        public ValueTask<IActionOutcome<TResult>> InvokeAsync<TAction, TResult>(
            HostActionEntryRequest<TAction, TResult> request,
            CancellationToken ct)
        {
            if (request.Action is PermissionContextAccessAction)
                ContextRequest = (HostActionEntryRequest<PermissionContextAccessAction, PermissionDecision>)(object)request;
            else if (request.Action is PermissionAgentAccessAction)
                AgentRequest = (HostActionEntryRequest<PermissionAgentAccessAction, PermissionDecision>)(object)request;

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
