using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.Agents;

/// <summary>Module-owned Agent job definition and domain state.</summary>
public sealed record AgentJob(
    Guid Id,
    Guid AgentId,
    string CallerIdentity,
    string ActionIdentity,
    string Resource,
    string ScriptJson,
    string PayloadJson,
    string WorkingDirectory,
    string Status,
    string Clearance,
    long InputTokens,
    long OutputTokens,
    IReadOnlyList<string> ApprovalIdentities,
    Guid? ChannelId,
    Guid? ContextId,
    string PermissionIdentity,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    Guid? CanonicalJobId,
    string? ResultJson,
    string? Error,
    string ResultAuthority = "sharpclaw.jobs")
{
    public const string CanonicalResultAuthority = "sharpclaw.jobs";

    public string HandlerKey { get; init; } = AgentJobHandlerKeys.Canonical;
    public string PayloadCodec { get; init; } = AgentJobPayloadCodecs.JsonV1;
    public string RecoveryMode { get; init; } = AgentJobRecoveryModes.CanonicalHandler;
}

public static class AgentJobHandlerKeys
{
    public const string Canonical = "sharpclaw.agents.job.canonical.v1";
}

public static class AgentJobPayloadCodecs
{
    public const string JsonV1 = "json.v1";
}

public static class AgentJobRecoveryModes
{
    public const string CanonicalHandler = "canonical_handler";
    public const string CanonicalRecovery = "canonical_recovery";
    public const string Terminal = "terminal";
}

public static class AgentJobImportStates
{
    public const string Queued = "queued";
    public const string Paused = "paused";
    public const string Active = "active";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>One neutral source record supplied to the Agents import boundary.</summary>
public sealed record NeutralAgentJobRecord(
    Guid SourceId,
    Guid AgentId,
    string CallerIdentity,
    string ActionIdentity,
    string Resource,
    string ScriptJson,
    string PayloadJson,
    string WorkingDirectory,
    string Status,
    string Clearance,
    long InputTokens,
    long OutputTokens,
    IReadOnlyList<string> ApprovalIdentities,
    Guid? ChannelId,
    Guid? ContextId,
    string PermissionIdentity,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    Guid? CanonicalJobId,
    string? ResultJson,
    string? Error,
    string ResultAuthority);

public sealed record AgentJobActionMapping(
    string ActionIdentity,
    string HandlerKey,
    string PayloadCodec);

/// <summary>Neutral import envelope for one canonical Jobs snapshot.</summary>
public sealed record CanonicalJobsImportSnapshot
{
    public CanonicalJobsImportSnapshot(
        string snapshotId,
        DateTimeOffset capturedAt,
        IReadOnlyList<NeutralAgentJobRecord> records,
        IReadOnlyList<AgentJobActionMapping> actionMappings)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(actionMappings);

        SnapshotId = snapshotId;
        CapturedAt = capturedAt;
        Records = records.ToArray();
        ActionMappings = actionMappings.ToArray();
        ExpectedRecordCount = Records.Count;
        OrderedSourceIds = Records
            .Select(record => record?.SourceId ?? Guid.Empty)
            .ToArray();
        SourceHashes = Records
            .Select(record => record is null
                ? string.Empty
                : AgentsJobImportIntegrity.ComputeSourceHash(record))
            .ToArray();
        AggregateHash = AgentsJobImportIntegrity.ComputeAggregateHash(
            OrderedSourceIds,
            SourceHashes);
        MappingHash = AgentsJobImportIntegrity.ComputeMappingHash(ActionMappings);
    }

    public CanonicalJobsImportSnapshot(
        string snapshotId,
        DateTimeOffset capturedAt,
        int expectedRecordCount,
        IReadOnlyList<Guid> orderedSourceIds,
        IReadOnlyList<string> sourceHashes,
        string aggregateHash,
        string mappingHash,
        IReadOnlyList<NeutralAgentJobRecord> records,
        IReadOnlyList<AgentJobActionMapping> actionMappings)
    {
        SnapshotId = snapshotId;
        CapturedAt = capturedAt;
        ExpectedRecordCount = expectedRecordCount;
        OrderedSourceIds = orderedSourceIds ?? throw new ArgumentNullException(nameof(orderedSourceIds));
        SourceHashes = sourceHashes ?? throw new ArgumentNullException(nameof(sourceHashes));
        AggregateHash = aggregateHash;
        MappingHash = mappingHash;
        Records = records ?? throw new ArgumentNullException(nameof(records));
        ActionMappings = actionMappings ?? throw new ArgumentNullException(nameof(actionMappings));
    }

    public string SnapshotId { get; init; } = string.Empty;
    public DateTimeOffset CapturedAt { get; init; }
    public int ExpectedRecordCount { get; init; }
    public IReadOnlyList<Guid> OrderedSourceIds { get; init; } = [];
    public IReadOnlyList<string> SourceHashes { get; init; } = [];
    public string AggregateHash { get; init; } = string.Empty;
    public string MappingHash { get; init; } = string.Empty;
    public IReadOnlyList<NeutralAgentJobRecord> Records { get; init; } = [];
    public IReadOnlyList<AgentJobActionMapping> ActionMappings { get; init; } = [];
}

public sealed record AgentJobImportState(
    string SnapshotId,
    DateTimeOffset CapturedAt,
    int ExpectedRecordCount,
    IReadOnlyList<Guid> OrderedSourceIds,
    IReadOnlyList<string> SourceHashes,
    string AggregateHash,
    string MappingHash,
    int ImportedRecordCount,
    bool Completed);

public sealed record AgentJobImportPlan(
    CanonicalJobsImportSnapshot Snapshot,
    IReadOnlyList<AgentJob> Jobs);

public sealed class AgentJobImportException(string message) : InvalidOperationException(message);

public static class AgentsJobImportIntegrity
{
    private static readonly JsonSerializerOptions HashJsonOptions =
        new(JsonSerializerDefaults.General);

    public static string ComputeSourceHash(NeutralAgentJobRecord source) =>
        ComputeHash(JsonSerializer.Serialize(source, HashJsonOptions));

    public static string ComputeAgentJobHash(AgentJob job) =>
        ComputeHash(JsonSerializer.Serialize(job, HashJsonOptions));

    public static bool AreEquivalent(AgentJob expected, AgentJob actual) =>
        string.Equals(ComputeAgentJobHash(expected), ComputeAgentJobHash(actual), StringComparison.Ordinal);

    public static string ComputeMappingHash(IReadOnlyList<AgentJobActionMapping> orderedMappings) =>
        ComputeHash(JsonSerializer.Serialize(orderedMappings, HashJsonOptions));

    public static string ComputeAggregateHash(
        IReadOnlyList<Guid> orderedSourceIds,
        IReadOnlyList<string> sourceHashes)
    {
        if (orderedSourceIds.Count != sourceHashes.Count)
            throw new ArgumentException("Source identities and hashes must have equal counts.");

        var builder = new StringBuilder();
        builder.Append(orderedSourceIds.Count).Append('\n');
        for (var index = 0; index < orderedSourceIds.Count; index++)
        {
            builder.Append(orderedSourceIds[index].ToString("D"))
                .Append(':')
                .Append(sourceHashes[index])
                .Append('\n');
        }

        return ComputeHash(builder.ToString());
    }

    public static string ImportKey(string snapshotId) =>
        $"snapshot:{ComputeHash(snapshotId)}";

    public static bool ManifestMatches(
        CanonicalJobsImportSnapshot snapshot,
        AgentJobImportState state) =>
        string.Equals(snapshot.SnapshotId, state.SnapshotId, StringComparison.Ordinal)
        && snapshot.CapturedAt == state.CapturedAt
        && snapshot.ExpectedRecordCount == state.ExpectedRecordCount
        && state.OrderedSourceIds is not null
        && state.SourceHashes is not null
        && snapshot.OrderedSourceIds.SequenceEqual(state.OrderedSourceIds)
        && snapshot.SourceHashes.SequenceEqual(state.SourceHashes, StringComparer.Ordinal)
        && string.Equals(snapshot.AggregateHash, state.AggregateHash, StringComparison.Ordinal)
        && string.Equals(snapshot.MappingHash, state.MappingHash, StringComparison.Ordinal);

    private static string ComputeHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public static class AgentsJobImportConverter
{
    public static AgentJob Convert(
        JobActionInput<NeutralAgentJobRecord> input,
        AgentJobActionMapping mapping)
    {
        input.Deconstruct(out var source);
        return Convert(new CanonicalJobsImportSnapshot(
            "single-record",
            source.CreatedAt,
            [source],
            [mapping])).Single();
    }

    public static IReadOnlyList<AgentJob> Convert(CanonicalJobsImportSnapshot snapshot) =>
        Prepare(snapshot).Jobs;

    public static AgentJobImportPlan Prepare(CanonicalJobsImportSnapshot snapshot)
    {
        if (snapshot is null)
            throw new AgentJobImportException("The Jobs import snapshot is required.");
        if (string.IsNullOrWhiteSpace(snapshot.SnapshotId))
            throw new AgentJobImportException("The Jobs import snapshot requires an id.");
        if (snapshot.CapturedAt == default)
            throw new AgentJobImportException("The Jobs import snapshot requires a capture time.");
        if (snapshot.ExpectedRecordCount < 0
            || snapshot.Records is null
            || snapshot.ActionMappings is null
            || snapshot.OrderedSourceIds is null
            || snapshot.SourceHashes is null)
            throw new AgentJobImportException("The Jobs import snapshot is incomplete.");
        if (snapshot.ExpectedRecordCount != snapshot.Records.Count)
            throw new AgentJobImportException("The Jobs import snapshot record count is not authoritative.");
        if (snapshot.OrderedSourceIds.Count != snapshot.ExpectedRecordCount
            || snapshot.SourceHashes.Count != snapshot.ExpectedRecordCount)
            throw new AgentJobImportException("The Jobs import snapshot identity lists are incomplete.");
        if (string.IsNullOrWhiteSpace(snapshot.AggregateHash)
            || string.IsNullOrWhiteSpace(snapshot.MappingHash))
            throw new AgentJobImportException(
                "The Jobs import snapshot requires record and action mapping hashes.");

        var mappings = BuildMappings(snapshot.ActionMappings);
        var mappingHash = AgentsJobImportIntegrity.ComputeMappingHash(snapshot.ActionMappings);
        if (!string.Equals(snapshot.MappingHash, mappingHash, StringComparison.Ordinal))
            throw new AgentJobImportException(
                "The Jobs import action mapping hash does not match its ordered mappings.");
        var jobs = new List<AgentJob>(snapshot.Records.Count);
        var sourceIds = new HashSet<Guid>();
        for (var index = 0; index < snapshot.Records.Count; index++)
        {
            var source = snapshot.Records[index];
            if (source is null)
                throw new AgentJobImportException("The Jobs import snapshot contains a null source record.");
            if (!sourceIds.Add(source.SourceId))
                throw new AgentJobImportException($"The source id '{source.SourceId}' occurs more than once.");
            if (snapshot.OrderedSourceIds[index] != source.SourceId)
                throw new AgentJobImportException("The Jobs import source order does not match its identity list.");
            var sourceHash = AgentsJobImportIntegrity.ComputeSourceHash(source);
            if (!string.Equals(snapshot.SourceHashes[index], sourceHash, StringComparison.Ordinal))
                throw new AgentJobImportException(
                    $"The source hash for '{source.SourceId}' does not match its record.");
            jobs.Add(Convert(source, mappings));
        }

        var aggregateHash = AgentsJobImportIntegrity.ComputeAggregateHash(
            snapshot.OrderedSourceIds,
            snapshot.SourceHashes);
        if (!string.Equals(snapshot.AggregateHash, aggregateHash, StringComparison.Ordinal))
            throw new AgentJobImportException("The Jobs import aggregate hash does not match its ordered records.");

        return new AgentJobImportPlan(snapshot, jobs);
    }

    private static AgentJob Convert(
        NeutralAgentJobRecord source,
        IReadOnlyDictionary<string, AgentJobActionMapping> mappings)
    {
        ValidateSource(source);
        if (!mappings.TryGetValue(source.ActionIdentity, out var mapping))
            throw new AgentJobImportException(
                $"The action '{source.ActionIdentity}' has no exact handler mapping.");
        if (!string.Equals(mapping.HandlerKey, AgentJobHandlerKeys.Canonical, StringComparison.Ordinal))
            throw new AgentJobImportException(
                $"The action '{source.ActionIdentity}' maps to an unsupported handler.");
        if (!string.Equals(mapping.PayloadCodec, AgentJobPayloadCodecs.JsonV1, StringComparison.Ordinal))
            throw new AgentJobImportException(
                $"The action '{source.ActionIdentity}' maps to an unsupported payload codec.");

        var payload = CanonicalizeJson(source.PayloadJson, source.ActionIdentity);
        var status = source.Status.Trim().ToLowerInvariant();
        var recoveryMode = status switch
        {
            AgentJobImportStates.Queued or AgentJobImportStates.Paused
                => RequireHandlerState(source),
            AgentJobImportStates.Active or AgentJobImportStates.Running
                => RequireRecoveryState(source),
            AgentJobImportStates.Completed or AgentJobImportStates.Failed or AgentJobImportStates.Cancelled
                => RequireTerminalState(source),
            _ => throw new AgentJobImportException($"The status '{source.Status}' is not supported."),
        };

        return new AgentJob(
            source.SourceId,
            source.AgentId,
            source.CallerIdentity,
            source.ActionIdentity,
            source.Resource,
            source.ScriptJson,
            payload,
            source.WorkingDirectory,
            status,
            source.Clearance,
            source.InputTokens,
            source.OutputTokens,
            source.ApprovalIdentities,
            source.ChannelId,
            source.ContextId,
            source.PermissionIdentity,
            source.CreatedAt,
            source.UpdatedAt,
            source.StartedAt,
            source.CompletedAt,
            source.CanonicalJobId,
            source.ResultJson,
            source.Error,
            AgentJob.CanonicalResultAuthority)
        {
            HandlerKey = mapping.HandlerKey,
            PayloadCodec = mapping.PayloadCodec,
            RecoveryMode = recoveryMode,
        };
    }

    private static IReadOnlyDictionary<string, AgentJobActionMapping> BuildMappings(
        IReadOnlyList<AgentJobActionMapping> mappings)
    {
        var result = new Dictionary<string, AgentJobActionMapping>(StringComparer.Ordinal);
        foreach (var mapping in mappings)
        {
            if (mapping is null
                || string.IsNullOrWhiteSpace(mapping.ActionIdentity)
                || !result.TryAdd(mapping.ActionIdentity, mapping))
                throw new AgentJobImportException("The Jobs import action mappings are incomplete or duplicated.");
        }
        return result;
    }

    private static void ValidateSource(NeutralAgentJobRecord source)
    {
        if (source.SourceId == Guid.Empty || source.AgentId == Guid.Empty)
            throw new AgentJobImportException("The source and Agent identities are required.");
        if (string.IsNullOrWhiteSpace(source.CallerIdentity)
            || string.IsNullOrWhiteSpace(source.ActionIdentity)
            || string.IsNullOrWhiteSpace(source.Resource)
            || string.IsNullOrWhiteSpace(source.ScriptJson)
            || string.IsNullOrWhiteSpace(source.WorkingDirectory)
            || string.IsNullOrWhiteSpace(source.Status)
            || string.IsNullOrWhiteSpace(source.Clearance)
            || string.IsNullOrWhiteSpace(source.PermissionIdentity))
            throw new AgentJobImportException("The source record is missing a required identity or value.");
        if (source.ChannelId is null || source.ContextId is null)
            throw new AgentJobImportException("The source record requires Channel and Context identities.");
        if (source.ChannelId == Guid.Empty || source.ContextId == Guid.Empty)
            throw new AgentJobImportException("The source record contains an empty Channel or Context identity.");
        if (source.CreatedAt == default || source.UpdatedAt == default)
            throw new AgentJobImportException("The source record requires creation and update times.");
        if (source.InputTokens < 0 || source.OutputTokens < 0)
            throw new AgentJobImportException("The source record cannot contain negative token counts.");
        if (source.ApprovalIdentities is null || source.ApprovalIdentities.Any(string.IsNullOrWhiteSpace))
            throw new AgentJobImportException("The source record contains an invalid approval identity.");
        if (!string.IsNullOrWhiteSpace(source.ResultAuthority)
            && !string.Equals(source.ResultAuthority, AgentJob.CanonicalResultAuthority, StringComparison.Ordinal))
            throw new AgentJobImportException("The source record contains an unsupported result authority.");
    }

    private static string RequireHandlerState(NeutralAgentJobRecord source)
    {
        if (source.CanonicalJobId is not null || source.ResultJson is not null || source.Error is not null)
            throw new AgentJobImportException("Queued and paused records cannot contain canonical completion state.");
        return AgentJobRecoveryModes.CanonicalHandler;
    }

    private static string RequireRecoveryState(NeutralAgentJobRecord source)
    {
        if (source.CanonicalJobId is null || source.CanonicalJobId == Guid.Empty
            || source.StartedAt is null || source.StartedAt == default)
            throw new AgentJobImportException("Active records require canonical identity and start time.");
        if (!string.Equals(source.ResultAuthority, AgentJob.CanonicalResultAuthority, StringComparison.Ordinal))
            throw new AgentJobImportException("Active records require canonical result authority.");
        if (source.ResultJson is not null || source.Error is not null)
            throw new AgentJobImportException("Active records cannot contain terminal result state.");
        return AgentJobRecoveryModes.CanonicalRecovery;
    }

    private static string RequireTerminalState(NeutralAgentJobRecord source)
    {
        if (source.CanonicalJobId is null || source.CanonicalJobId == Guid.Empty
            || source.CompletedAt is null || source.CompletedAt == default)
            throw new AgentJobImportException("Terminal records require canonical identity and completion time.");
        if (!string.Equals(source.ResultAuthority, AgentJob.CanonicalResultAuthority, StringComparison.Ordinal))
            throw new AgentJobImportException("Terminal records require canonical result authority.");
        if (source.ResultJson is null && source.Error is null)
            throw new AgentJobImportException("Terminal records require a result or error.");
        return AgentJobRecoveryModes.Terminal;
    }

    private static string CanonicalizeJson(string payload, string actionIdentity)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind is System.Text.Json.JsonValueKind.Null
                or System.Text.Json.JsonValueKind.Undefined)
                throw new AgentJobImportException($"The payload for '{actionIdentity}' is empty.");
            return document.RootElement.GetRawText();
        }
        catch (AgentJobImportException)
        {
            throw;
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new AgentJobImportException(
                $"The payload for '{actionIdentity}' is not valid JSON: {exception.Message}");
        }
    }
}

public sealed record AgentsRecordJobAction(
    AgentJob Job,
    RequestPrincipal Caller,
    HostActionEntryRequestContext HostActionContext);

public sealed record AgentsAttachCanonicalJobAction(
    Guid AgentJobId,
    Guid CanonicalJobId,
    RequestPrincipal Caller,
    HostActionEntryRequestContext HostActionContext);

public sealed record AgentsCompleteJobAction(
    Guid AgentJobId,
    Guid CanonicalJobId,
    string Status,
    string? ResultJson,
    string? Error,
    long InputTokens,
    long OutputTokens,
    DateTimeOffset CompletedAt,
    RequestPrincipal Caller,
    HostActionEntryRequestContext HostActionContext);

public sealed record AgentsImportJobsAction(
    CanonicalJobsImportSnapshot Snapshot,
    RequestPrincipal Caller,
    HostActionEntryRequestContext HostActionContext);

public interface IAgentsJobActionExecutor
{
    Task<AgentJob> RecordAsync(
        AgentsRecordJobAction action,
        CancellationToken ct = default);

    Task<AgentJob> AttachCanonicalJobAsync(
        AgentsAttachCanonicalJobAction action,
        CancellationToken ct = default);

    Task<AgentJob> CompleteAsync(
        AgentsCompleteJobAction action,
        CancellationToken ct = default);

    Task<IReadOnlyList<AgentJob>> ImportAsync(
        AgentsImportJobsAction action,
        CancellationToken ct = default);
}

public sealed class AgentsJobActionExecutor(
    AgentsCatalog catalog) : IAgentsJobActionExecutor
{
    public Task<AgentJob> RecordAsync(
        AgentsRecordJobAction action,
        CancellationToken ct = default) =>
        catalog.RecordAgentJobAsync(action.Caller, action.Job, ct, action.HostActionContext);

    public Task<AgentJob> AttachCanonicalJobAsync(
        AgentsAttachCanonicalJobAction action,
        CancellationToken ct = default) =>
        catalog.AttachCanonicalJobAsync(action.Caller, action.AgentJobId, action.CanonicalJobId, ct, action.HostActionContext);

    public Task<AgentJob> CompleteAsync(
        AgentsCompleteJobAction action,
        CancellationToken ct = default) =>
        catalog.ProjectCanonicalCompletionAsync(
            action.Caller,
            action.AgentJobId,
            action.CanonicalJobId,
            action.Status,
            action.ResultJson,
            action.Error,
            action.InputTokens,
            action.OutputTokens,
            action.CompletedAt,
            ct,
            action.HostActionContext);

    public Task<IReadOnlyList<AgentJob>> ImportAsync(
        AgentsImportJobsAction action,
        CancellationToken ct = default) =>
        catalog.ImportAgentJobsAsync(action.Caller, action.Snapshot, ct, action.HostActionContext);
}
