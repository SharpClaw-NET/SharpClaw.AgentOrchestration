using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.AgentOrchestration.Contracts;

public sealed record ContextModuleContract;

public sealed record PermissionModuleContract;

public sealed record AgentsModuleContract;

public sealed record ContextAccessRequest(
    RequestPrincipal Principal,
    Guid ChannelId,
    Guid? OwnerAgentId,
    IReadOnlyList<Guid> AllowedAgentIds,
    Guid? DefaultContextAgentId,
    IReadOnlyList<Guid> ContextAllowedAgentIds,
    bool SourceChannelOptedIn);

public sealed record ContextAccessDecision(
    bool Allowed,
    string Code,
    string Message)
{
    public static ContextAccessDecision Allow(string code = "allowed") =>
        new(true, code, "Access allowed.");

    public static ContextAccessDecision Deny(string code, string message) =>
        new(false, code, message);
}

public interface IContextAccessPolicy
{
    ValueTask<ContextAccessDecision> EvaluateAsync(
        ContextAccessRequest request,
        CancellationToken ct = default);
}

public interface IAgentAccessPolicy
{
    ValueTask<ContextAccessDecision> EvaluateAgentAsync(
        RequestPrincipal principal,
        string capability,
        Guid? targetAgentId,
        CancellationToken ct = default);
}
