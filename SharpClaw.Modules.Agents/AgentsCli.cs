using System.Text.Json;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.Agents;

public sealed class AgentsCliHandler(AgentsCatalog catalog) : IModuleCliHandler
{
    public async ValueTask<ModuleCliResult> ExecuteAsync(
        ModuleCliInvocation invocation,
        CancellationToken ct)
    {
        if (!invocation.Caller.Roles?.Any(role => role.Equals("admin", StringComparison.OrdinalIgnoreCase)
            || role.Equals("administrator", StringComparison.OrdinalIgnoreCase)) == true)
        {
            return Failure("An administrator role is required.");
        }

        if (invocation.Command.Equals("agents-list", StringComparison.OrdinalIgnoreCase))
            return Success(JsonSerializer.Serialize(await catalog.ListAgentsAsync(ct)));
        if (invocation.Command.Equals("skills-list", StringComparison.OrdinalIgnoreCase))
            return Success(JsonSerializer.Serialize(await catalog.ListSkillsAsync(ct)));
        return Failure($"Unknown Agents command '{invocation.Command}'.");
    }

    private static ModuleCliResult Success(string text) =>
        new(true, [new ModuleCliOutput("stdout", text)]);

    private static ModuleCliResult Failure(string text) =>
        new(false, [new ModuleCliOutput("stderr", text)], new ExecutionError("permission_denied", text));
}
