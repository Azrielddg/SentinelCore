using SentinelCore.Demon1.Learning;
using System.Text.Json.Serialization;

namespace SentinelCore.Demon1.Validation;

public enum OperatorAssessment { OWNER_CONSISTENT, BEHAVIOR_DIFFERENT, INCONCLUSIVE }

public sealed record FeatureDeviation(string Feature, double ExpectedMedian, double CurrentValue, double RobustDistance, double DeviationIndex);

public sealed record BehaviorAssessment(
    DateTimeOffset ObservedAtUtc,
    OperatorAssessment OperatorAssessment,
    double? DeviationPercent,
    double Confidence,
    LearningState CalibrationState,
    bool IsEmpiricallyValidated,
    bool ShouldNotifyDemon3,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<FeatureDeviation> FeatureResults);

public sealed record BehaviorDeviationSignals(
    [property: JsonPropertyName("keyboardDeviationPercent")] double? KeyboardDeviationPercent,
    [property: JsonPropertyName("mouseDeviationPercent")] double? MouseDeviationPercent,
    [property: JsonPropertyName("keyboardFeatureCount")] int KeyboardFeatureCount,
    [property: JsonPropertyName("mouseFeatureCount")] int MouseFeatureCount,
    [property: JsonPropertyName("visualMatch")] string VisualMatch = "NOT_IMPLEMENTED",
    [property: JsonPropertyName("liveness")] string Liveness = "NOT_IMPLEMENTED",
    [property: JsonPropertyName("inputOrigin")] string InputOrigin = "NOT_PROVIDED",
    [property: JsonPropertyName("sessionType")] string SessionType = "NOT_PROVIDED",
    [property: JsonPropertyName("rawKeystrokesStored")] bool RawKeystrokesStored = false,
    [property: JsonPropertyName("applicationContext")] ApplicationContextSignal? ApplicationContext = null);

public sealed record ApplicationContextSignal(
    [property: JsonPropertyName("profileId")] string ProfileId,
    [property: JsonPropertyName("observedAtUtc")] DateTimeOffset ObservedAtUtc,
    [property: JsonPropertyName("ageSeconds")] int AgeSeconds,
    [property: JsonPropertyName("foregroundApplication")] string ForegroundApplication,
    [property: JsonPropertyName("foregroundSeconds")] int ForegroundSeconds,
    [property: JsonPropertyName("localHour")] int LocalHour,
    [property: JsonPropertyName("localDayOfWeek")] int LocalDayOfWeek,
    [property: JsonPropertyName("seenInLearnedHistory")] bool? SeenInLearnedHistory,
    [property: JsonPropertyName("hourCommonForApplication")] bool? HourCommonForApplication,
    [property: JsonPropertyName("cpuPercent")] double? CpuPercent,
    [property: JsonPropertyName("memoryMiB")] double? MemoryMiB,
    [property: JsonPropertyName("systemMemoryUsedPercent")] double? SystemMemoryUsedPercent,
    [property: JsonPropertyName("sampleCount")] int SampleCount,
    [property: JsonPropertyName("reasonCode")] string ReasonCode);

/// <summary>Payload only; the Demon 3 host adds the common message envelope/IDs and transports it.</summary>
public sealed record BehaviorDeviationPayload(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("observedAtUtc")] DateTimeOffset ObservedAtUtc,
    [property: JsonPropertyName("sid")] string Sid,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("operatorAssessment")] string OperatorAssessment,
    [property: JsonPropertyName("deviationPercent")] double? DeviationPercent,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("calibrationState")] string CalibrationState,
    [property: JsonPropertyName("signals")] BehaviorDeviationSignals Signals,
    [property: JsonPropertyName("reasonCodes")] IReadOnlyList<string> ReasonCodes,
    [property: JsonPropertyName("evidenceRefs")] IReadOnlyList<string> EvidenceRefs,
    [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset ExpiresAtUtc);

public sealed record UserContextPayload(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("observedAtUtc")] DateTimeOffset ObservedAtUtc,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("applicationContext")] ApplicationContextSignal ApplicationContext,
    [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Compares one aggregated observation with an already learned owner profile.
/// The score is a divergence index, never a probability or a named-user identity claim.
/// </summary>
public sealed class BehaviorProfileValidator
{
    public const double AlertThreshold = 60.0;
    public const double MinimumQuality = 0.80;
    public const int MinimumFeatureSamples = 20;
    private const int ExpectedKeyboardFeatures = 4;
    private const int ExpectedMouseFeatures = 4;
    private const int SamplesForFullMaturity = 60;

    public BehaviorDeviationPayload? ToDemon3Payload(BehaviorAssessment assessment, string sid, string sessionId,
        IReadOnlyList<ContextActivityObservation>? recentContext = null)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        if (!assessment.ShouldNotifyDemon3) return null;
        var keyboard = assessment.FeatureResults.Where(x => x.Feature.StartsWith("typing.", StringComparison.Ordinal)).ToArray();
        var mouse = assessment.FeatureResults.Where(x => x.Feature.StartsWith("mouse.", StringComparison.Ordinal)).ToArray();
        double? keyboardScore = keyboard.Length == 0 ? null : keyboard.Average(x => x.DeviationIndex);
        double? mouseScore = mouse.Length == 0 ? null : mouse.Average(x => x.DeviationIndex);
        var contextSignal = BuildApplicationContextSignal(recentContext);
        return new BehaviorDeviationPayload(1, assessment.ObservedAtUtc, sid, sessionId,
            assessment.OperatorAssessment.ToString(), assessment.DeviationPercent, assessment.Confidence,
            assessment.IsEmpiricallyValidated ? "VALIDATED" : "UNVALIDATED",
            new BehaviorDeviationSignals(keyboardScore, mouseScore, keyboard.Length, mouse.Length, ApplicationContext: contextSignal),
            assessment.ReasonCodes, [], assessment.ObservedAtUtc.AddSeconds(30));
    }

    /// <summary>Summarizes foreground activity against recent learned samples. This is context, not a safety verdict.</summary>
    public static ApplicationContextSignal? BuildApplicationContextSignal(IReadOnlyList<ContextActivityObservation>? observations)
    {
        if (observations is null || observations.Count == 0) return null;
        var now = DateTimeOffset.UtcNow;
        var ordered = observations.Where(x => x.ObservedAtUtc <= now).OrderByDescending(x => x.ObservedAtUtc).ToArray();
        if (ordered.Length == 0) return null;
        // The newest row is the observation being described. Never use it to establish its own familiarity.
        var latest = ordered[0];
        if (now - latest.ObservedAtUtc > TimeSpan.FromMinutes(5)) return null;
        var history = ordered.Skip(1).Where(x => latest.ObservedAtUtc - x.ObservedAtUtc <= TimeSpan.FromDays(90)).ToArray();
        var appHistory = history.Where(x => StringComparer.OrdinalIgnoreCase.Equals(x.ApplicationName, latest.ApplicationName)).ToArray();
        var matureProfile = history.Length >= 30;
        var seenInHistory = appHistory.Length > 0 ? true : matureProfile ? false : (bool?)null;
        var hourCount = appHistory.Count(x => Math.Abs(x.LocalHour - latest.LocalHour) <= 1
            || Math.Abs(x.LocalHour - latest.LocalHour) >= 23);
        var commonHour = appHistory.Length >= 3 ? hourCount >= Math.Ceiling(appHistory.Length * 0.25) : (bool?)null;
        var reason = !matureProfile ? "APPLICATION_HISTORY_INSUFFICIENT"
            : seenInHistory == false ? "APPLICATION_NOT_SEEN_IN_PROFILE"
            : appHistory.Length < 3 ? "APPLICATION_HOUR_HISTORY_INSUFFICIENT"
            : commonHour == true ? "APPLICATION_AND_HOUR_SEEN_IN_PROFILE" : "APPLICATION_SEEN_AT_UNUSUAL_HOUR";
        return new(latest.ProfileId, latest.ObservedAtUtc, Math.Max(0, (int)(now - latest.ObservedAtUtc).TotalSeconds), latest.ApplicationName,
            latest.ForegroundSeconds, latest.LocalHour, latest.LocalDayOfWeek, seenInHistory, commonHour,
            latest.ForegroundCpuPercent, latest.ForegroundMemoryMiB, latest.SystemMemoryUsedPercent, history.Length, reason);
    }

    public static UserContextPayload? ToUserContextPayload(IReadOnlyList<ContextActivityObservation>? recentContext, string sessionId)
    {
        var signal = BuildApplicationContextSignal(recentContext);
        if (signal is null || string.IsNullOrWhiteSpace(sessionId)) return null;
        var now = DateTimeOffset.UtcNow;
        return new(1, signal.ObservedAtUtc, sessionId, signal, now.AddSeconds(30));
    }

    public BehaviorAssessment Assess(
        LearningReport baseline,
        IReadOnlyDictionary<string, double> observation,
        double quality,
        DateTimeOffset? observedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(observation);
        var at = observedAtUtc ?? DateTimeOffset.UtcNow;
        if (baseline.State != LearningState.READY)
            return Inconclusive(at, baseline.State, "PROFILE_NOT_READY");
        if (!double.IsFinite(quality) || quality < MinimumQuality)
            return Inconclusive(at, baseline.State, "SENSOR_QUALITY_LOW", notify: true);

        var rows = new List<FeatureDeviation>();
        foreach (var (key, value) in observation)
        {
            if (!double.IsFinite(value) || !baseline.Features.TryGetValue(key, out var stats) || stats.Count < MinimumFeatureSamples) continue;
            var scaleFloor = key.EndsWith("_ratio", StringComparison.Ordinal) ? 0.01 : 1.0;
            var scale = Math.Max(Math.Max(1.4826 * Math.Max(0, stats.Mad), Math.Max(0, stats.Iqr) / 1.349), Math.Max(Math.Abs(stats.Median) * 0.01, scaleFloor));
            var distance = Math.Abs(value - stats.Median) / scale;
            var index = Math.Clamp((distance - 1.0) / 3.0 * 100.0, 0.0, 100.0);
            rows.Add(new(key, stats.Median, value, distance, index));
        }

        var keyboard = rows.Where(x => x.Feature.StartsWith("typing.", StringComparison.Ordinal)).ToArray();
        var mouse = rows.Where(x => x.Feature.StartsWith("mouse.", StringComparison.Ordinal)).ToArray();
        if (keyboard.Length < 2 || mouse.Length < 2)
            return Inconclusive(at, baseline.State, "INSUFFICIENT_MODALITY_COVERAGE", rows);

        // Each input device contributes half the score so a modality with more fields
        // cannot dominate the other one.
        var keyboardScore = keyboard.Average(x => x.DeviationIndex);
        var mouseScore = mouse.Average(x => x.DeviationIndex);
        var deviation = (keyboardScore + mouseScore) / 2.0;
        var featureCoverage = Math.Min(1.0, keyboard.Length / (double)ExpectedKeyboardFeatures)
            * 0.5 + Math.Min(1.0, mouse.Length / (double)ExpectedMouseFeatures) * 0.5;
        var maturity = Math.Min(1.0, rows.Min(x => baseline.Features[x.Feature].Count) / (double)SamplesForFullMaturity);
        var confidence = Math.Clamp(quality * featureCoverage * maturity, 0.0, 1.0);
        var reasons = new List<string>();
        if (keyboardScore > 0) reasons.Add("KEYBOARD_DYNAMICS_OUTSIDE_BASELINE");
        if (mouseScore > 0) reasons.Add("MOUSE_DYNAMICS_OUTSIDE_BASELINE");
        if (confidence < 0.65) reasons.Add("PROFILE_OR_SENSOR_CONFIDENCE_LOW");

        var assessment = confidence < 0.65 ? OperatorAssessment.INCONCLUSIVE
            : deviation > AlertThreshold ? OperatorAssessment.BEHAVIOR_DIFFERENT
            : OperatorAssessment.OWNER_CONSISTENT;
        if (assessment == OperatorAssessment.BEHAVIOR_DIFFERENT) reasons.Insert(0, "BEHAVIOR_DIVERGENCE_OVER_60");
        if (reasons.Count == 0) reasons.Add("BEHAVIOR_WITHIN_OWNER_BASELINE");
        return new(at, assessment, deviation, confidence, baseline.State, false,
            deviation > AlertThreshold || (confidence < 0.65 && assessment == OperatorAssessment.INCONCLUSIVE), reasons, rows);
    }

    private static BehaviorAssessment Inconclusive(DateTimeOffset at, LearningState state, string reason, IReadOnlyList<FeatureDeviation>? rows = null, bool notify = false)
        => new(at, OperatorAssessment.INCONCLUSIVE, null, 0, state, false, notify, [reason], rows ?? []);
}
