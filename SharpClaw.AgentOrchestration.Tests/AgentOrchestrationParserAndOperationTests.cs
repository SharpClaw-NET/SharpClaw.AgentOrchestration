using NUnit.Framework;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Modules.AgentOrchestration;

namespace SharpClaw.AgentOrchestration.Tests;

[TestFixture]
public sealed class AgentOrchestrationParserAndOperationTests
{
    [Test]
    public void DescriptorProviderExposesAgentOrchestrationOperationDescriptors()
    {
        var provider = new AgentOrchestrationOperationDescriptorProvider();
        var descriptors = provider.Descriptors.ToDictionary(
            d => d.MethodName ?? throw new InvalidOperationException("Operation descriptor is missing a method name."),
            StringComparer.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(provider.ModuleId, Is.EqualTo(AgentOrchestrationModule.ModuleIdValue));
            Assert.That(descriptors.Keys, Is.EquivalentTo(new[]
            {
                "Chat",
                "ChatStream",
                "ChatToThread",
                "Emit",
                "ParseResponse",
                "FindModel",
                "FindProvider",
                "FindAgent",
                "CreateAgent",
                "CreateThread",
                "CreateRole",
                "FindRole",
                "SetRolePermissions",
                "AssignRole",
                "CreateChannel",
                "FindChannel",
                "AddAllowedAgent",
            }));
            Assert.That(descriptors["Chat"].OperationKey, Is.EqualTo(AgentOrchestrationOperationKeys.Chat));
            Assert.That(descriptors["Chat"].ExpressionArgIndex, Is.EqualTo(1));
            Assert.That(descriptors["ChatStream"].OperationKey, Is.EqualTo(AgentOrchestrationOperationKeys.ChatStream));
            Assert.That(descriptors["Emit"].OperationKey, Is.EqualTo(AgentOrchestrationOperationKeys.Emit));
            Assert.That(descriptors["Emit"].FirstArgIsExpression, Is.True);
            Assert.That(descriptors["ParseResponse"].OperationKey, Is.EqualTo(AgentOrchestrationOperationKeys.ParseResponse));
            Assert.That(descriptors["ParseResponse"].CapturesGenericType, Is.True);
            Assert.That(descriptors["ParseResponse"].RequiresDeclaredGenericType, Is.True);
            Assert.That(descriptors.Values, Has.All.Matches<TaskOperationDescriptor>(
                d => d.OwnerId == AgentOrchestrationModule.ModuleIdValue));
        });
    }

    [Test]
    public void ParserExtensionRegistersTimerMappingAndAoTriggerHandlers()
    {
        var extension = TaskScriptingParserExtension.Instance;

        Assert.Multiple(() =>
        {
            Assert.That(extension.EventTriggerMappings["OnTimer"].TriggerKey,
                Is.EqualTo(TaskScriptingParserExtension.TimerTriggerKey));
            Assert.That(extension.EventTriggerMappings["OnTimer"].ModuleId,
                Is.EqualTo(AgentOrchestrationModule.ModuleIdValue));
            Assert.That(extension.TriggerAttributeHandlers.Keys, Is.SupersetOf(new[]
            {
                "Schedule",
                "OnStartup",
                "OnShutdown",
                "OnTaskCompleted",
                "OnTaskFailed",
                "OnTrigger",
                "OnEvent",
                "OnFileChanged",
            }));
            Assert.That(extension.OperationKeyMappings, Is.Empty);
            Assert.That(extension.SingleArgExpressionMethods, Is.Empty);
        });
    }

    [Test]
    public void TriggerHandlersPopulateScheduleEventFilesystemTaskAndCustomParameters()
    {
        var handlers = TaskScriptingParserExtension.Instance.TriggerAttributeHandlers;

        var schedule = handlers["Schedule"].Handle(new TestTriggerAttributeContext(
            "Schedule",
            positionalStrings: ["0 9 * * *"],
            namedStrings: new Dictionary<string, string> { ["Timezone"] = "America/New_York" }));
        var onEvent = handlers["OnEvent"].Handle(new TestTriggerAttributeContext(
            "OnEvent",
            positionalStrings: ["ModelAdded"],
            namedStrings: new Dictionary<string, string> { ["Filter"] = "provider=openai" }));
        var fileChanged = handlers["OnFileChanged"].Handle(new TestTriggerAttributeContext(
            "OnFileChanged",
            positionalStrings: ["/tmp/data"],
            namedStrings: new Dictionary<string, string> { ["Pattern"] = "*.json" },
            namedEnums: new Dictionary<string, string>
            {
                ["Events"] = "FileWatchEvent.Created | FileWatchEvent.Deleted",
            }));
        var taskCompleted = handlers["OnTaskCompleted"].Handle(new TestTriggerAttributeContext(
            "OnTaskCompleted",
            positionalStrings: ["IngestData"]));
        var taskFailed = handlers["OnTaskFailed"].Handle(new TestTriggerAttributeContext(
            "OnTaskFailed",
            positionalStrings: ["IngestData"]));
        var custom = handlers["OnTrigger"].Handle(new TestTriggerAttributeContext(
            "OnTrigger",
            positionalStrings: ["MyCustomSource"],
            namedStrings: new Dictionary<string, string> { ["Filter"] = "type=foo" }));

        Assert.Multiple(() =>
        {
            Assert.That(schedule!.TriggerKey, Is.EqualTo(TaskScriptingTriggerKeys.Cron));
            Assert.That(schedule.Parameters[TaskScriptingTriggerKeys.CronExpression], Is.EqualTo("0 9 * * *"));
            Assert.That(schedule.Parameters[TaskScriptingTriggerKeys.CronTimezone], Is.EqualTo("America/New_York"));
            Assert.That(onEvent!.TriggerKey, Is.EqualTo(AgentOrchestrationTriggerKeys.Event));
            Assert.That(onEvent.Parameters[AgentOrchestrationTriggerKeys.EventType], Is.EqualTo("ModelAdded"));
            Assert.That(onEvent.Parameters[AgentOrchestrationTriggerKeys.EventFilter], Is.EqualTo("provider=openai"));
            Assert.That(fileChanged!.TriggerKey, Is.EqualTo(FilesystemTriggerKeys.FileChanged));
            Assert.That(fileChanged.Parameters[FilesystemTriggerKeys.WatchPath], Is.EqualTo("/tmp/data"));
            Assert.That(fileChanged.Parameters[FilesystemTriggerKeys.FilePattern], Is.EqualTo("*.json"));
            Assert.That(fileChanged.Parameters[FilesystemTriggerKeys.FileEvents],
                Is.EqualTo((FileWatchEvent.Created | FileWatchEvent.Deleted).ToString()));
            Assert.That(taskCompleted!.TriggerKey, Is.EqualTo(TaskScriptingTriggerKeys.TaskCompleted));
            Assert.That(taskCompleted.Parameters[TaskScriptingTriggerKeys.SourceTaskName], Is.EqualTo("IngestData"));
            Assert.That(taskFailed!.TriggerKey, Is.EqualTo(TaskScriptingTriggerKeys.TaskFailed));
            Assert.That(taskFailed.Parameters[TaskScriptingTriggerKeys.SourceTaskName], Is.EqualTo("IngestData"));
            Assert.That(custom!.TriggerKey, Is.EqualTo("MyCustomSource"));
            Assert.That(custom.Parameters[TaskScriptingTriggerKeys.CustomSourceFilter], Is.EqualTo("type=foo"));
        });
    }

    [Test]
    public void TriggerHandlersPopulateStartupShutdownAndDefaultFileEvents()
    {
        var handlers = TaskScriptingParserExtension.Instance.TriggerAttributeHandlers;

        var startup = handlers["OnStartup"].Handle(new TestTriggerAttributeContext("OnStartup"));
        var shutdown = handlers["OnShutdown"].Handle(new TestTriggerAttributeContext("OnShutdown"));
        var fileChanged = handlers["OnFileChanged"].Handle(new TestTriggerAttributeContext(
            "OnFileChanged",
            positionalStrings: ["/tmp/data"]));

        Assert.Multiple(() =>
        {
            Assert.That(startup!.TriggerKey, Is.EqualTo(TaskScriptingTriggerKeys.Startup));
            Assert.That(shutdown!.TriggerKey, Is.EqualTo(TaskScriptingTriggerKeys.Shutdown));
            Assert.That(fileChanged!.TriggerKey, Is.EqualTo(FilesystemTriggerKeys.FileChanged));
            Assert.That(fileChanged.Parameters[FilesystemTriggerKeys.FileEvents],
                Is.EqualTo(FileWatchEvent.Any.ToString()));
        });
    }

    [Test]
    public void AgentOrchestrationOperationKeysUseModuleNamespace()
    {
        var keys = new[]
        {
            AgentOrchestrationOperationKeys.Chat,
            AgentOrchestrationOperationKeys.ChatStream,
            AgentOrchestrationOperationKeys.ChatToThread,
            AgentOrchestrationOperationKeys.Emit,
            AgentOrchestrationOperationKeys.ParseResponse,
            AgentOrchestrationOperationKeys.FindModel,
            AgentOrchestrationOperationKeys.FindProvider,
            AgentOrchestrationOperationKeys.FindAgent,
            AgentOrchestrationOperationKeys.CreateAgent,
            AgentOrchestrationOperationKeys.CreateThread,
            AgentOrchestrationOperationKeys.CreateRole,
            AgentOrchestrationOperationKeys.FindRole,
            AgentOrchestrationOperationKeys.SetRolePermissions,
            AgentOrchestrationOperationKeys.AssignRole,
            AgentOrchestrationOperationKeys.CreateChannel,
            AgentOrchestrationOperationKeys.FindChannel,
            AgentOrchestrationOperationKeys.AddAllowedAgent,
        };

        Assert.That(keys, Has.All.StartsWith("sharpclaw_agent_orchestration."));
        Assert.That(keys.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(keys.Length));
    }

    [Test]
    public void AgentOrchestrationExecutorHandlesOnlyModuleOwnedOperationKeys()
    {
        var executor = new AgentOrchestrationTaskOperationExecutor();

        Assert.Multiple(() =>
        {
            Assert.That(executor.CanExecute(AgentOrchestrationOperationKeys.ParseResponse), Is.True);
            Assert.That(executor.CanExecute(AgentOrchestrationOperationKeys.Chat), Is.True);
            Assert.That(executor.CanExecute(AgentOrchestrationOperationKeys.AddAllowedAgent), Is.True);
            Assert.That(executor.CanExecute("sharpclaw.task.log"), Is.False);
            Assert.That(executor.CanExecute("other.module.operation"), Is.False);
        });
    }

    [Test]
    public async Task EmitOperationWritesTaskOutputThroughExecutionContext()
    {
        var executor = new AgentOrchestrationTaskOperationExecutor();
        var context = new RecordingTaskOperationExecutionContext();

        var handled = await executor.ExecuteAsync(
            AgentOrchestrationOperationKeys.Emit,
            context,
            arguments: null,
            expression: "task-output",
            resultVariable: null);

        Assert.Multiple(() =>
        {
            Assert.That(handled, Is.True);
            Assert.That(context.Outputs, Is.EqualTo(new[] { "task-output" }));
            Assert.That(context.Logs, Is.Empty);
            Assert.That(context.Variables, Is.Empty);
        });
    }

    private sealed class RecordingTaskOperationExecutionContext : ITaskOperationExecutionContext
    {
        private static readonly ITaskEventHandler[] EmptyHandlers = [];

        public Guid InstanceId { get; } = Guid.NewGuid();
        public Guid ChannelId { get; private set; } = Guid.NewGuid();
        public CancellationToken CancellationToken => CancellationToken.None;
        public IServiceProvider Services { get; } = new EmptyServiceProvider();
        public IDictionary<string, object?> Variables { get; } =
            new Dictionary<string, object?>(StringComparer.Ordinal);
        public IReadOnlyList<ITaskEventHandler> EventHandlers => EmptyHandlers;
        public List<string> Logs { get; } = [];
        public List<string?> Outputs { get; } = [];

        public string ResolveExpression(string expression) =>
            Variables.TryGetValue(expression, out var value)
                ? value?.ToString() ?? string.Empty
                : expression;

        public Task AppendLogAsync(string message)
        {
            Logs.Add(message);
            return Task.CompletedTask;
        }

        public Task WriteOutputAsync(string? outputJson)
        {
            Outputs.Add(outputJson);
            return Task.CompletedTask;
        }

        public void SetChannelId(Guid channelId) => ChannelId = channelId;

        public Task<TaskStatementResult> ExecuteStatementsAsync(
            IReadOnlyList<ITaskStatementInvocation> steps,
            CancellationToken cancellationToken) =>
            Task.FromResult(TaskStatementResult.Continue);

        public bool EvaluateCondition(string? expression) =>
            bool.TryParse(expression, out var value) && value;

        public void RegisterEventHandler(
            string moduleTriggerKey,
            string? parameterName,
            IReadOnlyList<ITaskStatementInvocation> body)
        {
        }

        public Task WaitIfPausedAsync() => Task.CompletedTask;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class TestTriggerAttributeContext : TaskTriggerAttributeContext
    {
        private readonly IReadOnlyList<string?> _positionalStrings;
        private readonly IReadOnlyDictionary<string, string> _namedStrings;
        private readonly IReadOnlyDictionary<string, string> _namedEnums;

        public TestTriggerAttributeContext(
            string attributeName,
            IReadOnlyList<string?>? positionalStrings = null,
            IReadOnlyDictionary<string, string>? namedStrings = null,
            IReadOnlyDictionary<string, string>? namedEnums = null,
            int line = 7)
        {
            AttributeName = attributeName;
            Line = line;
            _positionalStrings = positionalStrings ?? [];
            _namedStrings = namedStrings ?? new Dictionary<string, string>();
            _namedEnums = namedEnums ?? new Dictionary<string, string>();
        }

        public override string AttributeName { get; }
        public override int Line { get; }
        public override int ArgumentCount => _positionalStrings.Count + _namedStrings.Count + _namedEnums.Count;

        public override string? GetStringArg(int index) =>
            index >= 0 && index < _positionalStrings.Count ? _positionalStrings[index] : null;

        public override int? GetIntArg(int index) => null;

        public override string? GetNamedStringArg(string name) =>
            _namedStrings.TryGetValue(name, out var value) ? value : null;

        public override int? GetNamedIntArg(string name) => null;

        public override double? GetNamedDoubleArg(string name) => null;

        public override T? GetNamedEnumArg<T>(string name) where T : struct
        {
            if (!_namedEnums.TryGetValue(name, out var text))
                return null;

            var value = 0;
            foreach (var segment in text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var enumName = segment.Contains('.')
                    ? segment[(segment.LastIndexOf('.') + 1)..]
                    : segment;
                value |= Convert.ToInt32(Enum.Parse(typeof(T), enumName, ignoreCase: true));
            }

            return (T)Enum.ToObject(typeof(T), value);
        }

        public override string? GetRawArgText(int index) => GetStringArg(index);

        public override void Report(
            TaskTriggerAttributeDiagnosticSeverity severity,
            string code,
            string message)
        {
        }
    }
}
