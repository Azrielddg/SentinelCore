using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SentinelCore.Demon1.Learning;

public enum LearningState { NO_PROFILE, CALIBRATING, PROVISIONAL, READY, FROZEN }
public enum OwnerVerification { NONE, OWNER_CONFIRMED, WINDOWS_HELLO, VISUAL_MATCH }
public enum InputOrigin { HUMAN_LIKELY, PHYSICAL_DEVICE, INJECTED, UNKNOWN }
public enum ThreatContext { CLEAR, SUSPICIOUS }
public enum SampleDisposition { ACCEPTED, QUARANTINED, REJECTED }

public sealed record LearningWindow(
    string Sid,
    DateTimeOffset ObservedAtUtc,
    string ContextKey,
    IReadOnlyDictionary<string, double> Features,
    double Quality,
    OwnerVerification OwnerVerification,
    InputOrigin InputOrigin,
    ThreatContext ThreatContext);

public sealed record FeatureBaseline(int Count, double Median, double Mad, double Iqr, double LowerQuartile, double UpperQuartile);
public sealed record LearningReport(LearningState State, int AcceptedWindows, int KeyboardWindows, int MouseWindows, int DistinctDays, double CalibrationProgressPercent, IReadOnlyDictionary<string, FeatureBaseline> Features, string Summary);
public sealed record LearningResult(SampleDisposition Disposition, string Reason, LearningReport Report);
public sealed record LearningHistoryEntry(long Id, DateTimeOffset ObservedAtUtc, string ContextKey, SampleDisposition Disposition, string Reason, LearningState State, int AcceptedWindows, double Quality, IReadOnlyDictionary<string, double> Features);

public interface ILearningHistoryRepository
{
    void Append(LearningWindow window, LearningResult result);
    IReadOnlyList<LearningHistoryEntry> ReadRecent(string sid, int take = 100);
}

public interface IAtomicLearningRepository : ILearningProfileRepository, ILearningHistoryRepository
{
    void Record(LearningWindow window, LearningResult result, LearningProfile? updatedProfile);
}

public sealed record ContextProfile(
    int AcceptedWindows,
    int KeyboardWindows,
    int MouseWindows,
    List<string> DayKeys,
    Dictionary<string, List<double>> FeatureSamples);

public sealed record LearningProfile(
    int SchemaVersion,
    string Sid,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    bool Frozen,
    string? FreezeReason,
    Dictionary<string, ContextProfile> Contexts);

public interface ILearningProfileRepository
{
    LearningProfile? Load(string sid);
    void Save(string sid, LearningProfile profile);
}

public sealed class InMemoryLearningProfileRepository : IAtomicLearningRepository
{
    private readonly ConcurrentDictionary<string, LearningProfile> _profiles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<LearningHistoryEntry>> _history = new(StringComparer.Ordinal);
    private long _nextId;
    public LearningProfile? Load(string sid) => _profiles.GetValueOrDefault(sid);
    public void Save(string sid, LearningProfile profile) => _profiles[sid] = profile;
    public void Append(LearningWindow window, LearningResult result)
    {
        var queue = _history.GetOrAdd(window.Sid, _ => new ConcurrentQueue<LearningHistoryEntry>());
        queue.Enqueue(new(Interlocked.Increment(ref _nextId), window.ObservedAtUtc, window.ContextKey, result.Disposition, result.Reason, result.Report.State, result.Report.AcceptedWindows, window.Quality, new Dictionary<string, double>(window.Features)));
        while (queue.Count > 10000) queue.TryDequeue(out _);
    }
    public void Record(LearningWindow window, LearningResult result, LearningProfile? updatedProfile)
    {
        if (updatedProfile is not null) Save(window.Sid, updatedProfile);
        Append(window, result);
    }
    public IReadOnlyList<LearningHistoryEntry> ReadRecent(string sid, int take = 100) => _history.TryGetValue(sid, out var queue) ? queue.Reverse().Take(Math.Clamp(take, 1, 1000)).ToArray() : [];
}

/// <summary>Local per-user DPAPI storage; only aggregate feature windows are persisted.</summary>
public sealed class DpapiLearningProfileRepository(string rootDirectory) : ILearningProfileRepository
{
    private readonly string _root = Path.GetFullPath(rootDirectory);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public LearningProfile? Load(string sid)
    {
        var path = ProfilePath(sid);
        if (!File.Exists(path)) return null;
        var json = CurrentUserDpapi.Unprotect(File.ReadAllBytes(path), sid);
        var profile = JsonSerializer.Deserialize<LearningProfile>(json, JsonOptions)
            ?? throw new InvalidDataException("Profil d’apprentissage vide ou illisible.");
        if (!StringComparer.Ordinal.Equals(profile.Sid, sid)) throw new InvalidDataException("Le SID du profil ne correspond pas au SID demandé.");
        return profile;
    }

    public void Save(string sid, LearningProfile profile)
    {
        if (!StringComparer.Ordinal.Equals(sid, profile.Sid)) throw new ArgumentException("Le SID du profil est incohérent.", nameof(profile));
        var path = ProfilePath(sid);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var protectedBytes = CurrentUserDpapi.Protect(JsonSerializer.SerializeToUtf8Bytes(profile, JsonOptions), sid);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temp, protectedBytes);
        File.Move(temp, path, true);
    }

    private string ProfilePath(string sid)
    {
        var folder = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..24];
        return Path.Combine(_root, folder, "learning.profile");
    }
}

internal static class CurrentUserDpapi
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Blob { public int Length; public IntPtr Data; }
    [System.Runtime.InteropServices.DllImport("crypt32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [System.Runtime.InteropServices.DllImport("crypt32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    public static byte[] Protect(byte[] data, string sid) => Transform(data, sid, true);
    public static byte[] Unprotect(byte[] data, string sid) => Transform(data, sid, false);

    private static byte[] Transform(byte[] data, string sid, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Le stockage protégé du mode apprentissage nécessite Windows DPAPI.");
        var input = ToBlob(data);
        var entropyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(sid));
        var entropy = ToBlob(entropyBytes);
        Blob output = default;
        try
        {
            var ok = protect
                ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            var result = new byte[output.Length];
            System.Runtime.InteropServices.Marshal.Copy(output.Data, result, 0, output.Length);
            return result;
        }
        finally
        {
            Free(input); Free(entropy);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    private static Blob ToBlob(byte[] data)
    {
        var ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(data.Length);
        System.Runtime.InteropServices.Marshal.Copy(data, 0, ptr, data.Length);
        return new Blob { Length = data.Length, Data = ptr };
    }
    private static void Free(Blob blob) { if (blob.Data != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(blob.Data); }
}

public sealed class LearningModeService(ILearningProfileRepository repository)
{
    private const int MaxSamplesPerFeature = 256;
    private const int MaxContextsPerProfile = 256;
    private const int ProvisionalWindows = 20;
    private const int ProvisionalDays = 3;
    private const int ReadyWindows = 60;
    private const int ReadyDays = 7;
    private const int ModalityWindowsRequired = 30;
    private const double MinimumQuality = 0.80;
    private readonly ILearningProfileRepository _repository = repository;
    private readonly object _gate = new();

    private static readonly HashSet<string> AllowedFeatures = new(StringComparer.Ordinal)
    {
        "typing.rate", "typing.dwell_median_ms", "typing.flight_median_ms", "typing.pause_ratio",
        "mouse.speed_median", "mouse.acceleration_p95", "mouse.curvature_median", "mouse.click_interval_median_ms"
    };

    public LearningResult Observe(LearningWindow window)
    {
        lock (_gate)
        {
            ValidateEnvelope(window);
            var profile = _repository.Load(window.Sid) ?? NewProfile(window.Sid, window.ObservedAtUtc);
            if (!profile.Contexts.ContainsKey(window.ContextKey) && profile.Contexts.Count >= MaxContextsPerProfile)
                return Record(window, SampleDisposition.REJECTED, "Limite de contextes de profil atteinte; demander une calibration propriétaire explicite.", profile, EmptyContext(), window.ContextKey, null);
            var context = profile.Contexts.GetValueOrDefault(window.ContextKey) ?? EmptyContext();
            if (profile.Frozen) return Record(window, SampleDisposition.QUARANTINED, $"Apprentissage gelé : {profile.FreezeReason ?? "motif non précisé"}.", profile, context, window.ContextKey, null);

            var rejection = EligibilityReason(window);
            if (rejection is not null)
            {
                var profileToPersist = window.ThreatContext == ThreatContext.SUSPICIOUS
                    ? profile with { Frozen = true, FreezeReason = "Signal de menace pendant l’apprentissage.", UpdatedAtUtc = window.ObservedAtUtc }
                    : null;
                var reportProfile = profileToPersist ?? profile;
                return Record(window, SampleDisposition.QUARANTINED, rejection, reportProfile, context, window.ContextKey, profileToPersist);
            }
            var samples = context.FeatureSamples.ToDictionary(x => x.Key, x => new List<double>(x.Value), StringComparer.Ordinal);
            foreach (var (name, value) in window.Features)
            {
                if (!samples.TryGetValue(name, out var values)) samples[name] = values = [];
                values.Add(value);
                if (values.Count > MaxSamplesPerFeature) values.RemoveAt(0);
            }
            var hasKeyboard = window.Features.Keys.Any(IsKeyboardFeature);
            var hasMouse = window.Features.Keys.Any(IsMouseFeature);
            var day = window.ObservedAtUtc.UtcDateTime.ToString("yyyy-MM-dd");
            var days = new List<string>(context.DayKeys);
            if (!days.Contains(day, StringComparer.Ordinal)) days.Add(day);
            if (days.Count > 365) days.RemoveAt(0);

            var updatedContext = new ContextProfile(
                context.AcceptedWindows + 1,
                context.KeyboardWindows + (hasKeyboard ? 1 : 0),
                context.MouseWindows + (hasMouse ? 1 : 0),
                days,
                samples);
            var contexts = new Dictionary<string, ContextProfile>(profile.Contexts, StringComparer.Ordinal) { [window.ContextKey] = updatedContext };
            var updatedProfile = profile with { UpdatedAtUtc = window.ObservedAtUtc, Contexts = contexts };
            return Record(window, SampleDisposition.ACCEPTED, "Fenêtre agrégée admise au profil; aucune frappe brute n’est conservée.", updatedProfile, updatedContext, window.ContextKey, updatedProfile);
        }
    }

    public LearningReport GetReport(string sid, string contextKey)
    {
        lock (_gate)
        {
            var profile = _repository.Load(sid);
            if (profile is null) return EmptyReport(LearningState.NO_PROFILE, "Aucun profil d’apprentissage.");
            if (!profile.Contexts.TryGetValue(contextKey, out var context)) return EmptyReport(LearningState.CALIBRATING, "Aucun échantillon pour ce contexte clavier/souris.");
            return BuildReport(profile, context, contextKey);
        }
    }

    public void Freeze(string sid, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Un motif de gel est obligatoire.", nameof(reason));
        lock (_gate)
        {
            var profile = _repository.Load(sid);
            if (profile is null) return;
            _repository.Save(sid, profile with { Frozen = true, FreezeReason = reason, UpdatedAtUtc = DateTimeOffset.UtcNow });
        }
    }

    public bool Resume(string sid, OwnerVerification verification, bool explicitOwnerApproval)
    {
        if (!explicitOwnerApproval || verification is OwnerVerification.NONE) return false;
        lock (_gate)
        {
            var profile = _repository.Load(sid);
            if (profile is null || !profile.Frozen) return false;
            _repository.Save(sid, profile with { Frozen = false, FreezeReason = null, UpdatedAtUtc = DateTimeOffset.UtcNow });
            return true;
        }
    }

    private static void ValidateEnvelope(LearningWindow window)
    {
        if (string.IsNullOrWhiteSpace(window.Sid) || window.Sid.Length > 184) throw new ArgumentException("SID absent ou invalide.");
        if (string.IsNullOrWhiteSpace(window.ContextKey) || window.ContextKey.Length > 64 || window.ContextKey.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '|')))
            throw new ArgumentException("ContextKey doit être un identifiant court sans chemin ni donnée personnelle.");
        if (window.Features.Count == 0 || window.Features.Count > AllowedFeatures.Count) throw new ArgumentException("Nombre de caractéristiques invalide.");
        foreach (var (name, value) in window.Features)
        {
            if (!AllowedFeatures.Contains(name)) throw new ArgumentException($"Caractéristique non autorisée : {name}.");
            if (!double.IsFinite(value) || value < 0) throw new ArgumentException($"Valeur invalide pour {name}.");
        }
    }

    private static string? EligibilityReason(LearningWindow window)
    {
        if (window.OwnerVerification is OwnerVerification.NONE) return "Propriétaire non confirmé; observation mise en quarantaine.";
        if (window.InputOrigin is not (InputOrigin.HUMAN_LIKELY or InputOrigin.PHYSICAL_DEVICE)) return "Provenance des entrées non admissible; observation mise en quarantaine.";
        if (window.ThreatContext is not ThreatContext.CLEAR) return "Signal de menace présent; observation mise en quarantaine.";
        if (!double.IsFinite(window.Quality) || window.Quality < MinimumQuality) return "Qualité du capteur inférieure à 0,80; observation mise en quarantaine.";
        return null;
    }

    private static LearningResult Result(SampleDisposition disposition, string reason, LearningProfile profile, ContextProfile context, string contextKey)
        => new(disposition, reason, BuildReport(profile, context, contextKey));

    private LearningResult Record(LearningWindow window, SampleDisposition disposition, string reason, LearningProfile profile, ContextProfile context, string contextKey, LearningProfile? profileToPersist)
    {
        var result = Result(disposition, reason, profile, context, contextKey);
        if (_repository is IAtomicLearningRepository atomic) atomic.Record(window, result, profileToPersist);
        else
        {
            if (profileToPersist is not null) _repository.Save(window.Sid, profileToPersist);
            if (_repository is ILearningHistoryRepository history) history.Append(window, result);
        }
        return result;
    }

    private static LearningReport BuildReport(LearningProfile profile, ContextProfile context, string contextKey)
    {
        var state = profile.Frozen ? LearningState.FROZEN : GetState(context);
        var baselines = context.FeatureSamples.ToDictionary(
            x => x.Key,
            x =>
            {
                var values = x.Value;
                var median = Median(values);
                return new FeatureBaseline(values.Count, median, Median(values.Select(v => Math.Abs(v - median)).ToList()), Quantile(values, .75) - Quantile(values, .25), Quantile(values, .25), Quantile(values, .75));
            }, StringComparer.Ordinal);
        var readinessRatio = Math.Min(Math.Min(context.AcceptedWindows / (double)ReadyWindows, context.DayKeys.Count / (double)ReadyDays), Math.Min(context.KeyboardWindows / (double)ModalityWindowsRequired, context.MouseWindows / (double)ModalityWindowsRequired));
        var progress = Math.Min(100, readinessRatio * 100);
        var summary = state switch
        {
            LearningState.READY => $"Profil prêt pour {contextKey}; adaptation limitée et filtrée activée.",
            LearningState.PROVISIONAL => $"Profil provisoire : {context.AcceptedWindows}/{ReadyWindows} fenêtres et {context.DayKeys.Count}/{ReadyDays} jours.",
            LearningState.FROZEN => $"Apprentissage gelé : {profile.FreezeReason ?? "motif non précisé"}.",
            _ => $"Calibration en cours : {context.AcceptedWindows}/{ProvisionalWindows} fenêtres et {context.DayKeys.Count}/{ProvisionalDays} jours."
        };
        return new(state, context.AcceptedWindows, context.KeyboardWindows, context.MouseWindows, context.DayKeys.Count, progress, baselines, summary);
    }

    private static LearningReport EmptyReport(LearningState state, string summary) => new(state, 0, 0, 0, 0, 0, new Dictionary<string, FeatureBaseline>(), summary);

    private static LearningState GetState(ContextProfile context)
    {
        if (context.AcceptedWindows >= ReadyWindows && context.DayKeys.Count >= ReadyDays && context.KeyboardWindows >= ModalityWindowsRequired && context.MouseWindows >= ModalityWindowsRequired)
            return LearningState.READY;
        if (context.AcceptedWindows >= ProvisionalWindows && context.DayKeys.Count >= ProvisionalDays) return LearningState.PROVISIONAL;
        return context.AcceptedWindows == 0 ? LearningState.NO_PROFILE : LearningState.CALIBRATING;
    }

    private static LearningProfile NewProfile(string sid, DateTimeOffset now) => new(1, sid, now, now, false, null, new(StringComparer.Ordinal));
    private static ContextProfile EmptyContext() => new(0, 0, 0, [], new(StringComparer.Ordinal));
    private static bool IsKeyboardFeature(string name) => name.StartsWith("typing.", StringComparison.Ordinal);
    private static bool IsMouseFeature(string name) => name.StartsWith("mouse.", StringComparison.Ordinal);
    private static double Median(IReadOnlyCollection<double> values) { var a = values.Order().ToArray(); if (a.Length == 0) return 0; return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2; }
    private static double Quantile(IReadOnlyCollection<double> values, double q) { var a = values.Order().ToArray(); if (a.Length == 0) return 0; var p = (a.Length - 1) * q; var lo = (int)Math.Floor(p); var hi = (int)Math.Ceiling(p); return a[lo] + (a[hi] - a[lo]) * (p - lo); }
}


