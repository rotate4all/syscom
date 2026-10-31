#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Syscom.Server.Sensors;

/// <summary>
/// Frame rate and frame time of the app drawing the most frames (normally the game), for any GPU vendor.
///
/// Reads Windows' own graphics "present" events through a real-time ETW session (providers Microsoft-Windows-DXGI
/// and Microsoft-Windows-D3D9), the same source PresentMon and FrameView use. Nothing is injected into the game, so
/// it is safe with anti-cheat. No extra dependency: the ETW API is called directly. Needs administrator rights,
/// which the SYSCOM service has.
///
/// Covers Direct3D 9 to 12 games, and Vulkan/OpenGL games whose driver presents through DXGI. The session only runs
/// while someone is watching (the hub only polls with clients connected) and stops 30 s after the last poll.
/// </summary>
public sealed unsafe class PresentMonitorSource : ISensorSource
{
    public const string FpsId = "/frames/fps";
    public const string FrameTimeId = "/frames/frametime";
    public const string FrameTimeWorstId = "/frames/frametime-worst";

    private const string SessionName = "SYSCOM-FrameRate";
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3d9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const ushort DxgiPresentStart = 42;
    private const ushort D3d9PresentStart = 1;
    private const float MinCandidateFps = 5;

    // Window compositor, shell and overlays present too; they're never "the game".
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm", "csrss", "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "TextInputHost",
        "LockApp", "NVIDIA Overlay", "nvcontainer", "GameBar", "GameBarFTServer", "syscom", "syscom-tray",
    };

    private static PresentMonitorSource? _active; // the static ETW callback forwards events here

    private readonly ILogger _log;
    private readonly object _gate = new();        // incoming events
    private readonly object _sessionGate = new(); // start / stop
    private readonly Timer _idleTimer;
    private Dictionary<int, List<long>> _incoming = new();
    private readonly Dictionary<int, List<long>> _frames = new();   // pid → present timestamps (QPC), last ~1 s
    private readonly Dictionary<int, long> _lastSeen = new();       // pid → when its events last arrived (TickCount64)
    private readonly Dictionary<int, string> _names = new();
    private ulong _session;
    private ulong _trace = InvalidTrace;
    private Thread? _thread;
    private IntPtr _loggerName;
    private long _lastPoll;
    private int _chosen;
    private string? _startError;

    private const ulong InvalidTrace = ulong.MaxValue;

    public PresentMonitorSource(ILogger log)
    {
        _log = log;
        Catalog = BuildCatalog("No game running");
        _idleTimer = new Timer(_ => StopIfIdle(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public string Name => "Frame rate";
    public IReadOnlyList<SensorInfo> Catalog { get; private set; }

    private static IReadOnlyList<SensorInfo> BuildCatalog(string app) => new[]
    {
        new SensorInfo(FpsId, "Frame rate", app, "Game", "Fps", Units.For("Fps")),
        new SensorInfo(FrameTimeId, "Frame time", app, "Game", "Frametime", Units.For("Frametime")),
        new SensorInfo(FrameTimeWorstId, "Frame time (worst)", app, "Game", "Frametime", Units.For("Frametime")),
    };

    public bool Update(Dictionary<string, float> values)
    {
        Volatile.Write(ref _lastPoll, Environment.TickCount64);
        EnsureRunning();

        Dictionary<int, List<long>> batch;
        lock (_gate) { batch = _incoming; _incoming = new(); }
        var tick = Environment.TickCount64;
        foreach (var (pid, stamps) in batch)
        {
            if (!_frames.TryGetValue(pid, out var list)) _frames[pid] = list = new List<long>(256);
            list.AddRange(stamps);
            _lastSeen[pid] = tick;
        }

        // Per app: frames within the last second of its own events (ETW delivers in batches, so anchor on the newest).
        var freq = (double)Stopwatch.Frequency;
        var stats = new Dictionary<int, (double Fps, double AvgMs, double WorstMs)>();
        foreach (var pid in _frames.Keys.ToList())
        {
            if (tick - _lastSeen.GetValueOrDefault(pid) > 3000) { _frames.Remove(pid); _lastSeen.Remove(pid); continue; }
            var list = _frames[pid];
            list.Sort();
            var last = list[^1];
            var cutoff = last - Stopwatch.Frequency;
            list.RemoveAll(t => t < cutoff);
            if (list.Count < 2) continue;
            var span = (last - list[0]) / freq;
            if (span <= 0) continue;
            double worst = 0;
            for (var i = 1; i < list.Count; i++) worst = Math.Max(worst, (list[i] - list[i - 1]) / freq);
            var fps = (list.Count - 1) / span;
            stats[pid] = (fps, 1000 * span / (list.Count - 1), 1000 * worst);
        }

        // Pick the app with the most frames; stick with the current one unless another clearly leads.
        var candidates = stats.Where(kv => kv.Value.Fps >= MinCandidateFps && !Ignored.Contains(NameOf(kv.Key))).ToList();
        var leader = candidates.OrderByDescending(kv => kv.Value.Fps).Select(kv => kv.Key).FirstOrDefault();
        if (leader != 0 && _chosen != leader &&
            !(stats.TryGetValue(_chosen, out var cur) && cur.Fps >= MinCandidateFps && stats[leader].Fps < cur.Fps * 1.5))
        {
            _chosen = leader;
        }
        else if (leader == 0 && !stats.ContainsKey(_chosen))
        {
            _chosen = 0;
        }

        var changed = false;
        var app = _chosen != 0 ? NameOf(_chosen) : "No game running";
        if (Catalog[0].Hardware != app)
        {
            Catalog = BuildCatalog(app);
            changed = true;
        }
        if (_chosen != 0 && stats.TryGetValue(_chosen, out var s))
        {
            values[FpsId] = (float)s.Fps;
            values[FrameTimeId] = (float)s.AvgMs;
            values[FrameTimeWorstId] = (float)s.WorstMs;
        }
        foreach (var pid in _names.Keys.Where(p => !_frames.ContainsKey(p)).ToList()) _names.Remove(pid);
        return changed;
    }

    private string NameOf(int pid)
    {
        if (_names.TryGetValue(pid, out var n)) return n;
        try { n = Process.GetProcessById(pid).ProcessName; }
        catch { n = $"Process {pid}"; }
        _names[pid] = n;
        return n;
    }

    // ---- ETW session

    private void EnsureRunning()
    {
        if (_thread is not null) return;
        lock (_sessionGate)
        {
            if (_thread is not null) return;
            if (_startError is not null) throw new InvalidOperationException(_startError);
            try
            {
                Start();
            }
            catch (Exception ex)
            {
                // Typically "access denied" when not running as administrator. Don't retry every poll.
                _startError = ex.Message;
                Stop();
                throw;
            }
        }
    }

    private void Start()
    {
        _active = this;
        var props = NewProperties(out _);
        try
        {
            var err = StartTraceW(out _session, SessionName, props);
            if (err == ErrorAlreadyExists)
            {
                // Left over from a crashed run: stop it and start fresh.
                ControlTraceW(0, SessionName, props, ControlStop);
                Marshal.FreeHGlobal((IntPtr)props);
                props = NewProperties(out _);
                err = StartTraceW(out _session, SessionName, props);
            }
            if (err == ErrorAccessDenied) throw new InvalidOperationException("Frame rate needs administrator rights (Windows present events).");
            if (err != 0) throw new InvalidOperationException($"Could not start the frame rate session (error {err}).");
        }
        finally
        {
            Marshal.FreeHGlobal((IntPtr)props);
        }

        foreach (var provider in new[] { DxgiProvider, D3d9Provider })
        {
            var guid = provider;
            var err = EnableTraceEx2(_session, &guid, EnableProvider, TraceLevelInformation, ulong.MaxValue, 0, 0, null);
            if (err != 0) _log.LogWarning("Could not enable present events from {Provider} (error {Error})", provider, err);
        }

        var logfile = (byte*)NativeMemory.AllocZeroed(LogfileSize);
        try
        {
            _loggerName = Marshal.StringToHGlobalUni(SessionName);
            *(IntPtr*)(logfile + 8) = _loggerName;                                                       // LoggerName
            *(uint*)(logfile + 28) = ProcessTraceModeRealTime | ProcessTraceModeEventRecord | ProcessTraceModeRawTimestamp;
            *(IntPtr*)(logfile + 424) = (IntPtr)(delegate* unmanaged<EventRecord*, void>)&OnEvent;      // EventRecordCallback
            _trace = OpenTraceW(logfile);
        }
        finally
        {
            NativeMemory.Free(logfile);
        }
        if (_trace == InvalidTrace) throw new InvalidOperationException($"Could not open the frame rate session (error {Marshal.GetLastPInvokeError()}).");

        var trace = _trace;
        _thread = new Thread(() =>
        {
            var handle = trace;
            ProcessTrace(&handle, 1, null, null); // blocks until the session stops
        }) { IsBackground = true, Name = "SYSCOM frame rate" };
        _thread.Start();
        _log.LogInformation("Frame rate monitoring started");
    }

    private void StopIfIdle()
    {
        if (_thread is null || Environment.TickCount64 - Volatile.Read(ref _lastPoll) < 30_000) return;
        lock (_sessionGate)
        {
            Stop();
            _log.LogInformation("Frame rate monitoring paused (nobody watching)");
        }
    }

    private void Stop()
    {
        if (_session != 0)
        {
            var props = NewProperties(out _);
            ControlTraceW(_session, null, props, ControlStop);
            Marshal.FreeHGlobal((IntPtr)props);
            _session = 0;
        }
        if (_trace != InvalidTrace)
        {
            CloseTrace(_trace);
            _trace = InvalidTrace;
        }
        _thread?.Join(2000);
        _thread = null;
        if (_loggerName != IntPtr.Zero) { Marshal.FreeHGlobal(_loggerName); _loggerName = IntPtr.Zero; }
        lock (_gate) _incoming.Clear();
        _frames.Clear();
        _lastSeen.Clear();
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(EventRecord* e)
    {
        var self = _active;
        if (self is null) return;
        var id = e->EventId;
        if (!((id == DxgiPresentStart && e->ProviderId == DxgiProvider) || (id == D3d9PresentStart && e->ProviderId == D3d9Provider))) return;
        var pid = (int)e->ProcessId;
        lock (self._gate)
        {
            if (!self._incoming.TryGetValue(pid, out var list)) self._incoming[pid] = list = new List<long>(64);
            list.Add(e->TimeStamp);
        }
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        lock (_sessionGate) Stop();
        if (_active == this) _active = null;
    }

    // ---- native interop (advapi32 ETW API), x64 layouts

    private const uint ErrorAccessDenied = 5;
    private const uint ErrorAlreadyExists = 183;
    private const uint ControlStop = 1;
    private const uint EnableProvider = 1;
    private const byte TraceLevelInformation = 4;
    private const uint ProcessTraceModeRealTime = 0x00000100;
    private const uint ProcessTraceModeRawTimestamp = 0x00001000;
    private const uint ProcessTraceModeEventRecord = 0x10000000;
    private const int PropertiesSize = 120;   // EVENT_TRACE_PROPERTIES
    private const int LogfileSize = 448;      // EVENT_TRACE_LOGFILEW

    /// <summary>EVENT_TRACE_PROPERTIES for a real-time session using QPC timestamps, followed by room for the name.</summary>
    private static byte* NewProperties(out int size)
    {
        size = PropertiesSize + 2 * (SessionName.Length + 1) + 64;
        var p = (byte*)Marshal.AllocHGlobal(size);
        new Span<byte>(p, size).Clear();
        *(uint*)(p + 0) = (uint)size;          // Wnode.BufferSize
        *(uint*)(p + 40) = 1;                  // Wnode.ClientContext: QueryPerformanceCounter timestamps
        *(uint*)(p + 44) = 0x00020000;         // Wnode.Flags: WNODE_FLAG_TRACED_GUID
        *(uint*)(p + 48) = 4;                  // BufferSize (KB): small buffers reach us quickly
        *(uint*)(p + 64) = 0x00000100;         // LogFileMode: EVENT_TRACE_REAL_TIME_MODE
        *(uint*)(p + 68) = 1;                  // FlushTimer (s)
        *(uint*)(p + 116) = PropertiesSize;    // LoggerNameOffset
        return p;
    }

    /// <summary>The EVENT_RECORD header fields used here.</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct EventRecord
    {
        [FieldOffset(12)] public uint ProcessId;
        [FieldOffset(16)] public long TimeStamp;
        [FieldOffset(24)] public Guid ProviderId;
        [FieldOffset(40)] public ushort EventId;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint StartTraceW(out ulong handle, string name, byte* properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint ControlTraceW(ulong handle, string? name, byte* properties, uint controlCode);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint EnableTraceEx2(ulong handle, Guid* provider, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, void* parameters);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern ulong OpenTraceW(byte* logfile);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint ProcessTrace(ulong* handles, uint count, void* startTime, void* endTime);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint CloseTrace(ulong handle);
}
#endif
