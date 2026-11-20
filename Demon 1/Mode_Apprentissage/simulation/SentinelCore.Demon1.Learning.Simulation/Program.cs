using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SentinelCore.Demon1.Learning;
using SentinelCore.Demon1.Learning.Controller;

if (args.Length == 2 && args[0] == "--collector-smoke")
{
    await RunCollectorSmoke(args[1]);
    return;
}

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

static async Task RunCollectorSmoke(string appExe)
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Ce contrôle nécessite Windows.");
    if (!File.Exists(appExe)) throw new FileNotFoundException("Application publiée introuvable.", appExe);
    var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
    var root = Path.Combine(Path.GetTempPath(), $"sentinelcore-collector-smoke-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var authRoot = Path.Combine(root, "auth-check"); var auth = new AuthStore(authRoot);
    const string testPassword = "SentinelCore-Test-Only-Password-2026";
    auth.SetPassword(testPassword);
    if (!auth.VerifyPassword(testPassword) || auth.VerifyPassword(testPassword + "wrong")) throw new InvalidDataException("L’enregistrement ou la vérification du mot de passe maître a échoué.");
    var authFileText = File.ReadAllText(Path.Combine(authRoot, "auth.bin"));
    if (authFileText.Contains(testPassword, StringComparison.Ordinal)) throw new InvalidDataException("Le mot de passe maître apparaît en clair dans son fichier.");
    var database = Path.Combine(root, "learning.db"); var status = Path.Combine(root, "status.json"); var stop = Path.Combine(root, "stop.signal");
    var ticket = Path.Combine(root, "ticket.bin");
    File.WriteAllText(status, "existing status to exercise replacement");
    var proof = OneTimeAuthorizationTicket.Create(ticket, sid);
    var start = new ProcessStartInfo(appExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in new[] { "--worker", "collect", "--db", database, "--sid", sid, "--context", "SMOKE_TEST", "--status", status, "--stop", stop, "--auth-ticket", ticket, "--auth-proof", proof, "--window-seconds", "10", "--idle-seconds", "30", "--ignore-pid", Environment.ProcessId.ToString() }) start.ArgumentList.Add(arg);
    using var worker = Process.Start(start) ?? throw new InvalidOperationException("Le collecteur n’a pas démarré.");
    var deadline = DateTime.UtcNow.AddSeconds(15);
    var activeSeen = false;
    while (!activeSeen && DateTime.UtcNow < deadline)
    {
        if (worker.HasExited) break;
        try
        {
            using var state = System.Text.Json.JsonDocument.Parse(File.ReadAllText(status));
            activeSeen = state.RootElement.TryGetProperty("active", out var active) && active.GetBoolean();
        }
        catch (IOException) { }
        catch (System.Text.Json.JsonException) { }
        await Task.Delay(200);
    }
    if (!activeSeen) throw new TimeoutException("Le collecteur n’a pas écrit son état actif dans status.json.");
    await Task.Delay(1200);
    File.WriteAllText(stop, "stop");
    if (!worker.WaitForExit(10000)) { worker.Kill(entireProcessTree: true); throw new TimeoutException("Le collecteur n’a pas répondu au signal d’arrêt."); }
    var errors = await worker.StandardError.ReadToEndAsync();
    if (worker.ExitCode != 0) throw new InvalidOperationException($"Le collecteur a échoué ({worker.ExitCode}) : {errors}");
    using var finalStatus = System.Text.Json.JsonDocument.Parse(File.ReadAllText(status));
    if (!finalStatus.RootElement.TryGetProperty("active", out _)) throw new InvalidDataException("Le JSON d’état final n’a pas le champ active.");
    if (File.Exists(ticket)) throw new InvalidDataException("Le ticket d’autorisation à usage unique n’a pas été consommé.");
    var deniedTicket = Path.Combine(root, "denied-ticket.bin"); var deniedProof = OneTimeAuthorizationTicket.Create(deniedTicket, sid);
    var deniedStatus = Path.Combine(root, "denied-status.json"); var deniedStop = Path.Combine(root, "denied-stop.signal");
    var deniedStart = new ProcessStartInfo(appExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in new[] { "--worker", "collect", "--db", database, "--sid", sid, "--context", "SMOKE_TEST", "--status", deniedStatus, "--stop", deniedStop, "--auth-ticket", deniedTicket, "--auth-proof", deniedProof + "x", "--window-seconds", "10", "--idle-seconds", "30" }) deniedStart.ArgumentList.Add(arg);
    using var deniedWorker = Process.Start(deniedStart) ?? throw new InvalidOperationException("Le scénario de preuve invalide n’a pas démarré.");
    if (!deniedWorker.WaitForExit(10000)) { deniedWorker.Kill(entireProcessTree: true); throw new TimeoutException("Le scénario de preuve invalide a expiré."); }
    if (deniedWorker.ExitCode == 0 || File.Exists(deniedStatus)) throw new InvalidDataException("Une preuve invalide a pu démarrer la collecte.");
    Console.WriteLine("OK — l’EXE a consommé son ticket et démarré le collecteur.");
    Console.WriteLine("OK — status.json préexistant a été réécrit sans erreur d’accès.");
    Console.WriteLine("OK — l’arrêt manuel a terminé le processus et le JSON final est lisible.");
    Console.WriteLine("OK — une preuve incorrecte est refusée sans démarrer de collecte.");
    Console.WriteLine("OK — le mot de passe maître est vérifié et son fichier ne contient pas le secret en clair.");
    Console.WriteLine($"Base temporaire de validation : {root}");
}
