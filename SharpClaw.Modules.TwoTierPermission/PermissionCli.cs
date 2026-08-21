using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.TwoTierPermission;

public sealed class PermissionCliHandler(IServiceScopeFactory scopeFactory) : IModuleCliHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static IReadOnlyList<(string Name, string Operation)> Commands { get; } =
    [
        ("perm-evaluate", PermissionApiOperations.Evaluate),
        ("perm-grant", PermissionApiOperations.Grant),
        ("perm-revoke", PermissionApiOperations.Revoke),
        ("perm-approve", PermissionApiOperations.Approve),
        ("perm-policy-list", PermissionApiOperations.ListPolicies),
        ("perm-policy-get", PermissionApiOperations.GetPolicy),
        ("perm-policy-save", PermissionApiOperations.SavePolicy),
        ("perm-policy-delete", PermissionApiOperations.DeletePolicy),
        ("perm-role-list", PermissionApiOperations.ListRoles),
        ("perm-role-get", PermissionApiOperations.GetRole),
        ("perm-role-save", PermissionApiOperations.SaveRole),
        ("perm-role-delete", PermissionApiOperations.DeleteRole),
        ("perm-role-assign", PermissionApiOperations.AssignRole),
        ("perm-set-list", PermissionApiOperations.ListPermissionSets),
        ("perm-set-get", PermissionApiOperations.GetPermissionSet),
        ("perm-set-save", PermissionApiOperations.SavePermissionSet),
        ("perm-set-delete", PermissionApiOperations.DeletePermissionSet),
        ("perm-set-assign", PermissionApiOperations.AssignPermissionSet),
    ];

    public async ValueTask<ModuleCliResult> ExecuteAsync(
        ModuleCliInvocation invocation,
        CancellationToken ct)
    {
        var command = Commands.FirstOrDefault(item =>
            item.Name.Equals(invocation.Command, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(command.Name))
            return Failure($"Unknown permission command '{invocation.Command}'.");

        try
        {
            var payload = BuildPayload(command.Operation, invocation.Arguments);
            using var scope = scopeFactory.CreateScope();
            var gateway = scope.ServiceProvider.GetRequiredService<IPermissionActionGateway>();
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
            PermissionApiOperations.Evaluate => Payload(
                ("subjectId", arguments.ElementAtOrDefault(0)),
                ("capability", arguments.ElementAtOrDefault(1)),
                ("scope", arguments.ElementAtOrDefault(2) ?? "global")),
            PermissionApiOperations.Grant => JsonSerializer.SerializeToElement(
                new PermissionGrantAction(
                    Required(arguments, 0, "subject id"),
                    Required(arguments, 1, "capability"),
                    arguments.ElementAtOrDefault(3) ?? "global",
                    Enum.Parse<PermissionClearance>(Required(arguments, 2, "clearance"), true)),
                JsonOptions),
            PermissionApiOperations.Revoke => Payload(
                ("subjectId", arguments.ElementAtOrDefault(0)),
                ("capability", arguments.ElementAtOrDefault(1)),
                ("scope", arguments.ElementAtOrDefault(2) ?? "global")),
            PermissionApiOperations.Approve => JsonSerializer.SerializeToElement(
                new PermissionApproveAction(
                    Required(arguments, 0, "subject id"),
                    Required(arguments, 1, "capability"),
                    arguments.ElementAtOrDefault(2) ?? "global"),
                JsonOptions),
            PermissionApiOperations.ListPolicies
                or PermissionApiOperations.ListRoles
                or PermissionApiOperations.ListPermissionSets => Empty(),
            PermissionApiOperations.GetPolicy
                or PermissionApiOperations.DeletePolicy => Payload(
                    ("subjectId", arguments.ElementAtOrDefault(0))),
            PermissionApiOperations.GetRole
                or PermissionApiOperations.DeleteRole => Payload(
                    ("roleId", arguments.ElementAtOrDefault(0))),
            PermissionApiOperations.GetPermissionSet
                or PermissionApiOperations.DeletePermissionSet => Payload(
                    ("permissionSetId", arguments.ElementAtOrDefault(0))),
            PermissionApiOperations.AssignRole => JsonSerializer.SerializeToElement(new
            {
                roleId = arguments.ElementAtOrDefault(0),
                subjectId = arguments.ElementAtOrDefault(1),
                assign = true,
            }),
            PermissionApiOperations.AssignPermissionSet => JsonSerializer.SerializeToElement(new
            {
                permissionSetId = arguments.ElementAtOrDefault(0),
                subjectId = arguments.ElementAtOrDefault(1),
                assign = true,
            }),
            PermissionApiOperations.SavePolicy
                or PermissionApiOperations.SaveRole
                or PermissionApiOperations.SavePermissionSet =>
                throw new ArgumentException("A JSON document is required for save commands."),
            _ => throw new ArgumentException("The permission command arguments are invalid."),
        };
    }

    private static string Required(
        IReadOnlyList<string> arguments,
        int index,
        string name) =>
        arguments.ElementAtOrDefault(index)
        ?? throw new ArgumentException($"{name} is required.");

    private static JsonElement Payload(params (string Name, string? Value)[] values) =>
        JsonSerializer.SerializeToElement(values
            .Where(item => item.Value is not null)
            .ToDictionary(item => item.Name, item => item.Value));

    private static JsonElement Empty() => JsonSerializer.SerializeToElement(new { });

    private static ModuleCliResult Success(string text) =>
        new(true, [new ModuleCliOutput("stdout", text)]);

    private static ModuleCliResult Failure(string text) =>
        new(false, [new ModuleCliOutput("stderr", text)], new ExecutionError("permission_denied", text));
}
