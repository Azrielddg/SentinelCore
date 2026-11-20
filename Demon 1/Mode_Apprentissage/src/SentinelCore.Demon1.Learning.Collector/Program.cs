using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using SentinelCore.Demon1.Learning;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Le collecteur nécessite Windows.");
            if (args.Length == 0 || args[0] is "help" or "--help") { PrintHelp(); return 0; }
            var options = Parse(args.Skip(1).ToArray());
            return args[0] switch
            {
                "collect" => Collect(options),
                "list" => List(options),
                _ => throw new ArgumentException("Commande inconnue.")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Collect(Dictionary<string, string> o)
    {
        var database = Need(o, "db"); var sid = Need(o, "sid"); var context = Need(o, "context");
        var statusPath = Need(o, "status"); var stopPath = Need(o, "stop");
        if (o.GetValueOrDefault("owner-confirmed") != "true") throw new InvalidOperationException("Le mode apprentissage exige une confirmation explicite du propriétaire.");
        using var repository = new SqliteLearningProfileRepository(database);
        var service = new LearningModeService(repository);
        var ignorePid = int.TryParse(o.GetValueOrDefault("ignore-pid"), out var parsedPid) ? parsedPid : 0;
        var collector = new RawInputLearningCollector(context, ignorePid);
        collector.Start();
        Console.WriteLine("CAPTURE_ACTIVE");
        IReadOnlyDictionary<string, double> latestMeasures = new Dictionary<string, double>();

        using var statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        using var windowTimer = new System.Windows.Forms.Timer { Interval = 15_000 };
        statusTimer.Tick += (_, _) =>
        {
            if (File.Exists(stopPath)) { statusTimer.Stop(); windowTimer.Stop(); Application.ExitThread(); return; }
            if (GetIdleSeconds() >= 90)
            {
                statusTimer.Stop(); windowTimer.Stop(); collector.Stop();
                WriteStatus(statusPath, collector.Snapshot(), service.GetReport(sid, context), false, "Collecte arrêtée après 90 secondes sans interaction. Redémarre-la manuellement.", latestMeasures);
                Application.ExitThread();
                return;
            }
            try { WriteStatus(statusPath, collector.Snapshot(), service.GetReport(sid, context), true, null, latestMeasures); }
            catch (Exception ex) { WriteStatus(statusPath, collector.Snapshot(), service.GetReport(sid, context), true, ex.Message, latestMeasures); }
        };
        windowTimer.Tick += (_, _) =>
        {
            try
            {
                var window = collector.TakeWindow();
                latestMeasures = window.Features;
                if (window.Features.Count == 0) return;
                var observation = new LearningWindow(sid, DateTimeOffset.UtcNow, context, window.Features, window.Quality,
                    OwnerVerification.OWNER_CONFIRMED, InputOrigin.PHYSICAL_DEVICE, ThreatContext.CLEAR);
                var result = service.Observe(observation);
                WriteStatus(statusPath, collector.Snapshot(), result.Report, true, result.Disposition == SampleDisposition.ACCEPTED ? null : result.Reason, latestMeasures);
            }
            catch (Exception ex) { WriteStatus(statusPath, collector.Snapshot(), service.GetReport(sid, context), true, ex.Message, latestMeasures); }
        };
        statusTimer.Start(); windowTimer.Start();
        WriteStatus(statusPath, collector.Snapshot(), service.GetReport(sid, context), true, null, latestMeasures);
        Application.Run(new ApplicationContext());
        collector.Stop();
        WriteStatus(statusPath, collector.Snapshot(), service.GetReport(sid, context), false, "Capture arrêtée par l’utilisateur.", latestMeasures);
        return 0;
    }

    private static int List(Dictionary<string, string> o)
    {
        var db = Need(o, "db"); var sid = Need(o, "sid");
        var take = int.TryParse(o.GetValueOrDefault("take", "50"), out var parsed) ? Math.Clamp(parsed, 1, 200) : 50;
        using var repository = new SqliteLearningProfileRepository(db);
        var output = repository.ReadRecent(sid, take).Select(row => new
        {
            row.Id,
            atLocal = row.ObservedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
            row.ContextKey,
            result = row.Disposition.ToString(),
            profile = row.State.ToString(),
            row.AcceptedWindows,
            quality = Math.Round(row.Quality, 2),
            row.Reason,
            measures = row.Features.ToDictionary(x => x.Key, x => Math.Round(x.Value, 3), StringComparer.Ordinal)
        });
        Console.WriteLine(JsonSerializer.Serialize(output));
        return 0;
    }

    private static void WriteStatus(string path, CollectorSnapshot sensor, LearningReport report, bool active, string? note, IReadOnlyDictionary<string, double> latestMeasures)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var payload = new
        {
            active,
            updatedLocal = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            sensor.WindowSeconds,
            sensor.KeyPresses,
            sensor.MouseMoves,
            sensor.Clicks,
            sensor.PendingKeys,
            report.State,
            report.AcceptedWindows,
            report.KeyboardWindows,
            report.MouseWindows,
            report.DistinctDays,
            report.CalibrationProgressPercent,
            report.Features,
            latestMeasures,
            note
        };
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(payload));
        File.Move(temp, path, true);
    }

    private static double GetIdleSeconds()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return unchecked((uint)Environment.TickCount - info.Time) / 1000d;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size; public uint Time; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetLastInputInfo(ref LastInputInfo info);

    private static Dictionary<string, string> Parse(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = args[i][2..];
            result[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
        }
        return result;
    }
    private static string Need(Dictionary<string, string> options, string name) => options.TryGetValue(name, out var value) ? value : throw new ArgumentException($"Argument --{name} manquant.");
    private static void PrintHelp() => Console.WriteLine("SentinelCore Learning Collector\n  collect --db PATH --sid SID --context CONTEXT --status PATH --stop PATH --owner-confirmed\n  list --db PATH --sid SID [--take 50]");
}

internal sealed record CollectedWindow(IReadOnlyDictionary<string, double> Features, double Quality);
internal sealed record CollectorSnapshot(int WindowSeconds, int KeyPresses, int MouseMoves, int Clicks, int PendingKeys);

internal sealed class RawInputLearningCollector
{
    private const int WindowSeconds = 15;
    private readonly RawInputWindow _window;
    private DateTimeOffset _windowStarted = DateTimeOffset.UtcNow;
    private readonly List<double> _dwell = [];
    private readonly List<double> _flight = [];
    private readonly List<double> _mouseSpeed = [];
    private readonly List<double> _mouseAccel = [];
    private readonly List<double> _mouseCurve = [];
    private readonly List<double> _clickIntervals = [];
    private readonly Dictionary<ushort, long> _keyDownAt = [];
    private long? _lastKeyDown;
    private int _keyPresses, _mouseMoves, _clicks, _longPauses;
    private long _lastClickAt;
    private double? _lastSpeed;
    private double? _lastHeading;
    private long _lastMouseAt;
    private int _pendingKeys;

    public RawInputLearningCollector(string context, int ignorePid)
    {
        _window = new RawInputWindow(OnKeyboard, OnMouse, ignorePid);
    }
    public void Start() => _window.Start();
    public void Stop() => _window.Stop();

    public CollectorSnapshot Snapshot() => new(WindowSeconds, Volatile.Read(ref _keyPresses), Volatile.Read(ref _mouseMoves), Volatile.Read(ref _clicks), Volatile.Read(ref _pendingKeys));

    public CollectedWindow TakeWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var elapsed = Math.Max(1, (now - _windowStarted).TotalSeconds);
        var features = new Dictionary<string, double>(StringComparer.Ordinal);
        if (_keyPresses >= 3)
        {
            features["typing.rate"] = _keyPresses / elapsed * 60;
            if (_dwell.Count > 0) features["typing.dwell_median_ms"] = Median(_dwell);
            if (_flight.Count > 0) features["typing.flight_median_ms"] = Median(_flight);
            if (_keyPresses > 1) features["typing.pause_ratio"] = _longPauses / (double)Math.Max(1, _keyPresses - 1);
        }
        if (_mouseMoves >= 3)
        {
            if (_mouseSpeed.Count > 0) features["mouse.speed_median"] = Median(_mouseSpeed);
            if (_mouseAccel.Count > 0) features["mouse.acceleration_p95"] = Quantile(_mouseAccel, .95);
            if (_mouseCurve.Count > 0) features["mouse.curvature_median"] = Median(_mouseCurve);
            if (_clickIntervals.Count > 0) features["mouse.click_interval_median_ms"] = Median(_clickIntervals);
        }
        var eventCount = _keyPresses + _mouseMoves + _clicks;
        var quality = Math.Clamp(eventCount / 50d, 0, 1);
        ResetWindow(now);
        return new CollectedWindow(features, quality);
    }

    private void OnKeyboard(ushort scanCode, bool isUp)
    {
        var now = Environment.TickCount64;
        if (!isUp)
        {
            if (!_keyDownAt.ContainsKey(scanCode))
            {
                _keyDownAt[scanCode] = now;
                _pendingKeys = _keyDownAt.Count;
                _keyPresses++;
                if (_lastKeyDown.HasValue)
                {
                    var gap = now - _lastKeyDown.Value;
                    if (gap > 750) _longPauses++;
                    _flight.Add(gap);
                }
                _lastKeyDown = now;
            }
        }
        else if (_keyDownAt.Remove(scanCode, out var downAt))
        {
            var duration = Math.Clamp(now - downAt, 1, 3000);
            _dwell.Add(duration);
            _pendingKeys = _keyDownAt.Count;
        }
    }

    private void OnMouse(int dx, int dy, ushort buttonFlags)
    {
        var now = Environment.TickCount64;
        if (dx != 0 || dy != 0)
        {
            _mouseMoves++;
            var dt = Math.Max(1, now - _lastMouseAt);
            var distance = Math.Sqrt((double)dx * dx + (double)dy * dy);
            var speed = distance / dt * 1000;
            _mouseSpeed.Add(speed);
            if (_lastSpeed.HasValue) _mouseAccel.Add(Math.Abs(speed - _lastSpeed.Value) / dt * 1000);
            if (distance > 0)
            {
                var heading = Math.Atan2(dy, dx);
                if (_lastHeading.HasValue)
                {
                    var delta = Math.Abs(heading - _lastHeading.Value);
                    _mouseCurve.Add(Math.Min(delta, 2 * Math.PI - delta) / Math.PI);
                }
                _lastHeading = heading;
            }
            _lastSpeed = speed;
            _lastMouseAt = now;
        }
        const ushort buttonDownMask = 0x0001 | 0x0004 | 0x0010 | 0x0040 | 0x0100 | 0x0400;
        if ((buttonFlags & buttonDownMask) != 0)
        {
            _clicks++;
            if (_lastClickAt != 0) _clickIntervals.Add(Math.Clamp(now - _lastClickAt, 1, 10000));
            _lastClickAt = now;
        }
    }

    private void ResetWindow(DateTimeOffset now)
    {
        _windowStarted = now;
        _dwell.Clear(); _flight.Clear(); _mouseSpeed.Clear(); _mouseAccel.Clear(); _mouseCurve.Clear(); _clickIntervals.Clear();
        _keyPresses = 0; _mouseMoves = 0; _clicks = 0; _longPauses = 0;
    }
    private static double Median(List<double> values) { var a = values.Order().ToArray(); return a.Length == 0 ? 0 : a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2; }
    private static double Quantile(List<double> values, double q) { var a = values.Order().ToArray(); if (a.Length == 0) return 0; var p = (a.Length - 1) * q; var lo = (int)Math.Floor(p); var hi = (int)Math.Ceiling(p); return a[lo] + (a[hi] - a[lo]) * (p - lo); }
}

internal sealed class RawInputWindow(Action<ushort, bool> keyboard, Action<int, int, ushort> mouse, int ignorePid) : NativeWindow
{
    private const int WmInput = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const uint RimTypeMouse = 0;
    private const uint RimTypeKeyboard = 1;
    private const uint RiKeyBreak = 1;
    private const uint RidevInputSink = 0x00000100;
    private static readonly IntPtr HwndMessage = new(-3);
    private readonly Action<ushort, bool> _keyboard = keyboard;
    private readonly Action<int, int, ushort> _mouse = mouse;
    private readonly int _ignorePid = ignorePid;
    private bool _started;

    public void Start()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        CreateHandle(new CreateParams { Caption = "SentinelCore learning raw input", Parent = HwndMessage });
        var devices = new[]
        {
            new RawInputDevice { UsagePage = 0x01, Usage = 0x02, Flags = RidevInputSink, Target = Handle },
            new RawInputDevice { UsagePage = 0x01, Usage = 0x06, Flags = RidevInputSink, Target = Handle }
        };
        if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>()))
            throw new InvalidOperationException($"Windows n’a pas activé Raw Input ({Marshal.GetLastWin32Error()}).");
        _started = true;
    }

    public void Stop()
    {
        if (!_started) return;
        var devices = new[]
        {
            new RawInputDevice { UsagePage = 0x01, Usage = 0x02, Flags = 0x00000001, Target = IntPtr.Zero },
            new RawInputDevice { UsagePage = 0x01, Usage = 0x06, Flags = 0x00000001, Target = IntPtr.Zero }
        };
        RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>());
        DestroyHandle();
        _started = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmInput && ForegroundProcessId() != _ignorePid) ReadInput(m.LParam);
        DefWindowProc(m.HWnd, (uint)m.Msg, m.WParam, m.LParam);
        base.WndProc(ref m);
    }

    private void ReadInput(IntPtr rawHandle)
    {
        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        if (GetRawInputData(rawHandle, RidInput, IntPtr.Zero, ref size, headerSize) == uint.MaxValue || size == 0) return;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(rawHandle, RidInput, buffer, ref size, headerSize) == uint.MaxValue) return;
            var header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            var payload = IntPtr.Add(buffer, Marshal.SizeOf<RawInputHeader>());
            if (header.Type == RimTypeKeyboard)
            {
                var key = Marshal.PtrToStructure<RawKeyboard>(payload);
                if (key.MakeCode != 0 && key.MakeCode != 0xFF) _keyboard(key.MakeCode, (key.Flags & RiKeyBreak) != 0);
            }
            else if (header.Type == RimTypeMouse)
            {
                var movement = Marshal.PtrToStructure<RawMouse>(payload);
                if ((movement.Flags & 0x0001) == 0) _mouse(movement.LastX, movement.LastY, movement.ButtonFlags);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static int ForegroundProcessId()
    {
        var hwnd = GetForegroundWindow();
        GetWindowThreadProcessId(hwnd, out var pid);
        return unchecked((int)pid);
    }

    [StructLayout(LayoutKind.Sequential)] private struct RawInputDevice { public ushort UsagePage; public ushort Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] private struct RawInputHeader { public uint Type; public uint Size; public IntPtr Device; public IntPtr WParam; }
    [StructLayout(LayoutKind.Sequential)] private struct RawKeyboard { public ushort MakeCode; public ushort Flags; public ushort Reserved; public ushort VKey; public uint Message; public uint ExtraInformation; }
    [StructLayout(LayoutKind.Explicit)] private struct RawMouse
    {
        [FieldOffset(0)] public ushort Flags;
        [FieldOffset(4)] public ushort ButtonFlags;
        [FieldOffset(6)] public ushort ButtonData;
        [FieldOffset(8)] public uint RawButtons;
        [FieldOffset(12)] public int LastX;
        [FieldOffset(16)] public int LastY;
        [FieldOffset(20)] public uint ExtraInformation;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint numDevices, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
