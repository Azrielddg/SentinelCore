using System.Text.Json;

namespace SentinelCore.Demon2.Core;

public enum ThreatEventType
{
    PROCESS_STARTED, SCRIPT_ACTIVITY, FILE_CREATED_OR_CHANGED, DOWNLOAD_OBSERVED,
    NETWORK_CONNECTION, DOMAIN_QUERY, DOMAIN_REPUTATION, IMAGE_LOAD, PROCESS_ACCESS, PERSISTENCE_CHANGE, TELEMETRY_GAP
}

public enum InterDaemonMessageType { HELLO, HEARTBEAT, THREAT_FINDING, TELEMETRY_GAP, SESSION_CONTEXT_REQUEST, CONTEXT_RESPONSE }

public enum ThreatSeverity { low, medium, high, critical }
public enum TelemetryState { Available, Degraded, Unavailable }

public sealed record CanonicalEvent(
    string EventId,
    DateTimeOffset TimestampUtc,
    string Source,
    string SourceEventId,
    string HostId,
    ThreatEventType EventType,
    int? ProcessId = null,
    string? ProcessGuid = null,
    int? ParentProcessId = null,
    string? ParentProcessGuid = null,
    string? ImagePath = null,
    string? SignerStatus = null,
    string? Sha256 = null,
    string? TargetPath = null,
    string? RemoteHost = null,
    int? RemotePort = null,
    string? UserSid = null,
    string? SessionId = null,
    IReadOnlyDictionary<string, string>? Fields = null,
    int SchemaVersion = 1,
    string? SourceCursor = null);

public sealed record DetectionFact(string Name, string Value);

public sealed record ThreatFinding(
    string FindingId,
    string CorrelationId,
    string RuleId,
    int RuleVersion,
    ThreatSeverity Severity,
    double Confidence,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    string Summary,
    IReadOnlyList<DetectionFact> Facts,
    IReadOnlyList<string> EvidenceRefs,
    IReadOnlyList<string> MissingTelemetry,
    DateTimeOffset ExpiresAtUtc);

public sealed record TelemetryGap(
    string GapId,
    string Source,
    DateTimeOffset ObservedAtUtc,
    string Reason,
    long DroppedCount,
    IReadOnlyList<string> MissingFields);

public sealed record InterDaemonEnvelope(
    Guid MessageId,
    string CorrelationId,
    DateTimeOffset TimestampUtc,
    string SourceDaemon,
    string Destination,
    string MessageType,
    int SchemaVersion,
    DateTimeOffset ExpiresAtUtc,
    JsonElement Payload);

public sealed record DetectionResult(IReadOnlyList<ThreatFinding> Findings, IReadOnlyList<TelemetryGap> Gaps);

public sealed record SysmonRecord(string Xml, string? BookmarkXml, string? SourceError = null);
public sealed record EventSourceHealth(TelemetryState State, long DroppedCount, string? Error, DateTimeOffset? LastEventUtc, int QueueDepth = 0, DateTimeOffset? StartedAtUtc = null);

public interface IEventSource<T>
{
    string Name { get; }
    IAsyncEnumerable<T> ReadAllAsync(CancellationToken cancellationToken);
}

public interface IEventSourceHealthProvider { EventSourceHealth GetHealth(); }
public interface IEventCursorSource { void CommitCursor(string cursor); }

public interface IEventNormalizer<T>
{
    bool TryNormalize(T input, out CanonicalEvent? result, out string? rejectionReason);
}

public interface IEventStore
{
    Task<bool> SaveEventAsync(CanonicalEvent item, CancellationToken cancellationToken = default);
    Task<bool> SaveFindingAsync(ThreatFinding finding, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ThreatFinding>> LoadFindingsAsync(CancellationToken cancellationToken = default);
    Task<bool> SaveTelemetryGapAsync(TelemetryGap gap, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TelemetryGap>> LoadTelemetryGapsAsync(CancellationToken cancellationToken = default);
    Task<bool> HasFindingAsync(string deduplicationKey, CancellationToken cancellationToken = default);
    Task SaveCursorAsync(string source, string cursor, CancellationToken cancellationToken = default);
    Task<string?> LoadCursorAsync(string source, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CanonicalEvent>> LoadEventsAsync(string? processGuid, int? processId, DateTimeOffset fromUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CanonicalEvent>> LoadEventsByIdsAsync(IReadOnlyCollection<string> eventIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CanonicalEvent>> LoadRecentEventsAsync(DateTimeOffset fromUtc, int limit, CancellationToken cancellationToken = default);
    Task PruneExpiredAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    Task<bool> EnqueueMessageAsync(InterDaemonEnvelope message, CancellationToken cancellationToken = default);
    Task<int> GetPendingMessageCountAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InterDaemonEnvelope>> LoadPendingMessagesAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken = default);
    Task MarkMessageDeliveredAsync(Guid messageId, CancellationToken cancellationToken = default);
    Task ScheduleMessageRetryAsync(Guid messageId, string error, DateTimeOffset nextAttemptUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InterDaemonEnvelope>> ExpireMessagesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
}

public interface IInterDaemonBus
{
    ValueTask PublishAsync(InterDaemonEnvelope message, CancellationToken cancellationToken = default);
}
