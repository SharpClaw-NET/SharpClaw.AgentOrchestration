using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Modules.AgentOrchestration;
using SharpClaw.Modules.AgentOrchestration.Models;
using SharpClaw.Modules.AgentOrchestration.ScheduledJobs;
using SharpClaw.Modules.AgentOrchestration.Services;

namespace SharpClaw.AgentOrchestration.Tests;

[TestFixture]
public sealed class ScheduledJobWorkerBehaviorTests
{
    [Test]
    public async Task ScheduledJobWorker_ProcessDueJobs_LaunchesBoundTaskWithParameters()
    {
        await using var host = ScheduledJobHost.Create();
        var taskDefinitionId = Guid.NewGuid();
        var callerAgentId = Guid.NewGuid();
        var due = DateTimeOffset.UtcNow.AddSeconds(-1);
        await host.Store.CreateAsync(new ScheduledJobDB
        {
            Id = Guid.NewGuid(),
            Name = "due-job",
            TaskDefinitionId = taskDefinitionId,
            CallerAgentId = callerAgentId,
            ParameterValuesJson = """{"Topic":"scheduled"}""",
            NextRunAt = due,
            RepeatInterval = TimeSpan.FromMinutes(5),
        });

        await host.Worker.ProcessDueJobsAsync(CancellationToken.None);

        var launch = host.Launcher.Launches.Single();
        Assert.Multiple(() =>
        {
            Assert.That(launch.TaskDefinitionId, Is.EqualTo(taskDefinitionId));
            Assert.That(launch.CallerAgentId, Is.EqualTo(callerAgentId));
            Assert.That(launch.ParameterValues["Topic"], Is.EqualTo("scheduled"));
            Assert.That(launch.ChannelId, Is.Null);
            Assert.That(launch.ContextId, Is.Null);
        });

        var updated = await host.ReadScheduledJobAsync("due-job");
        Assert.Multiple(() =>
        {
            Assert.That(updated.Status, Is.EqualTo(ScheduledTaskStatus.Pending));
            Assert.That(updated.RetryCount, Is.EqualTo(0));
            Assert.That(updated.LastError, Is.Null);
            Assert.That(updated.LastRunAt, Is.Not.Null);
            Assert.That(updated.NextRunAt, Is.GreaterThan(due));
        });
    }

    [Test]
    public async Task ScheduledJobWorker_SkipMissedFire_DoesNotLaunchTask()
    {
        await using var host = ScheduledJobHost.Create(new Dictionary<string, string?>
        {
            ["Scheduler:MissedFireThresholdMinutes"] = "1",
        });
        var missedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await host.Store.CreateAsync(new ScheduledJobDB
        {
            Id = Guid.NewGuid(),
            Name = "missed-job",
            TaskDefinitionId = Guid.NewGuid(),
            NextRunAt = missedAt,
            RepeatInterval = TimeSpan.FromMinutes(5),
            MissedFirePolicy = MissedFirePolicy.Skip,
        });

        await host.Worker.ProcessDueJobsAsync(CancellationToken.None);

        var updated = await host.ReadScheduledJobAsync("missed-job");
        Assert.Multiple(() =>
        {
            Assert.That(host.Launcher.Launches, Is.Empty);
            Assert.That(updated.Status, Is.EqualTo(ScheduledTaskStatus.Pending));
            Assert.That(updated.LastRunAt, Is.Null);
            Assert.That(updated.NextRunAt, Is.GreaterThan(missedAt));
        });
    }

    [Test]
    public async Task ScheduledJobWorker_FailedLaunch_RequeuesUntilMaxRetriesThenFails()
    {
        await using var host = ScheduledJobHost.Create();
        host.Launcher.ThrowOnLaunch = true;
        var jobId = Guid.NewGuid();
        await host.Store.CreateAsync(new ScheduledJobDB
        {
            Id = jobId,
            Name = "retry-job",
            TaskDefinitionId = Guid.NewGuid(),
            NextRunAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            MaxRetries = 2,
        });

        await host.Worker.ProcessDueJobsAsync(CancellationToken.None);
        var firstAttempt = await host.ReadScheduledJobAsync("retry-job");
        Assert.Multiple(() =>
        {
            Assert.That(firstAttempt.Status, Is.EqualTo(ScheduledTaskStatus.Pending));
            Assert.That(firstAttempt.RetryCount, Is.EqualTo(1));
            Assert.That(firstAttempt.LastError, Does.Contain("forced launch failure"));
        });

        await host.Store.UpdateAsync(
            jobId,
            job => job.NextRunAt = DateTimeOffset.UtcNow.AddSeconds(-1));
        await host.Worker.ProcessDueJobsAsync(CancellationToken.None);

        var secondAttempt = await host.ReadScheduledJobAsync("retry-job");
        Assert.Multiple(() =>
        {
            Assert.That(secondAttempt.Status, Is.EqualTo(ScheduledTaskStatus.Failed));
            Assert.That(secondAttempt.RetryCount, Is.EqualTo(2));
            Assert.That(host.Launcher.Launches, Has.Count.EqualTo(2));
        });
    }

    private sealed class ScheduledJobHost : IAsyncDisposable
    {
        private readonly ServiceProvider _root;
        private readonly AsyncServiceScope _scope;

        private ScheduledJobHost(ServiceProvider root, AsyncServiceScope scope)
        {
            _root = root;
            _scope = scope;
        }

        public ScheduledJobStore Store => _scope.ServiceProvider.GetRequiredService<ScheduledJobStore>();
        public ScheduledJobWorker Worker => _root.GetRequiredService<ScheduledJobWorker>();
        public RecordingTaskInstanceLauncher Launcher => _root.GetRequiredService<RecordingTaskInstanceLauncher>();

        public static ScheduledJobHost Create(IReadOnlyDictionary<string, string?>? settings = null)
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
                .Build();

            services.AddSingleton<IConfiguration>(configuration);
            services.AddLogging();
            services.AddSingleton<IModuleStorageGateway>(
                new InMemoryModuleStorageGateway(new AgentOrchestrationModule().GetStorageContracts()));
            services.AddScoped<ScheduledJobStore>();
            services.AddSingleton<RecordingTaskInstanceLauncher>();
            services.AddSingleton<ITaskInstanceLauncher>(
                sp => sp.GetRequiredService<RecordingTaskInstanceLauncher>());
            services.AddSingleton<ScheduledJobWorker>();

            var root = services.BuildServiceProvider();
            return new ScheduledJobHost(root, root.CreateAsyncScope());
        }

        public async Task<ScheduledJobDB> ReadScheduledJobAsync(string name)
        {
            var jobs = await Store.ListAsync();
            return jobs.Single(job => job.Name == name);
        }

        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _root.DisposeAsync();
        }
    }

    private sealed class RecordingTaskInstanceLauncher : ITaskInstanceLauncher
    {
        public List<RecordedLaunch> Launches { get; } = [];
        public bool ThrowOnLaunch { get; set; }

        public Task<Guid> LaunchAsync(
            Guid taskDefinitionId,
            IReadOnlyDictionary<string, string>? parameterValues,
            Guid? callerAgentId,
            Guid? channelId,
            Guid? contextId,
            CancellationToken ct)
        {
            Launches.Add(new RecordedLaunch(
                taskDefinitionId,
                parameterValues is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string>(parameterValues, StringComparer.Ordinal),
                callerAgentId,
                channelId,
                contextId));

            if (ThrowOnLaunch)
                throw new InvalidOperationException("forced launch failure");

            return Task.FromResult(Guid.NewGuid());
        }
    }

    private sealed record RecordedLaunch(
        Guid TaskDefinitionId,
        IReadOnlyDictionary<string, string> ParameterValues,
        Guid? CallerAgentId,
        Guid? ChannelId,
        Guid? ContextId);

    private sealed class InMemoryModuleStorageGateway(
        IReadOnlyList<ModuleStorageContractDescriptor> contracts) : IModuleStorageGateway
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly Dictionary<string, StoredRecord> _records = new(StringComparer.Ordinal);

        public IReadOnlyList<ModuleStorageContractDescriptor> ListContracts() => contracts;

        public Task<JsonElement> InvokeAsync(
            string moduleId,
            string storageName,
            string operation,
            JsonElement payload,
            CancellationToken ct)
        {
            var result = operation switch
            {
                ModuleStorageOperations.Get => Get(payload),
                ModuleStorageOperations.Upsert => Upsert(payload),
                ModuleStorageOperations.Delete => Delete(payload),
                ModuleStorageOperations.List => RecordsResponse(_records.Values.OrderBy(record => record.Key)),
                ModuleStorageOperations.Query => RecordsResponse(ApplyQuery(payload, patch: null, indexPatch: null)),
                ModuleStorageOperations.Claim => Claim(payload),
                _ => throw new NotSupportedException(operation),
            };

            return Task.FromResult(result);
        }

        private JsonElement Get(JsonElement payload)
        {
            var key = payload.GetProperty("key").GetString()!;
            if (!_records.TryGetValue(key, out var record))
                return JsonSerializer.SerializeToElement(new { found = false }, JsonOptions);

            return JsonSerializer.SerializeToElement(new
            {
                found = true,
                key,
                value = record.Value,
            }, JsonOptions);
        }

        private JsonElement Upsert(JsonElement payload)
        {
            var key = payload.GetProperty("key").GetString()!;
            var indexes = ReadIndexes(payload.TryGetProperty("indexes", out var indexElement)
                ? indexElement
                : default);

            _records[key] = new StoredRecord(
                key,
                payload.GetProperty("value").Clone(),
                indexes);

            return JsonSerializer.SerializeToElement(new { saved = true }, JsonOptions);
        }

        private JsonElement Delete(JsonElement payload)
        {
            var key = payload.GetProperty("key").GetString()!;
            var deleted = _records.Remove(key);
            return JsonSerializer.SerializeToElement(new { deleted }, JsonOptions);
        }

        private JsonElement Claim(JsonElement payload)
        {
            var patch = payload.GetProperty("patch");
            var indexPatch = payload.TryGetProperty("indexes", out var indexes)
                ? ReadIndexes(indexes)
                : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

            return RecordsResponse(ApplyQuery(payload, patch, indexPatch));
        }

        private IReadOnlyList<StoredRecord> ApplyQuery(
            JsonElement payload,
            JsonElement? patch,
            IReadOnlyDictionary<string, JsonElement>? indexPatch)
        {
            IEnumerable<StoredRecord> records = _records.Values;
            if (payload.TryGetProperty("filters", out var filters) && filters.ValueKind == JsonValueKind.Array)
            {
                foreach (var filter in filters.EnumerateArray())
                {
                    var indexName = filter.GetProperty("indexName").GetString()!;
                    var comparison = filter.GetProperty("operator").GetString()!;
                    var expected = filter.GetProperty("value");
                    records = records.Where(record => Matches(record, indexName, comparison, expected));
                }
            }

            var ordered = records.ToList();
            if (payload.TryGetProperty("orderBy", out var orderBy) && orderBy.ValueKind == JsonValueKind.Object)
            {
                var indexName = orderBy.GetProperty("indexName").GetString()!;
                ordered = ordered
                    .OrderBy(record => SortValue(record.Indexes[indexName]))
                    .ThenBy(record => record.Key, StringComparer.Ordinal)
                    .ToList();
            }
            else
            {
                ordered = ordered.OrderBy(record => record.Key, StringComparer.Ordinal).ToList();
            }

            if (payload.TryGetProperty("limit", out var limitElement) && limitElement.TryGetInt32(out var limit))
                ordered = ordered.Take(limit).ToList();

            if (patch is not null)
            {
                foreach (var record in ordered)
                    ApplyPatch(record, patch.Value, indexPatch ?? new Dictionary<string, JsonElement>());
            }

            return ordered;
        }

        private static bool Matches(
            StoredRecord record,
            string indexName,
            string comparison,
            JsonElement expected)
        {
            if (!record.Indexes.TryGetValue(indexName, out var actual))
                return false;

            return comparison switch
            {
                ModuleStorageComparisonOperators.EqualTo =>
                    string.Equals(IndexString(actual), IndexString(expected), StringComparison.Ordinal),
                ModuleStorageComparisonOperators.LessThanOrEqual =>
                    DateTimeOffset.Parse(IndexString(actual)!) <= DateTimeOffset.Parse(IndexString(expected)!),
                ModuleStorageComparisonOperators.GreaterThanOrEqual =>
                    DateTimeOffset.Parse(IndexString(actual)!) >= DateTimeOffset.Parse(IndexString(expected)!),
                _ => throw new NotSupportedException(comparison),
            };
        }

        private static object SortValue(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(value.GetString(), out var date))
            {
                return date;
            }

            return IndexString(value) ?? "";
        }

        private static string? IndexString(JsonElement value) =>
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.GetRawText();

        private static void ApplyPatch(
            StoredRecord record,
            JsonElement patch,
            IReadOnlyDictionary<string, JsonElement> indexPatch)
        {
            var node = JsonNode.Parse(record.Value.GetRawText())!.AsObject();
            foreach (var property in patch.EnumerateObject())
                node[property.Name] = JsonNode.Parse(property.Value.GetRawText());

            record.Value = JsonDocument.Parse(node.ToJsonString(JsonOptions)).RootElement.Clone();
            foreach (var (name, value) in indexPatch)
                record.Indexes[name] = value.Clone();
        }

        private static Dictionary<string, JsonElement> ReadIndexes(JsonElement indexes)
        {
            var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (indexes.ValueKind != JsonValueKind.Object)
                return result;

            foreach (var property in indexes.EnumerateObject())
                result[property.Name] = property.Value.Clone();

            return result;
        }

        private static JsonElement RecordsResponse(IEnumerable<StoredRecord> records)
        {
            var items = records.Select(record => new
            {
                key = record.Key,
                value = record.Value,
            });

            return JsonSerializer.SerializeToElement(new { records = items }, JsonOptions);
        }

        private sealed class StoredRecord(
            string key,
            JsonElement value,
            Dictionary<string, JsonElement> indexes)
        {
            public string Key { get; } = key;
            public JsonElement Value { get; set; } = value;
            public Dictionary<string, JsonElement> Indexes { get; } = indexes;
        }
    }
}
