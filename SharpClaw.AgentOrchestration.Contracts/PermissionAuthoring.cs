using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;

namespace SharpClaw.Modules.AgentOrchestration.Contracts;

/// <summary>Defines the stable Agent Orchestration permission boundary.</summary>
public static class AgentOrchestrationPermission
{
    public const string ContractName = "sharpclaw.permission";

    public static readonly Guid ContextTerminalId =
        Guid.Parse("8f7be0a6-2f4d-5b72-9dc8-3ca4e9c2f102");

    public static readonly Guid AgentTerminalId =
        Guid.Parse("8f7be0a6-2f4d-5b72-9dc8-3ca4e9c2f103");
}

/// <summary>Defines the permission checks that Agent Orchestration modules use.</summary>
public interface IAgentOrchestrationPermissionPolicy
{
    ValueTask<AccessDecision> EvaluateContextAsync(
        ActionContext<PermissionContextAccessAction> context,
        CancellationToken ct = default);

    ValueTask<AccessDecision> EvaluateAgentAsync(
        ActionContext<PermissionAgentAccessAction> context,
        CancellationToken ct = default);
}

[Flags]
public enum AgentOrchestrationPermissionUse
{
    None = 0,
    Context = 1 << 0,
    Agents = 1 << 1,
    All = Context | Agents,
}

/// <summary>Adds the provider or consumer side of the permission boundary.</summary>
public static class AgentOrchestrationPermissionBuilderExtensions
{
    /// <summary>Adds one permission policy as the permission contract owner.</summary>
    public static void AddAgentOrchestrationPermissionPolicy<TPolicy>(
        this ISharpClawModuleBuilder module)
        where TPolicy : class, IAgentOrchestrationPermissionPolicy
    {
        ArgumentNullException.ThrowIfNull(module);

        module.Services.TryAddScoped<TPolicy>();
        module.Services.TryAddScoped<IAgentOrchestrationPermissionPolicy>(services =>
            services.GetRequiredService<TPolicy>());
        module.Contracts.Export<PermissionModuleContract>(
            AgentOrchestrationPermission.ContractName);
        module.Actions.Add(PermissionActionDescriptors.ContextAccess);
        module.Actions.Add(PermissionActionDescriptors.AgentAccess);
        module.AddActionEntry<
            PermissionContextAccessAction,
            AccessDecision,
            PermissionContextPolicyTerminal>(
            PermissionActionDescriptors.ContextAccess,
            AgentOrchestrationPermission.ContextTerminalId);
        module.AddActionEntry<
            PermissionAgentAccessAction,
            AccessDecision,
            PermissionAgentPolicyTerminal>(
            PermissionActionDescriptors.AgentAccess,
            AgentOrchestrationPermission.AgentTerminalId);
    }

    /// <summary>Adds access to the active permission policy.</summary>
    public static void UseAgentOrchestrationPermission(
        this ISharpClawModuleBuilder module,
        AgentOrchestrationPermissionUse use)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (use is AgentOrchestrationPermissionUse.None ||
            (use & ~AgentOrchestrationPermissionUse.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(use));
        }

        module.Services.TryAddScoped<HostPermissionActionEntry>();
        module.Contracts.Require<PermissionModuleContract>(
            AgentOrchestrationPermission.ContractName);

        if ((use & AgentOrchestrationPermissionUse.Context) != 0)
        {
            module.Services.TryAddScoped<PermissionContextRelayHook>();
            module.Hooks.For(PermissionActionDescriptors.ContextAccess)
                .Use<PermissionContextRelayHook>(
                    ActionInterceptionCapabilities.Inspect |
                    ActionInterceptionCapabilities.Observe,
                    new HookOrdering("permission.context-access.host-entry"));
        }

        if ((use & AgentOrchestrationPermissionUse.Agents) != 0)
        {
            module.Services.TryAddScoped<PermissionAgentRelayHook>();
            module.Hooks.For(PermissionActionDescriptors.AgentAccess)
                .Use<PermissionAgentRelayHook>(
                    ActionInterceptionCapabilities.Inspect |
                    ActionInterceptionCapabilities.Observe,
                    new HookOrdering("permission.agent-access.host-entry"));
        }
    }
}

/// <summary>Runs a context check through the configured permission policy.</summary>
public sealed class PermissionContextPolicyTerminal(
    IAgentOrchestrationPermissionPolicy policy)
    : IHostActionEntryTerminal<PermissionContextAccessAction, AccessDecision>
{
    public Guid TerminalId => AgentOrchestrationPermission.ContextTerminalId;

    public ValueTask<AccessDecision> InvokeAsync(
        ActionContext<PermissionContextAccessAction> context,
        CancellationToken cancellationToken = default) =>
        policy.EvaluateContextAsync(context, cancellationToken);
}

/// <summary>Runs an agent check through the configured permission policy.</summary>
public sealed class PermissionAgentPolicyTerminal(
    IAgentOrchestrationPermissionPolicy policy)
    : IHostActionEntryTerminal<PermissionAgentAccessAction, AccessDecision>
{
    public Guid TerminalId => AgentOrchestrationPermission.AgentTerminalId;

    public ValueTask<AccessDecision> InvokeAsync(
        ActionContext<PermissionAgentAccessAction> context,
        CancellationToken cancellationToken = default) =>
        policy.EvaluateAgentAsync(context, cancellationToken);
}

/// <summary>Preserves the source-side context action while the host relays it.</summary>
public sealed class PermissionContextRelayHook
    : IActionInterceptor<PermissionContextAccessAction, AccessDecision>
{
    public ValueTask<IActionOutcome<AccessDecision>> InvokeAsync(
        ActionContext<PermissionContextAccessAction> context,
        IActionControl<PermissionContextAccessAction, AccessDecision> control,
        CancellationToken ct) =>
        control.ProceedAsync(ct);
}

/// <summary>Preserves the source-side agent action while the host relays it.</summary>
public sealed class PermissionAgentRelayHook
    : IActionInterceptor<PermissionAgentAccessAction, AccessDecision>
{
    public ValueTask<IActionOutcome<AccessDecision>> InvokeAsync(
        ActionContext<PermissionAgentAccessAction> context,
        IActionControl<PermissionAgentAccessAction, AccessDecision> control,
        CancellationToken ct) =>
        control.ProceedAsync(ct);
}
