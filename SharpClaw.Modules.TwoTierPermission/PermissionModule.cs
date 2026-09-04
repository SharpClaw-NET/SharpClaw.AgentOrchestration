using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Persistence;
using SharpClaw.ModuleSDK;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.TwoTierPermission;

public sealed class TwoTierPermissionModule : ISharpClawModule
{
    public const string ModuleIdValue = "sharpclaw_two_tier_permission";
    public const string EvaluateTool = "perm_evaluate";
    public const string GrantTool = "perm_grant";
    public const string RevokeTool = "perm_revoke";
    public const string ApproveTool = "perm_approve";
    public const string PermissionChangedEvent = "permission.changed";
    public static readonly Guid ApiTerminalId = Guid.Parse("8f7be0a6-2f4d-5b72-9dc8-3ca4e9c2f101");

    private static readonly ActionRepeatPolicy RepeatPolicy =
        new(ActionRepeatKind.Receipted, 3, TimeSpan.FromMilliseconds(100), "permission");
    private static readonly IReadOnlyList<ActionSafePoint> SafePoints =
    [
        ActionSafePoint.BeforeTerminal,
        ActionSafePoint.AfterTerminal,
        ActionSafePoint.BeforeCommit,
        ActionSafePoint.AfterCommit,
    ];

    public static readonly ActionDescriptor<PermissionApiAction, JsonElement> ApiDescriptor =
        new(new("permission.api.dispatch"), 1, "permission.api",
            ActionInterceptionCapabilities.Inspect
            | ActionInterceptionCapabilities.Cancel
            | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            InputSchema = new JsonSchemaReference(
                "sharpclaw.kernel.action.input.permission.api.dispatch",
                1,
                "9730C734344C8CDCC030B54D093217D8AD4038346CC0AB54494A00FD1A346D43"),
            ResultSchema = new JsonSchemaReference(
                "sharpclaw.kernel.action.result.permission.api.dispatch",
                1,
                "6FC66027153DC70AF18F195B681CFA9EC51D26D0528ADC664BBC10395E07A379"),
            SafePoints = SafePoints,
        };

    public ModuleIdentity Identity { get; } = new(
        ModuleIdValue,
        "SharpClaw Two Tier Permission",
        "perm");

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<PermissionPolicyStore>();
        services.AddScoped<TwoTierPermissionPolicy>();
        services.AddScoped<PermissionToolHandler>();
        services.AddScoped<PermissionApiActionExecutor>();
        services.AddScoped<PermissionEndpointContribution>();
        services.AddScoped<IPermissionActionGateway, PermissionActionGateway>();
        services.AddScoped<HostActionInvoker>();
        services.AddScoped<PermissionGrantAuthorizationHook>();
        services.AddSingleton<PermissionCliHandler>();

        services.AddAuthorizationPolicy<TwoTierAuthorizationPolicy>();

        foreach (var storage in StorageContracts)
            services.AddStorage(storage);

        services.AddAction(ApiDescriptor)
            .UseTerminal<PermissionApiActionTerminal>(ApiTerminalId);

        services.AddAction(new ActionDescriptor<PermissionEvaluateAction, TwoTierPermissionDecision>(
            new("permission.evaluate"), 1, "permission",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Observe,
            true, false, RepeatPolicy, null, TimeSpan.FromSeconds(10))
        {
            SafePoints = SafePoints,
        });
        services.AddAction(new ActionDescriptor<PermissionGrantAction, bool>(
            new("permission.grant"), 1, "permission.administration",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            SafePoints = SafePoints,
        });
        services.AddAction(new ActionDescriptor<PermissionRevokeAction, bool>(
            new("permission.revoke"), 1, "permission.administration",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            SafePoints = SafePoints,
        });
        services.AddAction(new ActionDescriptor<PermissionApproveAction, bool>(
            new("permission.approve"), 1, "permission.approval",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            SafePoints = SafePoints,
        });
        services.OnAction(new SharpClawActionKey("permission.grant"))
            .Use<PermissionGrantAuthorizationHook>(new HookOrdering("permission.grant.authorization"));

        services.AddEvent(new EventDescriptor<PermissionChangedEvent>(
            new(PermissionChangedEvent), 1, "permission",
            EventInterceptionCapabilities.Inspect | EventInterceptionCapabilities.Observe,
            true, true)
        {
            DeliveryClasses = [EventDelivery.Durable],
        });

        services.AddTool<PermissionToolHandler>(new ToolDescriptor(
            EvaluateTool,
            "Evaluate both permission tiers for one context access request.",
            BuildEvaluateSchema(), ContainsSensitiveData: true));
        services.AddTool<PermissionToolHandler>(new ToolDescriptor(
            GrantTool,
            "Grant a capability and clearance to a subject.",
            BuildGrantSchema(), ContainsSensitiveData: true));
        services.AddTool<PermissionToolHandler>(new ToolDescriptor(
            RevokeTool,
            "Revoke a capability from a subject.",
            BuildRevokeSchema(), ContainsSensitiveData: true));
        services.AddTool<PermissionToolHandler>(new ToolDescriptor(
            ApproveTool,
            "Approve a same-level permission grant.",
            BuildApproveSchema(), ContainsSensitiveData: true));
        foreach (EndpointRouteDescriptor route in PermissionEndpointContribution.EndpointRoutes)
            services.AddHttpEndpoint<PermissionEndpointContribution>(route);
        foreach (var command in PermissionCliHandler.Commands)
        {
            services.AddCliCommand<PermissionCliHandler>(new CliCommandDescriptor(
                command.Name,
                command.Name switch
                {
                    "perm-grant" => ["permission-grant"],
                    "perm-approve" => ["permission-approve"],
                    _ => [],
                },
                $"Execute the permission {command.Operation} operation.",
                new JsonSchemaReference("sharpclaw.permission.cli.arguments", 1),
                new JsonSchemaReference("sharpclaw.permission.cli.result", 1),
                RequiresAdministrator: command.Operation is not PermissionApiOperations.Evaluate));
        }
    }

    public ValueTask StartAsync(ServiceStartContext context, CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken ct) => ValueTask.CompletedTask;

    public static IReadOnlyList<ScopedStorageContractDescriptor> StorageContracts =>
    [
        Storage(PermissionPolicyStore.PoliciesStorage, "Subject clearance and two-tier capability policy.",
            [
                new("subjectId", ScopedStorageIndexValueKind.String),
                new("clearance", ScopedStorageIndexValueKind.String),
                new("updatedAt", ScopedStorageIndexValueKind.DateTime),
            ]),
        Storage(PermissionPolicyStore.GrantsStorage, "Delegated and administratively issued resource grants.",
            [new("subjectId", ScopedStorageIndexValueKind.String), new("capability", ScopedStorageIndexValueKind.String), new("scope", ScopedStorageIndexValueKind.String)]),
        Storage(PermissionPolicyStore.ApprovalsStorage, "Approval records for same-level and delegated checks.",
            [new("subjectId", ScopedStorageIndexValueKind.String), new("capability", ScopedStorageIndexValueKind.String), new("scope", ScopedStorageIndexValueKind.String)]),
        Storage(PermissionPolicyStore.RolesStorage, "Permission roles and assigned subjects.",
            [new("name", ScopedStorageIndexValueKind.String), new("clearance", ScopedStorageIndexValueKind.String), new("updatedAt", ScopedStorageIndexValueKind.DateTime)]),
        Storage(PermissionPolicyStore.PermissionSetsStorage, "Reusable permission capability sets.",
            [new("name", ScopedStorageIndexValueKind.String), new("updatedAt", ScopedStorageIndexValueKind.DateTime)]),
    ];

    private static ScopedStorageContractDescriptor Storage(
        string name,
        string description,
        IReadOnlyList<ScopedStorageIndexDescriptor> indexes) =>
        new(ModuleIdValue, name,
            [
                new(ScopedStorageOperations.Get),
                new(ScopedStorageOperations.Upsert),
                new(ScopedStorageOperations.BatchUpsert),
                new(ScopedStorageOperations.Delete),
                new(ScopedStorageOperations.BatchDelete),
                new(ScopedStorageOperations.List),
                new(ScopedStorageOperations.Query),
            ], description, indexes, 524_288, 500);

    private static JsonElement BuildEvaluateSchema() => JsonDocument.Parse("""
        {"type":"object","properties":{"subjectId":{"type":"string"},"channelId":{"type":"string"},"ownerAgentId":{"type":"string"},"allowedAgentIds":{"type":"array","items":{"type":"string"}},"defaultContextAgentId":{"type":"string"},"contextAllowedAgentIds":{"type":"array","items":{"type":"string"}},"sourceChannelOptedIn":{"type":"boolean"}},"additionalProperties":false}
        """).RootElement.Clone();

    private static JsonElement BuildGrantSchema() => JsonDocument.Parse("""
        {"type":"object","properties":{"subjectId":{"type":"string"},"capability":{"type":"string"},"scope":{"type":"string"},"clearance":{"type":"string"},"requireSourceOptIn":{"type":"boolean"}},"required":["subjectId","capability","clearance"],"additionalProperties":false}
        """).RootElement.Clone();

    private static JsonElement BuildRevokeSchema() => JsonDocument.Parse("""
        {"type":"object","properties":{"subjectId":{"type":"string"},"capability":{"type":"string"},"scope":{"type":"string"}},"required":["subjectId","capability"],"additionalProperties":false}
        """).RootElement.Clone();

    private static JsonElement BuildApproveSchema() => JsonDocument.Parse("""
        {"type":"object","properties":{"subjectId":{"type":"string"},"capability":{"type":"string"},"scope":{"type":"string"},"expiresAt":{"type":"string"}},"required":["subjectId","capability"],"additionalProperties":false}
        """).RootElement.Clone();
}
