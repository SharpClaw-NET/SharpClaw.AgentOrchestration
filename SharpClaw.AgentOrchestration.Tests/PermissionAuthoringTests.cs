using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;
using SharpClaw.Modules.TwoTierPermission;

namespace SharpClaw.AgentOrchestration.Tests;

[TestFixture]
public sealed class PermissionAuthoringTests
{
    [Test]
    public void IndependentProviderPublishesTheNeutralAuthorizationPort()
    {
        var graph = Compile(new IndependentAuthorizationPackage());

        Assert.Multiple(() =>
        {
            Assert.That(graph.Contracts, Has.Count.EqualTo(1));
            Assert.That(graph.Contracts[0].ContractName, Is.EqualTo(AuthorizationProtocol.ContractName));
            Assert.That(graph.Contracts[0].IsExport, Is.True);
            Assert.That(graph.Actions.Select(value => value.Descriptor.Key.Value),
                Is.EqualTo([AuthorizationProtocol.Evaluate.Key.Value]));
            Assert.That(graph.ActionEntries, Has.Count.EqualTo(1));
            Assert.That(graph.ActionEntries[0].TerminalId, Is.EqualTo(AuthorizationProtocol.TerminalId));
            Assert.That(graph.ActionEntries[0].TerminalType, Is.EqualTo(typeof(AuthorizationPolicyTerminal)));
        });

        var services = new ServiceCollection();
        foreach (var service in graph.Services)
            ((ICollection<ServiceDescriptor>)services).Add(service);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
        using var scope = provider.CreateScope();

        Assert.That(scope.ServiceProvider.GetRequiredService<IAuthorizationPolicy>(),
            Is.TypeOf<IndependentPolicy>());
    }

    [Test]
    public void IndependentProviderMatchesTheTwoTierProviderPort()
    {
        var independent = Compile(new IndependentAuthorizationPackage());
        var twoTier = Compile(new TwoTierPermissionModule());

        var twoTierContract = twoTier.Contracts.Single(value =>
            value.ContractName == AuthorizationProtocol.ContractName);
        var twoTierEntry = twoTier.ActionEntries.Single(value =>
            value.Descriptor.Key == AuthorizationProtocol.Evaluate.Key);

        Assert.Multiple(() =>
        {
            Assert.That(twoTierContract.ServiceType, Is.EqualTo(independent.Contracts[0].ServiceType));
            Assert.That(twoTierContract.IsExport, Is.True);
            Assert.That(twoTierEntry.Descriptor, Is.EqualTo(independent.ActionEntries[0].Descriptor));
            Assert.That(twoTierEntry.TerminalId, Is.EqualTo(independent.ActionEntries[0].TerminalId));
            Assert.That(twoTierEntry.TerminalType, Is.EqualTo(typeof(AuthorizationPolicyTerminal)));
        });
    }

    [Test]
    public async Task ProviderReceivesAuthenticatedCallerAndGenericResource()
    {
        var policy = new IndependentPolicy();
        var terminal = new AuthorizationPolicyTerminal(policy);
        var caller = new RequestPrincipal(
            "custom-owner",
            Roles: new HashSet<string>(["custom-policy"]),
            IsAuthenticated: true);
        var resourceId = Guid.NewGuid().ToString("D");
        var request = new AuthorizationRequest(
            "agents.read",
            new AuthorizationResource("agent", resourceId));

        var decision = await terminal.InvokeAsync(CreateContext(caller, request));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Allowed, Is.True);
            Assert.That(policy.LastCaller, Is.EqualTo(caller));
            Assert.That(policy.LastOperation, Is.EqualTo("agents.read"));
            Assert.That(policy.LastResource, Is.EqualTo(new AuthorizationResource("agent", resourceId)));
            Assert.That(typeof(AuthorizationRequest).GetProperty("Principal"), Is.Null);
        });
    }

    [Test]
    public void ProviderCancellationStopsBeforeEvaluation()
    {
        var policy = new IndependentPolicy();
        var terminal = new AuthorizationPolicyTerminal(policy);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await terminal.InvokeAsync(
                CreateContext(
                    new RequestPrincipal("custom-owner", IsAuthenticated: true),
                    new AuthorizationRequest(
                        "agents.read",
                        new AuthorizationResource("agent", Guid.NewGuid().ToString("D")))),
                cancellation.Token));
        Assert.That(policy.Evaluations, Is.Zero);
    }

    [Test]
    public void ConsumerRequiresOnlyTheNeutralAuthorizationPort()
    {
        var graph = Compile(new AuthorizationConsumerPackage());

        Assert.Multiple(() =>
        {
            Assert.That(graph.Contracts, Has.Count.EqualTo(1));
            Assert.That(graph.Contracts[0].ContractName, Is.EqualTo(AuthorizationProtocol.ContractName));
            Assert.That(graph.Contracts[0].IsExport, Is.False);
            Assert.That(graph.Actions, Is.Empty);
            Assert.That(graph.ActionEntries, Is.Empty);
            Assert.That(graph.Services.Any(value => value.ServiceType == typeof(HostAuthorizationEntry)), Is.True);
        });
    }

    private static ModuleContributionGraph Compile(ISharpClawModule package) =>
        SharpClawModuleCompiler.Compile(
            package,
            options: new ModuleCompilationOptions
            {
                HostingMode = ModuleHostingMode.OutOfProcess,
                RequireManifestRequests = false,
            });

    private static ActionContext<AuthorizationRequest> CreateContext(
        RequestPrincipal caller,
        AuthorizationRequest request) =>
        new(
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            1,
            DateTimeOffset.UtcNow.AddMinutes(1),
            AuthorizationProtocol.Evaluate.Key,
            IndependentAuthorizationPackage.SourceId,
            caller,
            request,
            ExtensionFeatureSet.Empty,
            new ActionPipelineSnapshot("authorization-authoring-test", [], [], 16));

    private sealed class IndependentAuthorizationPackage : ISharpClawModule
    {
        public const string SourceId = "independent_authorization";

        public ModuleIdentity Identity { get; } = new(
            SourceId,
            "Independent Authorization",
            "independent_authorization");

        public void ConfigureServices(IServiceCollection services) =>
            services.AddAuthorizationPolicy<IndependentPolicy>();
    }

    private sealed class AuthorizationConsumerPackage : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new(
            "authorization_consumer",
            "Authorization Consumer",
            "authorization_consumer");

        public void ConfigureServices(IServiceCollection services) => services.RequireAuthorization();
    }

    private sealed class IndependentPolicy : IAuthorizationPolicy
    {
        public RequestPrincipal? LastCaller { get; private set; }

        public string? LastOperation { get; private set; }

        public AuthorizationResource? LastResource { get; private set; }

        public int Evaluations { get; private set; }

        public ValueTask<AuthorizationDecision> EvaluateAsync(
            ActionContext<AuthorizationRequest> context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Evaluations++;
            LastCaller = context.Caller;
            LastOperation = context.Action.Operation;
            LastResource = context.Action.Resource;
            return ValueTask.FromResult(
                context.Caller.Roles?.Contains("custom-policy") == true
                    ? AuthorizationDecision.Allow("custom_policy")
                    : AuthorizationDecision.Deny("custom_policy", "The custom policy denies access."));
        }
    }
}
