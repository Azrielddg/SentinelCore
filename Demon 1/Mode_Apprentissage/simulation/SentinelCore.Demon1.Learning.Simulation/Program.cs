using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SentinelCore.Demon1.Learning;

var databasePath = Path.Combine(Path.GetTempPath(), $"sentinelcore-learning-sim-{Guid.NewGuid():N}.db");
using var repository = new SqliteLearningProfileRepository(databasePath);
var learner = new LearningModeService(repository);
const string sid = "S-1-5-21-SIMULATION";
const string context = "LOCAL|INTEGRATED_KB|MOUSE_USB";
var start = new DateTimeOffset(2026, 1, 5, 8, 0, 0, TimeSpan.Zero);
var accepted = 0;
var quarantined = 0;
LearningResult? last = null;

for (var i = 0; i < 70; i++)
{
    var day = i / 10;
    var minute = (i % 10) * 4;
    last = learner.Observe(new LearningWindow(
        sid,
        start.AddDays(day).AddMinutes(minute),
        context,
        SampleFeatures(i),
        0.98,
        OwnerVerification.VISUAL_MATCH,
        InputOrigin.HUMAN_LIKELY,
        ThreatContext.CLEAR));
    if (last.Disposition == SampleDisposition.ACCEPTED) accepted++;
}

var injected = learner.Observe(new LearningWindow(sid, start.AddDays(8), context, SampleFeatures(8), .99,
    OwnerVerification.VISUAL_MATCH, InputOrigin.INJECTED, ThreatContext.CLEAR));
if (injected.Disposition == SampleDisposition.QUARANTINED) quarantined++;
var unverified = learner.Observe(new LearningWindow(sid, start.AddDays(8).AddMinutes(1), context, SampleFeatures(9), .99,
    OwnerVerification.NONE, InputOrigin.HUMAN_LIKELY, ThreatContext.CLEAR));
if (unverified.Disposition == SampleDisposition.QUARANTINED) quarantined++;

const int iterations = 250;
var timer = Stopwatch.StartNew();
for (var i = 0; i < iterations; i++)
{
    learner.Observe(new LearningWindow(sid, start.AddDays(9).AddMinutes(i), context, SampleFeatures(i), .98,
        OwnerVerification.WINDOWS_HELLO, InputOrigin.HUMAN_LIKELY, ThreatContext.CLEAR));
}
timer.Stop();
var throughput = iterations / timer.Elapsed.TotalSeconds;
var report = learner.GetReport(sid, context);
var storedRows = repository.CountObservations(sid);
var visibleRows = repository.ReadRecent(sid, 5).Count;

Console.WriteLine("MODE APPRENTISSAGE — simulation synthétique");
Console.WriteLine($"État après calibration : {report.State}");
Console.WriteLine($"Fenêtres de calibration admises : {accepted}/70 ({accepted / 70d:P0})");
Console.WriteLine($"Fenêtres non admissibles écartées : {quarantined}/2 ({quarantined / 2d:P0})");
Console.WriteLine($"Contexte : clavier={report.KeyboardWindows}, souris={report.MouseWindows}, jours={report.DistinctDays}");
Console.WriteLine($"Progression : {report.CalibrationProgressPercent:F0}%");
Console.WriteLine($"Débit local : {throughput:F0} observations agrégées/s sur {iterations} observations, temps={timer.Elapsed.TotalMilliseconds:F1} ms");
Console.WriteLine($"Base SQLite : {storedRows} périodes stockées; {visibleRows} relues et déchiffrées pour l’affichage.");
Console.WriteLine("Ces pourcentages décrivent les règles d’admission de cette simulation, pas un taux terrain d’identification.");
repository.Dispose();
SqliteConnection.ClearAllPools();
foreach (var suffix in new[] { "", "-wal", "-shm" })
{
    var file = databasePath + suffix;
    if (File.Exists(file)) File.Delete(file);
}

static IReadOnlyDictionary<string, double> SampleFeatures(int i) => new Dictionary<string, double>(StringComparer.Ordinal)
{
    ["typing.rate"] = 70 + (i % 5) * 2,
    ["typing.dwell_median_ms"] = 100 + (i % 3) * 3,
    ["typing.flight_median_ms"] = 140 + (i % 4) * 4,
    ["typing.pause_ratio"] = .10 + (i % 4) * .01,
    ["mouse.speed_median"] = 300 + (i % 6) * 5,
    ["mouse.acceleration_p95"] = 40 + (i % 5) * 2,
    ["mouse.curvature_median"] = .40 + (i % 3) * .05,
    ["mouse.click_interval_median_ms"] = 600 + (i % 4) * 10
};
