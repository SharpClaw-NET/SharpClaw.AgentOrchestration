using System.Text.Json;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Modules.AgentOrchestration.Contracts;

public sealed record ContextCapabilityContract;

public sealed record AgentCapabilityContract;

public static class ContextAccessCapabilities
{
    public const string ReadCrossThreadHistory = "read_cross_thread_history";
    public const string ReadHistory = "context_read";
    public const string CreateThread = "context_create";
    public const string CommitExchange = "context_write";
}

public sealed record ContextAccessRequest(
    Guid ChannelId,
    Guid? OwnerAgentId,
    IReadOnlyList<Guid> AllowedAgentIds,
    Guid? DefaultContextAgentId,
    IReadOnlyList<Guid> ContextAllowedAgentIds,
    bool SourceChannelOptedIn,
    Guid? ContextId = null,
    string Capability = ContextAccessCapabilities.ReadCrossThreadHistory);

public sealed record AccessDecision(
    bool Allowed,
    string Code,
    string Message)
{
    public static AccessDecision Allow(string code = "allowed") =>
        new(true, code, "Access allowed.");

    public static AccessDecision Deny(string code, string message) =>
        new(false, code, message);
}

/// <summary>Maps Agent Orchestration domain data to the generic authorization port.</summary>
public static class AuthorizationRequestFactory
{
    private const string ChannelResource = "channel";
    private const string AgentResource = "agent";
    private const string GlobalResource = "global";
    private const string ContextResource = "context";
    private const string OwnerAgentResource = "owner-agent";
    private const string AllowedAgentResource = "allowed-agent";
    private const string DefaultContextAgentResource = "default-context-agent";
    private const string ContextAllowedAgentResource = "context-allowed-agent";
    private const string SourceChannelOptInFact = "source-channel-opted-in";

    public static AuthorizationRequest ForContext(ContextAccessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ChannelId == Guid.Empty)
            throw new ArgumentException("The channel identifier is required.", nameof(request));

        var related = new List<AuthorizationResource>();
        Add(related, ContextResource, request.ContextId);
        Add(related, OwnerAgentResource, request.OwnerAgentId);
        Add(related, DefaultContextAgentResource, request.DefaultContextAgentId);
        Add(related, AllowedAgentResource, request.AllowedAgentIds);
        Add(related, ContextAllowedAgentResource, request.ContextAllowedAgentIds);

        return new AuthorizationRequest(
            request.Capability,
            new AuthorizationResource(ChannelResource, Canonical(request.ChannelId)),
            related,
            [new AuthorizationFact(
                SourceChannelOptInFact,
                JsonSerializer.SerializeToElement(request.SourceChannelOptedIn))]);
    }

    public static AuthorizationRequest ForAgent(string capability, Guid? agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        if (agentId == Guid.Empty)
            throw new ArgumentException("The agent identifier cannot be empty.", nameof(agentId));

        return new AuthorizationRequest(
            capability,
            new AuthorizationResource(
                agentId.HasValue ? AgentResource : GlobalResource,
                agentId.HasValue ? Canonical(agentId.Value) : GlobalResource));
    }

    public static bool TryReadContext(
        AuthorizationRequest request,
        out ContextAccessRequest context)
    {
        ArgumentNullException.ThrowIfNull(request);
        context = null!;
        if (!string.Equals(request.Resource.Type, ChannelResource, StringComparison.Ordinal)
            || !Guid.TryParseExact(request.Resource.Id, "D", out var channelId)
            || channelId == Guid.Empty
            || !TryReadOptionalSingle(request, ContextResource, out var contextId)
            || !TryReadOptionalSingle(request, OwnerAgentResource, out var ownerAgentId)
            || !TryReadOptionalSingle(request, DefaultContextAgentResource, out var defaultAgentId)
            || !TryReadMany(request, AllowedAgentResource, out var allowedAgents)
            || !TryReadMany(request, ContextAllowedAgentResource, out var contextAllowedAgents)
            || !TryReadBooleanFact(request, SourceChannelOptInFact, out var sourceOptIn))
        {
            return false;
        }

        context = new ContextAccessRequest(
            channelId,
            ownerAgentId,
            allowedAgents,
            defaultAgentId,
            contextAllowedAgents,
            sourceOptIn,
            contextId,
            request.Operation);
        return true;
    }

    public static bool TryReadAgent(AuthorizationRequest request, out Guid? agentId)
    {
        ArgumentNullException.ThrowIfNull(request);
        agentId = null;
        if (string.Equals(request.Resource.Type, GlobalResource, StringComparison.Ordinal))
            return string.Equals(request.Resource.Id, GlobalResource, StringComparison.Ordinal);

        if (!string.Equals(request.Resource.Type, AgentResource, StringComparison.Ordinal)
            || !Guid.TryParseExact(request.Resource.Id, "D", out var parsed)
            || parsed == Guid.Empty)
        {
            return false;
        }

        agentId = parsed;
        return true;
    }

    private static bool TryReadOptionalSingle(
        AuthorizationRequest request,
        string type,
        out Guid? value)
    {
        value = null;
        var resources = request.EffectiveRelatedResources
            .Where(item => string.Equals(item.Type, type, StringComparison.Ordinal))
            .ToArray();
        if (resources.Length == 0)
            return true;
        if (resources.Length != 1
            || !Guid.TryParseExact(resources[0].Id, "D", out var parsed)
            || parsed == Guid.Empty)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryReadMany(
        AuthorizationRequest request,
        string type,
        out IReadOnlyList<Guid> values)
    {
        var result = new List<Guid>();
        foreach (var resource in request.EffectiveRelatedResources
                     .Where(item => string.Equals(item.Type, type, StringComparison.Ordinal)))
        {
            if (!Guid.TryParseExact(resource.Id, "D", out var parsed)
                || parsed == Guid.Empty
                || result.Contains(parsed))
            {
                values = [];
                return false;
            }

            result.Add(parsed);
        }

        values = result;
        return true;
    }

    private static bool TryReadBooleanFact(
        AuthorizationRequest request,
        string name,
        out bool value)
    {
        value = false;
        var facts = request.EffectiveFacts
            .Where(item => string.Equals(item.Name, name, StringComparison.Ordinal))
            .ToArray();
        if (facts.Length != 1
            || facts[0].Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = facts[0].Value.GetBoolean();
        return true;
    }

    private static void Add(
        ICollection<AuthorizationResource> resources,
        string type,
        Guid? value)
    {
        if (value.HasValue)
            resources.Add(new AuthorizationResource(type, Canonical(value.Value)));
    }

    private static void Add(
        ICollection<AuthorizationResource> resources,
        string type,
        IEnumerable<Guid> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var value in values.Order())
        {
            if (value == Guid.Empty)
                throw new ArgumentException("An authorization resource identifier cannot be empty.", nameof(values));
            resources.Add(new AuthorizationResource(type, Canonical(value)));
        }
    }

    private static string Canonical(Guid value) => value.ToString("D");
}
