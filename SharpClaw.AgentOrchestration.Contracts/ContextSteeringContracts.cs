using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;

namespace SharpClaw.Modules.AgentOrchestration.Contracts;

public sealed record ContextSteeringRecord(
    Guid Id,
    Guid ChannelId,
    Guid? ThreadId,
    string Source,
    string Category,
    string Summary,
    string? Details,
    string ClientType,
    RequestPrincipal Caller,
    DateTimeOffset CreatedAt);

public sealed record ContextRecordSteeringAction(
    Guid ChannelId,
    Guid? ThreadId,
    string Source,
    string Category,
    string Summary,
    string? Details,
    string ClientType);

public sealed record ContextListSteeringAction(
    Guid ChannelId,
    Guid? ThreadId = null,
    int MaxRecords = 50);

public static class ContextSteeringActionKeys
{
    public const string Record = "context.steering.record";
    public const string List = "context.steering.list";
}

public static class ContextSteeringActionDescriptors
{
    private static readonly ActionRepeatPolicy RepeatPolicy =
        new(ActionRepeatKind.Idempotent, 3, TimeSpan.FromMilliseconds(50), "context-steering");

    private static readonly IReadOnlyList<ActionSafePoint> SafePoints =
    [
        ActionSafePoint.BeforeTerminal,
        ActionSafePoint.AfterTerminal,
        ActionSafePoint.BeforeCommit,
        ActionSafePoint.AfterCommit,
    ];

    public static readonly ActionDescriptor<ContextRecordSteeringAction, ContextSteeringRecord> Record =
        new(new(ContextSteeringActionKeys.Record), 1, "context.steering",
            ActionInterceptionCapabilities.Inspect
            | ActionInterceptionCapabilities.Cancel
            | ActionInterceptionCapabilities.Observe,
            true, true, RepeatPolicy, null, TimeSpan.FromSeconds(30))
        {
            InputSchema = ModuleSchemaIdentity.ActionInput(
                new SharpClawActionKey(ContextSteeringActionKeys.Record),
                1,
                typeof(ContextRecordSteeringAction)),
            ResultSchema = ModuleSchemaIdentity.ActionResult(
                new SharpClawActionKey(ContextSteeringActionKeys.Record),
                1,
                typeof(ContextSteeringRecord)),
            SafePoints = SafePoints,
        };

    public static readonly ActionDescriptor<ContextListSteeringAction, IReadOnlyList<ContextSteeringRecord>> List =
        new(new(ContextSteeringActionKeys.List), 1, "context.steering",
            ActionInterceptionCapabilities.Inspect
            | ActionInterceptionCapabilities.Cancel
            | ActionInterceptionCapabilities.Observe,
            true, false, RepeatPolicy, null, TimeSpan.FromSeconds(10))
        {
            InputSchema = ModuleSchemaIdentity.ActionInput(
                new SharpClawActionKey(ContextSteeringActionKeys.List),
                1,
                typeof(ContextListSteeringAction)),
            ResultSchema = ModuleSchemaIdentity.ActionResult(
                new SharpClawActionKey(ContextSteeringActionKeys.List),
                1,
                typeof(IReadOnlyList<ContextSteeringRecord>)),
            SafePoints = SafePoints,
        };
}
