using System.Text.Json;
using SentinelCore.Demon1.VisualVerification;

namespace SentinelCore.Demon1.Learning.Controller;

internal sealed record VisualCheckReport(DateTimeOffset ObservedAtUtc, string Source, string CorrelationId, string Status,
    string Liveness, string? ProfileId, double? Similarity, double FaceQuality, int ValidFrames, string? EvidenceRef, string ReasonCode,
    string? DiagnosticSummary = null);

internal static class VisualCheckReportStore
{
    private static string PathFor(string root) => Path.Combine(root, "visual-reports.jsonl");

    public static void Append(string root, string source, VisualCheckResult result, string? diagnostics = null)
        => Append(root, new(result.CapturedAtUtc, source, result.CorrelationId, result.Match.ToString(), result.Liveness.ToString(), result.ProfileId,
            result.Similarity, result.FaceQuality, result.ValidFrames, result.EvidenceRef, result.ReasonCode, diagnostics));

    public static void Append(string root, string source, VisualIdentityResponse result, string? diagnostics = null)
        => Append(root, new(result.ObservedAtUtc, source, result.CorrelationId, result.Status, result.Liveness, result.ProfileId,
            result.Similarity, result.FaceQuality, result.ValidFrames, result.EvidenceRef, result.ReasonCode, diagnostics));

    public static void AppendEnrollment(string root, string profileId, bool succeeded, string reasonCode, string? diagnostics = null)
        => Append(root, new(DateTimeOffset.UtcNow, "ENROLEMENT", Guid.NewGuid().ToString("D"), succeeded ? "ENROLLED" : "FAILED",
            "NOT_CHECKED", profileId, null, 0, 0, null, reasonCode, diagnostics));

    private static void Append(string root, VisualCheckReport report)
    {
        Directory.CreateDirectory(root);
        File.AppendAllText(PathFor(root), JsonSerializer.Serialize(report) + Environment.NewLine);
    }

    public static IReadOnlyList<VisualCheckReport> ReadRecent(string root, int limit = 500)
    {
        var path = PathFor(root);
        if (!File.Exists(path)) return [];
        var reports = new List<VisualCheckReport>();
        foreach (var line in File.ReadLines(path).TakeLast(Math.Clamp(limit, 1, 5000)))
            try { if (JsonSerializer.Deserialize<VisualCheckReport>(line) is { } report) reports.Add(report); }
            catch (JsonException) { }
        return reports.OrderByDescending(x => x.ObservedAtUtc).ToArray();
    }
}
