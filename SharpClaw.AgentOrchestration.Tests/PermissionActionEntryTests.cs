using NUnit.Framework;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;

namespace SharpClaw.AgentOrchestration.Tests;

[TestFixture]
public sealed class PermissionActionEntryTests
{
    [Test]
    public async Task HostContextUsesOneGenericAuthorizationEntry()
    {
        var host = new RecordingHostActionEntry(AuthorizationDecision.Allow("allowed"));
        var entry = new HostAuthorizationEntry(host);
        var request = CreateRequest();

        var decision = await entry.EvaluateAsync(
            TestHostActionContext.Create(new RequestPrincipal("caller", IsAuthenticated: true)),
            request);

        Assert.Multiple(() =>
        {
            Assert.That(decision, Is.EqualTo(AuthorizationDecision.Allow("allowed")));
            Assert.That(host.Calls, Is.EqualTo(1));
            Assert.That(host.LastDescriptor, Is.EqualTo(AuthorizationProtocol.Evaluate));
            Assert.That(host.LastRequest, Is.SameAs(request));
        });
    }

    [Test]
    public async Task ActionContextUsesTheSameGenericEntry()
    {
        var host = new RecordingHostActionEntry(AuthorizationDecision.Deny(
            "denied",
            "The policy denies access."));
        var entry = new HostAuthorizationEntry(host);
        var request = CreateRequest();
        var parent = CreateParentContext(host);

        var decision = await entry.EvaluateAsync(parent, request);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Allowed, Is.False);
            Assert.That(host.Calls, Is.EqualTo(1));
            Assert.That(host.LastRequest, Is.SameAs(request));
        });
    }

    [Test]
    public async Task ChatContextUsesTheSameGenericEntry()
    {
        var host = new RecordingHostActionEntry(AuthorizationDecision.Allow());
        var entry = new HostAuthorizationEntry(host);
        var caller = new RequestPrincipal("chat-caller", IsAuthenticated: true);
        var chat = new ChatOperationContext(
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            1,
            DateTimeOffset.UtcNow.AddMinutes(1),
            caller,
            ExtensionFeatureSet.Empty,
            host);

        var decision = await entry.EvaluateAsync(chat, CreateRequest());

        Assert.Multiple(() =>
        {
            Assert.That(decision.Allowed, Is.True);
            Assert.That(host.Calls, Is.EqualTo(1));
        });
    }

    [Test]
    public void CancellationRemainsVisible()
    {
        var host = new RecordingHostActionEntry(null, ActionOutcomeKind.Cancelled);
        var entry = new HostAuthorizationEntry(host);
        using var cancellation = new CancellationTokenSource();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await entry.EvaluateAsync(
                TestHostActionContext.Create(new RequestPrincipal("caller", IsAuthenticated: true)),
                CreateRequest(),
                cancellation.Token));
    }

    [Test]
    public void FailureRemainsVisible()
    {
        var host = new RecordingHostActionEntry(
            null,
            ActionOutcomeKind.Failed,
            new ExecutionError("policy_failed", "The policy failed."));
        var entry = new HostAuthorizationEntry(host);

        var exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await entry.EvaluateAsync(
                TestHostActionContext.Create(new RequestPrincipal("caller", IsAuthenticated: true)),
                CreateRequest()));

        Assert.That(exception!.Message, Does.Contain("policy_failed"));
    }

    private static AuthorizationRequest CreateRequest() =>
        new("agents.read", new AuthorizationResource("agent", Guid.NewGuid().ToString("D")));

    private static ActionContext<string> CreateParentContext(IHostActionEntry host) =>
        new(
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            1,
            DateTimeOffset.UtcNow.AddMinutes(1),
            new SharpClawActionKey("agents.parent"),
            "agents",
            new RequestPrincipal("caller", IsAuthenticated: true),
            "parent",
            ExtensionFeatureSet.Empty,
            new ActionPipelineSnapshot("authorization-entry-test", [], [], 16))
        {
            HostActionEntry = host,
        };

    private sealed class RecordingHostActionEntry(
        AuthorizationDecision? result,
        ActionOutcomeKind kind = ActionOutcomeKind.Completed,
        ExecutionError? error = null) : IHostActionEntry, IModuleCrossSidecarActionEntry
    {
        public int Calls { get; private set; }

        public object? LastDescriptor { get; private set; }

        public AuthorizationRequest? LastRequest { get; private set; }

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
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastDescriptor = request.Descriptor;
            LastRequest = request.Action as AuthorizationRequest;
            return ValueTask.FromResult<IActionOutcome<TResult>>(
                new Outcome<TResult>(kind, (TResult?)(object?)result, error));
        }
    }

    private sealed class Outcome<TResult>(
        ActionOutcomeKind kind,
        TResult? result,
        ExecutionError? error) : IActionOutcome<TResult>
    {
        public ActionOutcomeKind Kind { get; } = kind;
        public TResult? Result { get; } = result;
        public ContinuationToken? Continuation => null;
        public ExecutionError? Error { get; } = error;
        public ActionUncertainty? Uncertainty => null;
    }
}
