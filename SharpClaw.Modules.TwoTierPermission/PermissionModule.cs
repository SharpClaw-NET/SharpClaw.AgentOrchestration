using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
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

    private static readonly ActionRepeatPolicy RepeatPolicy =
        new(ActionRepeatKind.Receipted, 3, TimeSpan.FromMilliseconds(100), "permission");
    private static readonly IReadOnlyList<ActionSafePoint> SafePoints =
    [
        ActionSafePoint.BeforeTerminal,
        ActionSafePoint.AfterTerminal,
        ActionSafePoint.BeforeCommit,
        ActionSafePoint.AfterCommit,
    ];

    public ModuleIdentity Identity { get; } = new(
        ModuleIdValue,
        "SharpClaw Two Tier Permission",
        "perm");

    public void Configure(ISharpClawModuleBuilder module)
    {
        module.Services.AddScoped<PermissionPolicyStore>();
        module.Services.AddScoped<TwoTierPermissionPolicy>();
        module.Services.AddScoped<IContextAccessPolicy>(sp =>
            sp.GetRequiredService<TwoTierPermissionPolicy>());
        module.Services.AddScoped<IAgentAccessPolicy>(sp =>
            sp.GetRequiredService<TwoTierPermissionPolicy>());
        module.Services.AddScoped<PermissionToolHandler>();
        module.Services.AddScoped<IPermissionActionExecutor, PermissionActionExecutor>();
        module.Services.AddScoped<PermissionGrantAuthorizationHook>();
        module.Services.AddScoped<PermissionCliHandler>();

        module.Contracts.Export<PermissionModuleContract>("sharpclaw.permission");

        foreach (var storage in StorageContracts)
            module.Storage.Add(storage);

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
        application.Cli.Add<PermissionCliHandler>(new ModuleCliCommandDescriptor(
            "perm-grant",
            ["permission-grant"],
            "Grant a permission capability.",
            new JsonSchemaReference("sharpclaw.permission.cli.arguments", 1),
            new JsonSchemaReference("sharpclaw.permission.cli.result", 1),
            RequiresAdministrator: true));
        application.Cli.Add<PermissionCliHandler>(new ModuleCliCommandDescriptor(
            "perm-approve",
            ["permission-approve"],
            "Approve a permission grant.",
            new JsonSchemaReference("sharpclaw.permission.approve.arguments", 1),
            new JsonSchemaReference("sharpclaw.permission.cli.result", 1)));
    }

    public ValueTask StartAsync(ModuleStartContext context, CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken ct) => ValueTask.CompletedTask;

    public static IReadOnlyList<ModuleStorageContractDescriptor> StorageContracts =>
    [
        Storage(PermissionPolicyStore.PoliciesStorage, "Subject clearance and two-tier capability policy.",
            [new("subjectId", ModuleStorageIndexValueKind.String), new("clearance", ModuleStorageIndexValueKind.String)]),
        Storage(PermissionPolicyStore.GrantsStorage, "Delegated and administratively issued resource grants.",
            [new("subjectId", ModuleStorageIndexValueKind.String), new("capability", ModuleStorageIndexValueKind.String), new("scope", ModuleStorageIndexValueKind.String)]),
        Storage(PermissionPolicyStore.ApprovalsStorage, "Approval records for same-level and delegated checks.",
            [new("subjectId", ModuleStorageIndexValueKind.String), new("capability", ModuleStorageIndexValueKind.String), new("scope", ModuleStorageIndexValueKind.String)]),
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
