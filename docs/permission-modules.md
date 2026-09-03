# Permission Module Development

## Developer Model

A permission module owns one neutral contract. Context and Agents call that contract through the authenticated action graph. SharpClaw remains unaware of the policy implementation.

## Implement a Policy

Implement `IAgentOrchestrationPermissionPolicy` to receive context and agent access checks. Each method receives the full `ActionContext`, the typed request, and cancellation authority.

```csharp
public sealed class MyPermissionPolicy(IModuleStorageGateway storage)
    : IAgentOrchestrationPermissionPolicy
{
    public ValueTask<AccessDecision> EvaluateContextAsync(
        ActionContext<PermissionContextAccessAction> context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var allowed = context.Caller.Roles?.Contains("context-reader") == true;
        return ValueTask.FromResult(allowed
            ? AccessDecision.Allow("role_allowed")
            : AccessDecision.Deny("role_denied", "The caller cannot read this context."));
    }

    public ValueTask<AccessDecision> EvaluateAgentAsync(
        ActionContext<PermissionAgentAccessAction> context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var allowed = context.Caller.Roles?.Contains(context.Action.Capability) == true;
        return ValueTask.FromResult(allowed
            ? AccessDecision.Allow("role_allowed")
            : AccessDecision.Deny("role_denied", "The caller cannot use this agent operation."));
    }
}
```

The policy can inject normal module services. It can use `IModuleStorageGateway` through declared module storage. It does not access host databases or service providers.

## Register the Provider

Call one builder method from the module entry point. This call adds the service, contract export, typed descriptors, generated schemas, and stable terminals.

```csharp
public void Configure(ISharpClawModuleBuilder module)
{
    module.AddAgentOrchestrationPermissionPolicy<MyPermissionPolicy>();
}
```

The module manifest must advertise the contract before process activation. This metadata lets the host resolve the provider without loading optional implementation code.

```json
{
  "exports": [
    {
      "contractName": "sharpclaw.permission",
      "serviceType": "SharpClaw.Modules.AgentOrchestration.Contracts.PermissionModuleContract",
      "optional": false
    }
  ]
}
```

## Use the Policy

A module that needs checks selects its required action family. The helper adds the contract requirement, typed client, and authenticated relay subscription.

```csharp
module.UseAgentOrchestrationPermission(AgentOrchestrationPermissionUse.Context);
```

Use `AgentOrchestrationPermissionUse.Agents` for agent operations. Use `AgentOrchestrationPermissionUse.All` when one module performs both check types.

## Preserve Authority

Use `context.Caller` as the caller identity. Use `context.Features` for issued feature authority. Never accept either value from an action payload or request body.

Return an explicit denial for unsupported operations. Propagate cancellation before storage or other effects. A denied check must complete before protected work starts.

## Replace Two Tier Permission

Remove the Two Tier Permission package from the selected module payload. Add one package that exports `sharpclaw.permission`. Context and Agents then use the replacement without source changes.

Only one module can own the permission contract. This rule prevents two providers from producing conflicting authority. A replacement can compose multiple internal evaluators behind its one policy.

## Keep Low-Level Control

`PermissionActionDescriptors` remains public for action inspection and observation. Advanced modules can register an exact typed hook instead of the standard relay helper.

The permission descriptors permit only `Inspect` and `Observe`. Another module cannot replace the request or result. This rule prevents a hook from granting authority.

A permission module can define additional typed actions without separate schema and terminal registration. The descriptor still controls all action capabilities and policies.

```csharp
module.DefineAction(MyPermissionActions.Review)
    .UseTerminal<MyPermissionReviewTerminal>(MyPermissionTerminals.Review);
```

Use `module.Actions.Add` and `module.AddActionEntry` when separate registration is necessary. Both forms compile through the same action graph.

## Test the Module

Compile the real module through `SharpClawModuleCompiler`. Verify one contract export, two action definitions, and two stable action entries. Invoke the terminals with authenticated `ActionContext` instances.

Test allowed, denied, failed, and cancelled checks. Verify that denial and cancellation occur before protected storage writes. Compile Context and Agents against the replacement contract.
