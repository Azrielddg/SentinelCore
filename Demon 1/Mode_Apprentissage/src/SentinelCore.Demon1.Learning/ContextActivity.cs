namespace SentinelCore.Demon1.Learning;

/// <summary>Résumé d'une période d'usage, sans titre de fenêtre, frappe brute ni contenu de fichier.</summary>
public sealed record ContextActivityObservation(
    string ProfileId,
    DateTimeOffset ObservedAtUtc,
    string ApplicationName,
    int LocalHour,
    int LocalDayOfWeek,
    int ForegroundSeconds,
    double? ForegroundCpuPercent,
    double? ForegroundMemoryMiB,
    double? SystemMemoryUsedPercent);

public sealed record ApplicationHabit(
    string ApplicationName,
    int PeriodCount,
    double ForegroundMinutes,
    IReadOnlyList<int> TypicalLocalHours,
    double? MedianCpuPercent,
    double? MedianMemoryMiB);

public interface IContextActivityRepository
{
    void AppendContextActivity(string sid, ContextActivityObservation observation);
    IReadOnlyList<ContextActivityObservation> ReadRecentContextActivity(string sid, string profileId, int take = 5000);
}

public static class ContextActivityAnalysis
{
    public static IReadOnlyList<ApplicationHabit> BuildApplicationHabits(IEnumerable<ContextActivityObservation> observations)
        => observations
            .Where(x => !string.IsNullOrWhiteSpace(x.ApplicationName))
            .GroupBy(x => x.ApplicationName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ApplicationHabit(
                group.Key,
                group.Count(),
                group.Sum(x => Math.Clamp(x.ForegroundSeconds, 0, 60)) / 60d,
                group.GroupBy(x => x.LocalHour).OrderByDescending(x => x.Count()).ThenBy(x => x.Key).Take(4).Select(x => x.Key).ToArray(),
                Median(group.Where(x => x.ForegroundCpuPercent.HasValue).Select(x => x.ForegroundCpuPercent!.Value)),
                Median(group.Where(x => x.ForegroundMemoryMiB.HasValue).Select(x => x.ForegroundMemoryMiB!.Value))))
            .OrderByDescending(x => x.PeriodCount)
            .ToArray();

    private static double? Median(IEnumerable<double> source)
    {
        var values = source.Where(double.IsFinite).Order().ToArray();
        if (values.Length == 0) return null;
        return values.Length % 2 == 1 ? values[values.Length / 2] : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;
    }
}
