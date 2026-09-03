using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;

namespace SharpClaw.Modules.AgentOrchestration.Contracts;

/// <summary>One restriction that preserves or denies an authoritative permission decision.</summary>
public readonly record struct PermissionRestriction
{
    private const int MaximumCodeLength = 80;
    private const int MaximumMessageLength = 512;

    private PermissionRestriction(bool denied, string? code, string? message)
    {
        Denied = denied;
        Code = code;
        Message = message;
    }

    public bool Denied { get; }

    public string? Code { get; }

    public string? Message { get; }

    /// <summary>Preserves the decision from the remaining permission pipeline.</summary>
    public static PermissionRestriction Preserve() => default;

    /// <summary>Stops the permission pipeline with a denial.</summary>
    public static PermissionRestriction Deny(string code, string message)
    {
        ValidateCode(code, nameof(code));
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Length > MaximumMessageLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(message),
                $"A permission restriction message cannot exceed {MaximumMessageLength} characters.");
        }

        return new PermissionRestriction(true, code, message);
    }

    internal static ExecutionError ToFailure(PermissionRestriction restriction)
    {
        if (!restriction.Denied ||
            string.IsNullOrWhiteSpace(restriction.Code) ||
            string.IsNullOrWhiteSpace(restriction.Message))
        {
            return new ExecutionError(
                "permission_restriction_invalid",
                "A permission restriction returned an invalid decision.");
        }

        return new ExecutionError(
            AgentOrchestrationPermission.RestrictionFailureCodePrefix + restriction.Code,
            restriction.Message);
    }

    internal static bool TryMapFailure(
        ExecutionError? error,
        out AccessDecision decision)
    {
        decision = null!;
        if (error is null ||
            !error.Code.StartsWith(
                AgentOrchestrationPermission.RestrictionFailureCodePrefix,
                StringComparison.Ordinal))
        {
            return false;
        }

        var code = error.Code[AgentOrchestrationPermission.RestrictionFailureCodePrefix.Length..];
        if (!IsValidCode(code) || string.IsNullOrWhiteSpace(error.Message))
            return false;

        decision = AccessDecision.Deny(code, error.Message);
        return true;
    }

    private static void ValidateCode(string code, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code, parameterName);
        if (!IsValidCode(code))
        {
            throw new ArgumentException(
                "A permission restriction code must use lowercase ASCII letters, digits, periods, hyphens, or underscores.",
                parameterName);
        }
    }

    private static bool IsValidCode(string code) =>
        code.Length is > 0 and <= MaximumCodeLength &&
        code.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_');
}

/// <summary>Restricts decisions from one independent Agent Orchestration permission provider.</summary>
public interface IAgentOrchestrationPermissionRestriction
{
    ValueTask<PermissionRestriction> RestrictContextAsync(
        ActionContext<PermissionContextAccessAction> context,
        CancellationToken ct = default) =>
        ValueTask.FromResult(PermissionRestriction.Preserve());

    ValueTask<PermissionRestriction> RestrictAgentAsync(
        ActionContext<PermissionAgentAccessAction> context,
        CancellationToken ct = default) =>
        ValueTask.FromResult(PermissionRestriction.Preserve());
}

/// <summary>Adds independent permission restrictions to the neutral action graph.</summary>
public static class AgentOrchestrationPermissionRestrictionBuilderExtensions
{
    private const ActionInterceptionCapabilities RestrictionCapabilities =
        ActionInterceptionCapabilities.Inspect |
        ActionInterceptionCapabilities.Wrap;

    /// <summary>Adds one restriction that can preserve or deny permission checks.</summary>
    public static void AddAgentOrchestrationPermissionRestriction<TRestriction>(
        this ISharpClawModuleBuilder module,
        string restrictionId,
        AgentOrchestrationPermissionUse use = AgentOrchestrationPermissionUse.All,
        HookPriority priority = HookPriority.Normal)
        where TRestriction : class, IAgentOrchestrationPermissionRestriction
    {
        ArgumentNullException.ThrowIfNull(module);
        ValidateRestrictionId(restrictionId);
        if (use is AgentOrchestrationPermissionUse.None ||
            (use & ~AgentOrchestrationPermissionUse.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(use));
        }

        module.Services.TryAddScoped<TRestriction>();
        module.Contracts.Require<PermissionModuleContract>(
            AgentOrchestrationPermission.ContractName);

        if ((use & AgentOrchestrationPermissionUse.Context) != 0)
        {
            module.Services.TryAddScoped<PermissionContextRestrictionHook<TRestriction>>();
            module.Hooks.For(PermissionActionDescriptors.ContextAccess)
                .Use<PermissionContextRestrictionHook<TRestriction>>(
                    RestrictionCapabilities,
                    new HookOrdering(
                        $"permission.restriction.{restrictionId}.context",
                        priority));
        }

        if ((use & AgentOrchestrationPermissionUse.Agents) != 0)
        {
            module.Services.TryAddScoped<PermissionAgentRestrictionHook<TRestriction>>();
            module.Hooks.For(PermissionActionDescriptors.AgentAccess)
                .Use<PermissionAgentRestrictionHook<TRestriction>>(
                    RestrictionCapabilities,
                    new HookOrdering(
                        $"permission.restriction.{restrictionId}.agent",
                        priority));
        }
    }

    private static void ValidateRestrictionId(string restrictionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(restrictionId);
        if (restrictionId.Length > 80 ||
            !restrictionId.All(character =>
                character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_'))
        {
            throw new ArgumentException(
                "A permission restriction identifier must use lowercase ASCII letters, digits, periods, hyphens, or underscores.",
                nameof(restrictionId));
        }
    }
}

/// <summary>Applies one restriction before a context permission decision.</summary>
public sealed class PermissionContextRestrictionHook<TRestriction>(TRestriction restriction)
    : IActionInterceptor<PermissionContextAccessAction, AccessDecision>
    where TRestriction : class, IAgentOrchestrationPermissionRestriction
{
    public async ValueTask<IActionOutcome<AccessDecision>> InvokeAsync(
        ActionContext<PermissionContextAccessAction> context,
        IActionControl<PermissionContextAccessAction, AccessDecision> control,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = await restriction.RestrictContextAsync(context, ct);
        return result.Denied
            ? control.Fail(PermissionRestriction.ToFailure(result))
            : await control.ProceedAsync(ct);
    }
}

/// <summary>Applies one restriction before an agent permission decision.</summary>
public sealed class PermissionAgentRestrictionHook<TRestriction>(TRestriction restriction)
    : IActionInterceptor<PermissionAgentAccessAction, AccessDecision>
    where TRestriction : class, IAgentOrchestrationPermissionRestriction
{
    public async ValueTask<IActionOutcome<AccessDecision>> InvokeAsync(
        ActionContext<PermissionAgentAccessAction> context,
        IActionControl<PermissionAgentAccessAction, AccessDecision> control,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = await restriction.RestrictAgentAsync(context, ct);
        return result.Denied
            ? control.Fail(PermissionRestriction.ToFailure(result))
            : await control.ProceedAsync(ct);
    }
}
