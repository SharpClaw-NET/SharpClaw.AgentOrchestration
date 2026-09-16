# Authorization Extension Development

## Developer Model

SharpClaw uses one neutral authorization contract. One package supplies the authoritative policy. Other packages can require that policy or add independent restrictions. The host discovers these packages and connects them through the authenticated action graph. The host does not know the policy implementation.

## Define Authorization Requests

An `AuthorizationRequest` contains an operation, one primary resource, optional related resources, and optional typed facts. It never contains caller authority. Use lowercase stable names for operations, resource types, and facts. Use `ActionContext.Caller` and `ActionContext.Features` as the only caller and feature authority.

```csharp
var request = new AuthorizationRequest(
    "documents.read",
    new AuthorizationResource("document", documentId.ToString("D")),
    RelatedResources:
    [
        new AuthorizationResource("tenant", tenantId.ToString("D")),
    ]);
```

## Supply an Authoritative Policy

Implement `IAuthorizationPolicy` to replace the active policy. The policy receives the validated request and the authenticated `ActionContext`. Return an explicit denial for each unsupported operation. Propagate cancellation before storage access or other effects.

```csharp
public sealed class DocumentAuthorizationPolicy : IAuthorizationPolicy
{
    public ValueTask<AuthorizationDecision> EvaluateAsync(
        ActionContext<AuthorizationRequest> context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var allowed = context.Caller.Roles?.Contains("document-reader") == true;
        return ValueTask.FromResult(allowed
            ? AuthorizationDecision.Allow("role_allowed")
            : AuthorizationDecision.Deny(
                "role_denied",
                "The caller cannot read this document."));
    }
}
```

## Register the Policy

Call `AddAuthorizationPolicy<TPolicy>` from `ConfigureServices`. This method registers the scoped policy, exports `sharpclaw.authorization`, defines `authorization.evaluate`, and adds its stable terminal. Normal constructor injection remains available to the policy.

```csharp
public sealed class DocumentAuthorizationModule : ISharpClawModule
{
    public ModuleIdentity Identity { get; } = new(
        "document_authorization",
        "Document Authorization",
        "document_auth");

    public void ConfigureServices(IServiceCollection services) =>
        services.AddAuthorizationPolicy<DocumentAuthorizationPolicy>();
}
```

## Declare the Policy Export

The package manifest must declare the exact contract and service type. The host rejects a missing or changed service type. Only one enabled package can export this contract.

```json
{
  "exports": [
    {
      "contractName": "sharpclaw.authorization",
      "serviceType": "SharpClaw.Contracts.Kernel.AuthorizationContract",
      "optional": false
    }
  ]
}
```

## Use the Active Policy

Call `RequireAuthorization` in each package that needs authorization. Inject `HostAuthorizationEntry` into the guarded service. Evaluate the request with the active action or chat context. Complete authorization before protected work starts.

```csharp
public sealed class DocumentActionExecutor(HostAuthorizationEntry authorization)
{
    public async ValueTask ExecuteAsync(
        ActionContext<DocumentReadAction> context,
        CancellationToken cancellationToken)
    {
        var decision = await authorization.EvaluateAsync(
            context,
            new AuthorizationRequest(
                "documents.read",
                new AuthorizationResource(
                    "document",
                    context.Action.DocumentId.ToString("D"))),
            cancellationToken);

        if (!decision.Allowed)
            throw new UnauthorizedAccessException(decision.Message);

        await ReadDocumentAsync(context.Action.DocumentId, cancellationToken);
    }
}
```

## Add an Independent Restriction

Implement `IAuthorizationRestriction` when a package must reduce access without owning the policy. A restriction returns `Preserve` or `Deny`. It cannot return an allowance, replace the policy result, replace authenticated identity, or issue authority.

```csharp
public sealed class TenantRestriction : IAuthorizationRestriction
{
    public ValueTask<AuthorizationRestriction> EvaluateAsync(
        AuthorizationRestrictionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tenantMatches = context.Features.Contains("tenant.scope");
        return ValueTask.FromResult(tenantMatches
            ? AuthorizationRestriction.Preserve()
            : AuthorizationRestriction.Deny(
                "tenant_denied",
                "The caller cannot access this tenant."));
    }
}
```

## Register the Restriction

Call `AddAuthorizationRestriction<TRestriction>` with one stable lowercase identifier. The helper requests only `Inspect`, `Wrap`, and `Observe`. Each restriction can preserve the current flow or stop it with a denial.

```csharp
public void ConfigureServices(IServiceCollection services) =>
    services.AddAuthorizationRestriction<TenantRestriction>(
        "tenant-boundary",
        HookPriority.High);
```

## Declare the Restriction Requirement

The restriction manifest must require the exact authorization service type. It must request the exact `authorization.evaluate` hook capabilities. The host rejects omitted service types, incompatible requirements, and multiple authoritative providers.

```json
{
  "requires": [
    {
      "contractName": "sharpclaw.authorization",
      "serviceType": "SharpClaw.Contracts.Kernel.AuthorizationContract",
      "optional": false
    }
  ],
  "requestedHooks": [
    {
      "target": "authorization.evaluate",
      "effects": ["Inspect", "Wrap", "Observe"]
    }
  ]
}
```

## Extend or Replace Agent Orchestration

To replace Two Tier Permission, remove that package and add one package that exports `sharpclaw.authorization`. Context and Agents continue to use the same neutral contract. Use `AuthorizationRequestFactory` from Agent Orchestration Contracts when the policy needs its exact context and agent resource mappings.

## Keep Low-Level Control

`AuthorizationProtocol.Evaluate` exposes the exact typed descriptor for advanced hooks and tests. Its descriptor permits `Inspect`, `Wrap`, and `Observe`. It does not permit input replacement, result replacement, repeat, deferment, or cancellation. Use normal typed actions for additional policy operations instead of changing this contract.

## Test the Package

Compile the real package and manifest through `SharpClawModuleCompiler`. Verify the exact contract, action, terminal, and hook contributions. Test allowance, denial, cancellation, malformed requests, and pre-write rejection. Run both in-process and out-of-process host tests when the package supports both modes.
