using System.Text.Json;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.Agents;

public sealed class AgentsCliHandler(IAgentsActionGateway gateway) : IModuleCliHandler
{
    public static IReadOnlyList<(string Name, string Operation)> Commands { get; } =
    [
        ("agents-list", AgentsApiOperations.ListAgents),
        ("agents-get", AgentsApiOperations.GetAgent),
        ("agents-delete", AgentsApiOperations.DeleteAgent),
        ("agents-role", AgentsApiOperations.AssignRole),
        ("agents-synchronize", AgentsApiOperations.SynchronizeAgent),
        ("agents-cost", AgentsApiOperations.GetCost),
        ("skills-list", AgentsApiOperations.ListSkills),
        ("skills-get", AgentsApiOperations.GetSkill),
        ("skills-save", AgentsApiOperations.SaveSkill),
        ("skills-delete", AgentsApiOperations.DeleteSkill),
        ("skills-access", AgentsApiOperations.AccessSkill),
        ("agents-memory-search", AgentsApiOperations.SearchMemory),
    ];

    public async ValueTask<ModuleCliResult> ExecuteAsync(
        ModuleCliInvocation invocation,
        CancellationToken ct)
    {
        if (!invocation.HostActionContext.Caller.Roles?.Any(role => role.Equals("admin", StringComparison.OrdinalIgnoreCase)
            || role.Equals("administrator", StringComparison.OrdinalIgnoreCase)) == true)
        {
            return Failure("An administrator role is required.");
        }

        var command = Commands.FirstOrDefault(item =>
            item.Name.Equals(invocation.Command, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(command.Name))
            return Failure($"Unknown Agents command '{invocation.Command}'.");

        try
        {
            var payload = BuildPayload(command.Operation, invocation.Arguments);
            var result = await gateway.ExecuteAsync(
                invocation.HostActionContext,
                command.Operation,
                payload,
                ct);
            return Success(result.GetRawText());
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure(exception.Message);
        }
        catch (ArgumentException exception)
        {
            return Failure(exception.Message);
        }
    }

    private static JsonElement BuildPayload(
        string operation,
        IReadOnlyList<string> arguments)
    {
        if (arguments.Count > 0 && arguments[0].TrimStart().StartsWith('{'))
            return JsonDocument.Parse(arguments[0]).RootElement.Clone();

        return operation switch
        {
            AgentsApiOperations.ListAgents
                or AgentsApiOperations.ListSkills => Empty(),
            AgentsApiOperations.GetAgent
                or AgentsApiOperations.DeleteAgent
                or AgentsApiOperations.SynchronizeAgent
                or AgentsApiOperations.GetCost => IdPayload("agentId", arguments),
            AgentsApiOperations.GetSkill
                or AgentsApiOperations.DeleteSkill
                or AgentsApiOperations.AccessSkill => IdPayload("skillId", arguments),
            AgentsApiOperations.AssignRole => JsonSerializer.SerializeToElement(new
            {
                agentId = arguments.ElementAtOrDefault(0),
                role = arguments.ElementAtOrDefault(1),
                assign = true,
            }),
            AgentsApiOperations.SearchMemory => JsonSerializer.SerializeToElement(new
            {
                agentId = arguments.ElementAtOrDefault(0),
                query = arguments.ElementAtOrDefault(1),
            }),
            AgentsApiOperations.SaveSkill =>
                throw new ArgumentException("A JSON document is required for skills-save."),
            _ => throw new ArgumentException("The Agents command arguments are invalid."),
        };
    }

    private static JsonElement IdPayload(string name, IReadOnlyList<string> arguments) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, string?>
        {
            [name] = arguments.ElementAtOrDefault(0),
        });

    private static JsonElement Empty() => JsonSerializer.SerializeToElement(new { });

    private static ModuleCliResult Success(string text) =>
        new(true, [new ModuleCliOutput("stdout", text)]);

    private static ModuleCliResult Failure(string text) =>
        new(false, [new ModuleCliOutput("stderr", text)], new ExecutionError("permission_denied", text));
}
