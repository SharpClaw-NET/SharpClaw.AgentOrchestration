using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.Context;

public sealed class ContextApiActionTerminal(
    ContextApiActionExecutor executor) : IHostActionEntryTerminal<ContextApiAction, JsonElement>
{
    public Guid TerminalId => ContextModule.ApiTerminalId;

    public ValueTask<JsonElement> InvokeAsync(
        ActionContext<ContextApiAction> context,
        CancellationToken ct) =>
        executor.ExecuteAsync(context, ct);
}

public sealed class ContextSteeringRecordActionTerminal(
    IContextSteeringActionExecutor executor) :
    IHostActionEntryTerminal<ContextRecordSteeringAction, ContextSteeringRecord>
{
    public Guid TerminalId => ContextModule.SteeringRecordTerminalId;

    public ValueTask<ContextSteeringRecord> InvokeAsync(
        ActionContext<ContextRecordSteeringAction> context,
        CancellationToken ct) =>
        new(executor.RecordAsync(context, ct));
}

public sealed class ContextSteeringListActionTerminal(
    IContextSteeringActionExecutor executor) :
    IHostActionEntryTerminal<ContextListSteeringAction, IReadOnlyList<ContextSteeringRecord>>
{
    public Guid TerminalId => ContextModule.SteeringListTerminalId;

    public ValueTask<IReadOnlyList<ContextSteeringRecord>> InvokeAsync(
        ActionContext<ContextListSteeringAction> context,
        CancellationToken ct) =>
        new(executor.ListAsync(context, ct));
}
