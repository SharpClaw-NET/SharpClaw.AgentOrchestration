using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.ModuleSDK;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.TwoTierPermission;

public sealed class TwoTierPermissionModule : ISharpClawModule, ISharpClawApplicationModule
{
    public const string ModuleIdValue = "sharpclaw_two_tier_permission";
    public const string EvaluateTool = "perm_evaluate";
    public const string GrantTool = "perm_grant";
    public const string RevokeTool = "perm_revoke";
    public const string ApproveTool = "perm_approve";
    public const string PermissionChangedEvent = "permission.changed";
    public static readonly Guid ApiTerminalId = Guid.Parse("8f7be0a6-2f4d-5b72-9dc8-3ca4e9c2f101");
    public static readonly Guid ContextAccessTerminalId = Guid.Parse("8f7be0a6-2f4d-5b72-9dc8-3ca4e9c2f102");
    public static readonly Guid AgentAccessTerminalId = Guid.Parse("8f7be0a6-2f4d-5b72-9dc8-3ca4e9c2f103");

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

    public void Configure(ISharpClawModuleBuilder module)
    {
        module.Services.AddScoped<PermissionPolicyStore>();
        module.Services.AddScoped<TwoTierPermissionPolicy>();
        module.Services.AddScoped<PermissionToolHandler>();
        module.Services.AddScoped<IPermissionActionExecutor, PermissionActionExecutor>();
        module.Services.AddScoped<PermissionApiActionExecutor>();
        module.Services.AddScoped<PermissionApiActionTerminal>();
        module.Services.AddScoped<PermissionContextAccessActionTerminal>();
        module.Services.AddScoped<PermissionAgentAccessActionTerminal>();
        module.Services.AddScoped<PermissionEndpointContribution>();
        module.Services.AddScoped<IPermissionActionGateway, PermissionActionGateway>();
        module.Services.AddScoped<HostModuleActionEntry>();
        module.Services.AddScoped<IModuleActionPipeline, ModuleActionPipeline>();
        module.Services.AddScoped<PermissionGrantAuthorizationHook>();
        module.Services.AddSingleton<PermissionCliHandler>();

        module.Contracts.Export<PermissionModuleContract>("sharpclaw.permission");

        foreach (var storage in StorageContracts)
            module.Storage.Add(storage);

        module.Actions.Add(ApiDescriptor);
        module.Actions.Add(PermissionActionDescriptors.ContextAccess);
        module.Actions.Add(PermissionActionDescriptors.AgentAccess);
        module.AddActionEntry<PermissionApiAction, JsonElement, PermissionApiActionTerminal>(
            ApiDescriptor,
            ApiTerminalId);
        module.AddActionEntry<PermissionContextAccessAction, PermissionDecision, PermissionContextAccessActionTerminal>(
            PermissionActionDescriptors.ContextAccess,
            ContextAccessTerminalId);
        module.AddActionEntry<PermissionAgentAccessAction, PermissionDecision, PermissionAgentAccessActionTerminal>(
            PermissionActionDescriptors.AgentAccess,
            AgentAccessTerminalId);

        module.Actions.Add(new ActionDescriptor<PermissionEvaluateAction, PermissionDecision>(
            new("permission.evaluate"), 1, "permission",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Observe,
            true, false, RepeatPolicy, null, TimeSpan.FromSeconds(10))
        {
            SafePoints = SafePoints,
        });
        module.Actions.Add(new ActionDescriptor<PermissionGrantAction, bool>(
            new("permission.grant"), 1, "permission.administration",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            SafePoints = SafePoints,
        });
        module.Actions.Add(new ActionDescriptor<PermissionRevokeAction, bool>(
            new("permission.revoke"), 1, "permission.administration",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            SafePoints = SafePoints,
        });
        module.Actions.Add(new ActionDescriptor<PermissionApproveAction, bool>(
            new("permission.approve"), 1, "permission.approval",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            SafePoints = SafePoints,
        });
        module.Hooks.For(new SharpClawActionKey("permission.grant"))
            .Use<PermissionGrantAuthorizationHook>(new HookOrdering("permission.grant.authorization"));

        module.Events.Add(new EventDescriptor<PermissionChangedEvent>(
            new(PermissionChangedEvent), 1, "permission",
            EventInterceptionCapabilities.Inspect | EventInterceptionCapabilities.Observe,
            true, true)
        {
            DeliveryClasses = [EventDelivery.Durable],
        });

        module.Tools.Add<PermissionToolHandler>(new ToolDescriptor(
            EvaluateTool,
            "Evaluate both permission tiers for one context access request.",
            BuildEvaluateSchema(), ContainsSensitiveData: true));
        module.Tools.Add<PermissionToolHandler>(new ToolDescriptor(
            GrantTool,
            "Grant a capability and clearance to a subject.",
            BuildGrantSchema(), ContainsSensitiveData: true));
        module.Tools.Add<PermissionToolHandler>(new ToolDescriptor(
            RevokeTool,
            "Revoke a capability from a subject.",
            BuildRevokeSchema(), ContainsSensitiveData: true));
        module.Tools.Add<PermissionToolHandler>(new ToolDescriptor(
            ApproveTool,
            "Approve a same-level permission grant.",
            BuildApproveSchema(), ContainsSensitiveData: true));
    }

    public void ConfigureApplication(ISharpClawApplicationBuilder application)
    {
        foreach (ModuleEndpointRouteDescriptor route in PermissionEndpointContribution.EndpointRoutes)
            application.Endpoints.AddHttp<PermissionEndpointContribution>(route);
        foreach (var command in PermissionCliHandler.Commands)
        {
            application.Cli.Add<PermissionCliHandler>(new ModuleCliCommandDescriptor(
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

    public ValueTask StartAsync(ModuleStartContext context, CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken ct) => ValueTask.CompletedTask;

    public static IReadOnlyList<ModuleStorageContractDescriptor> StorageContracts =>
    [
        Storage(PermissionPolicyStore.PoliciesStorage, "Subject clearance and two-tier capability policy.",
            [
                new("subjectId", ModuleStorageIndexValueKind.String),
                new("clearance", ModuleStorageIndexValueKind.String),
                new("updatedAt", ModuleStorageIndexValueKind.DateTime),
            ]),
        Storage(PermissionPolicyStore.GrantsStorage, "Delegated and administratively issued resource grants.",
            [new("subjectId", ModuleStorageIndexValueKind.String), new("capability", ModuleStorageIndexValueKind.String), new("scope", ModuleStorageIndexValueKind.String)]),
        Storage(PermissionPolicyStore.ApprovalsStorage, "Approval records for same-level and delegated checks.",
            [new("subjectId", ModuleStorageIndexValueKind.String), new("capability", ModuleStorageIndexValueKind.String), new("scope", ModuleStorageIndexValueKind.String)]),
        Storage(PermissionPolicyStore.RolesStorage, "Permission roles and assigned subjects.",
            [new("name", ModuleStorageIndexValueKind.String), new("clearance", ModuleStorageIndexValueKind.String), new("updatedAt", ModuleStorageIndexValueKind.DateTime)]),
        Storage(PermissionPolicyStore.PermissionSetsStorage, "Reusable permission capability sets.",
            [new("name", ModuleStorageIndexValueKind.String), new("updatedAt", ModuleStorageIndexValueKind.DateTime)]),
    ];

    private static ModuleStorageContractDescriptor Storage(
        string name,
        string description,
        IReadOnlyList<ModuleStorageIndexDescriptor> indexes) =>
        new(ModuleIdValue, name,
            [
                new(ModuleStorageOperations.Get),
                new(ModuleStorageOperations.Upsert),
                new(ModuleStorageOperations.BatchUpsert),
                new(ModuleStorageOperations.Delete),
                new(ModuleStorageOperations.BatchDelete),
                new(ModuleStorageOperations.List),
                new(ModuleStorageOperations.Query),
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
