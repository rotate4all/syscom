#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Syscom.Server.Sensors;

/// <summary>
/// Frame rate and frame time of the app drawing the most frames (normally the game), for any GPU vendor, plus its
/// native frame rate when frame generation adds frames in between.
///
/// Reads Windows' own graphics "present" events through a real-time ETW session (providers Microsoft-Windows-DXGI
/// and Microsoft-Windows-D3D9), the same source PresentMon and FrameView use. Nothing is injected into the game, so
/// it is safe with anti-cheat. No extra dependency: the ETW API is called directly. Needs administrator rights,
/// which the SYSCOM service has.
///
/// "Frame rate" counts the frames shown, generated ones included. "Native frame rate" counts the frames the game
/// renders itself, from the same public trace events PresentMon reads:
///  - NVIDIA Reflex "PC Latency Stats" (every game with DLSS Frame Generation ships Reflex): one set of markers per
///    rendered frame. While this provider is enabled the game also sends itself Reflex latency pings, as it does
///    for PresentMon and FrameView.
///  - Intel-PresentMon frame types: Intel XeSS Frame Generation marks each present as rendered or generated, and
///    AMD's driver marks the frames AMD Fluid Motion Frames adds (those never pass through DXGI, so they are added
///    to the shown frame rate).
/// Without any of these (no frame generation, or FSR 3 frame generation in a game without Reflex) both are the same.
///
/// Covers Direct3D 9 to 12 games, and Vulkan/OpenGL games whose driver presents through DXGI. The session runs while
/// the hub polls (always while history is on, else only with clients connected) and stops 30 s after the last poll.
/// </summary>
public sealed unsafe class PresentMonitorSource : ISensorSource
{
    public const string FpsId = "/frames/fps";
    public const string NativeFpsId = "/frames/fps-native";
    public const string FrameTimeId = "/frames/frametime";
    public const string FrameTimeWorstId = "/frames/frametime-worst";

    private const string SessionName = "SYSCOM-FrameRate";
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3d9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private static readonly Guid PclProvider = new("0D216F06-82A6-4D49-BC4F-8F38AE56EFAB");        // NVIDIA PC Latency Stats (Reflex)
    private static readonly Guid FrameTypeProvider = new("ECAA4712-4644-442F-B94C-A32F6CF8A499");  // Intel-PresentMon
    private const ushort DxgiPresentStart = 42;
    private const ushort D3d9PresentStart = 1;
    private const uint PclSimulationStart = 0, PclPresentStart = 4;
    private const ushort PresentFrameTypeInfo = 1, FlipFrameTypeInfo = 2;
    private const byte FrameTypeOriginal = 1, FrameTypeAfmf = 100;
    private const ushort ExtTypeSchemaTl = 11;   // EVENT_HEADER_EXT_TYPE_EVENT_SCHEMA_TL
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
    // Native frames per app, by signal: Reflex simulation starts, Reflex present starts, XeSS-FG rendered presents.
    private Dictionary<(int Pid, Signal Kind), List<long>> _incomingNative = new();
    private List<long> _incomingAfmf = new();                        // frames AMD Fluid Motion Frames added (any app)
    private readonly Dictionary<int, List<long>> _frames = new();   // pid → present timestamps (QPC), last ~1 s
    private readonly Dictionary<(int Pid, Signal Kind), List<long>> _native = new();
    private readonly List<long> _afmf = new();
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

    private enum Signal { ReflexSimulation, ReflexPresent, XefgRendered }

    private static IReadOnlyList<SensorInfo> BuildCatalog(string app) => new[]
    {
        new SensorInfo(FpsId, "Frame rate", app, "Game", "Fps", Units.For("Fps")),
        new SensorInfo(NativeFpsId, "Native frame rate", app, "Game", "Fps", Units.For("Fps")),
        new SensorInfo(FrameTimeId, "Frame time", app, "Game", "Frametime", Units.For("Frametime")),
        new SensorInfo(FrameTimeWorstId, "Frame time (worst)", app, "Game", "Frametime", Units.For("Frametime")),
    };

    public bool Update(Dictionary<string, float> values)
    {
        Volatile.Write(ref _lastPoll, Environment.TickCount64);
        EnsureRunning();

        Dictionary<int, List<long>> batch;
        Dictionary<(int, Signal), List<long>> nativeBatch;
        List<long> afmfBatch;
        lock (_gate)
        {
            batch = _incoming; _incoming = new();
            nativeBatch = _incomingNative; _incomingNative = new();
            afmfBatch = _incomingAfmf; _incomingAfmf = new();
        }
        var tick = Environment.TickCount64;
        foreach (var (pid, stamps) in batch)
        {
            if (!_frames.TryGetValue(pid, out var list)) _frames[pid] = list = new List<long>(256);
            list.AddRange(stamps);
            _lastSeen[pid] = tick;
        }
        foreach (var (key, stamps) in nativeBatch)
        {
            if (!_native.TryGetValue(key, out var list)) _native[key] = list = new List<long>(256);
            list.AddRange(stamps);
        }
        _afmf.AddRange(afmfBatch);

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
        // Native frame rates per app and signal (this also trims every list to its last second).
        var nativeRates = _native.ToDictionary(kv => kv.Key, kv => Rate(kv.Value));
        var afmfRate = Rate(_afmf);
        if (_chosen != 0 && stats.TryGetValue(_chosen, out var s))
        {
            // Native frames: the strongest signal this app gives; frames AFMF adds are shown on top of its presents.
            var native = new[] { Signal.ReflexSimulation, Signal.ReflexPresent, Signal.XefgRendered }
                .Select(k => nativeRates.GetValueOrDefault((_chosen, k)))
                .FirstOrDefault(r => r is not null);
            var added = native is null ? afmfRate ?? 0 : 0;
            values[FpsId] = (float)(s.Fps + added);
            values[NativeFpsId] = (float)(native ?? s.Fps);
            values[FrameTimeId] = (float)s.AvgMs;
            values[FrameTimeWorstId] = (float)s.WorstMs;
        }
        foreach (var key in _native.Keys.Where(k => !_frames.ContainsKey(k.Pid)).ToList()) _native.Remove(key);
        foreach (var pid in _names.Keys.Where(p => !_frames.ContainsKey(p)).ToList()) _names.Remove(pid);
        return changed;
    }

    /// <summary>Frames per second over the last second of a list of timestamps (trimmed); null when there is no recent run.</summary>
    private static double? Rate(List<long> list)
    {
        if (list.Count < 2) { list.Clear(); return null; }
        list.Sort();
        var last = list[^1];
        list.RemoveAll(t => t < last - Stopwatch.Frequency);
        var now = Stopwatch.GetTimestamp();
        if (list.Count < 2 || now - last > 3 * Stopwatch.Frequency) return null;   // stale: the signal stopped
        var span = (last - list[0]) / (double)Stopwatch.Frequency;
        return span > 0 ? (list.Count - 1) / span : null;
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

        foreach (var provider in new[] { DxgiProvider, D3d9Provider, PclProvider, FrameTypeProvider })
        {
            var guid = provider;
            var err = EnableTraceEx2(_session, &guid, EnableProvider, TraceLevelInformation, ulong.MaxValue, 0, 0, null);
            if (err != 0) _log.LogWarning("Could not enable frame events from {Provider} (error {Error})", provider, err);
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
        lock (_gate) { _incoming.Clear(); _incomingNative.Clear(); _incomingAfmf.Clear(); }
        _frames.Clear();
        _native.Clear();
        _afmf.Clear();
        _lastSeen.Clear();
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(EventRecord* e)
    {
        var self = _active;
        if (self is null) return;
        var id = e->EventId;
        var pid = (int)e->ProcessId;
        if ((id == DxgiPresentStart && e->ProviderId == DxgiProvider) || (id == D3d9PresentStart && e->ProviderId == D3d9Provider))
        {
            lock (self._gate)
            {
                if (!self._incoming.TryGetValue(pid, out var list)) self._incoming[pid] = list = new List<long>(64);
                list.Add(e->TimeStamp);
            }
        }
        else if (e->ProviderId == PclProvider)
        {
            // TraceLogging "PCLStatsEvent" (and V2/V3, "ReflexStatsEvent"): UInt32 Marker, UInt64 FrameID, ...
            if (e->UserDataLength < 12 || !IsStatsEvent(e)) return;
            var marker = *(uint*)e->UserData;
            var kind = marker == PclSimulationStart ? Signal.ReflexSimulation : marker == PclPresentStart ? Signal.ReflexPresent : (Signal?)null;
            if (kind is { } k) self.AddNative(pid, k, e->TimeStamp);
        }
        else if (e->ProviderId == FrameTypeProvider)
        {
            // Packed: PresentFrameType_Info { UInt32 FrameId; UInt8 FrameType; ... },
            //         FlipFrameType_Info { UInt32 VidPnSourceId; UInt32 LayerIndex; UInt64 PresentId; UInt8 FrameType; ... }
            if (id == PresentFrameTypeInfo && e->UserDataLength >= 5 && e->UserData[4] == FrameTypeOriginal)
                self.AddNative(pid, Signal.XefgRendered, e->TimeStamp);
            else if (id == FlipFrameTypeInfo && e->UserDataLength >= 17 && e->UserData[16] == FrameTypeAfmf)
                lock (self._gate) self._incomingAfmf.Add(e->TimeStamp);
        }
    }

    private void AddNative(int pid, Signal kind, long stamp)
    {
        lock (_gate)
        {
            if (!_incomingNative.TryGetValue((pid, kind), out var list)) _incomingNative[(pid, kind)] = list = new List<long>(64);
            list.Add(stamp);
        }
    }

    /// <summary>Whether a PC Latency Stats event is a frame marker, by the event name in its TraceLogging schema.</summary>
    private static bool IsStatsEvent(EventRecord* e)
    {
        for (var i = 0; i < e->ExtendedDataCount; i++)
        {
            var item = e->ExtendedData + i;
            if (item->ExtType != ExtTypeSchemaTl) continue;
            return new ReadOnlySpan<byte>((void*)item->DataPtr, item->DataSize).IndexOf("StatsEvent"u8) >= 0;
        }
        return false;
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

    /// <summary>The EVENT_RECORD fields used here (EVENT_HEADER is the first 80 bytes).</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct EventRecord
    {
        [FieldOffset(12)] public uint ProcessId;
        [FieldOffset(16)] public long TimeStamp;
        [FieldOffset(24)] public Guid ProviderId;
        [FieldOffset(40)] public ushort EventId;
        [FieldOffset(84)] public ushort ExtendedDataCount;
        [FieldOffset(86)] public ushort UserDataLength;
        [FieldOffset(88)] public ExtendedDataItem* ExtendedData;
        [FieldOffset(96)] public byte* UserData;
    }

    /// <summary>EVENT_HEADER_EXTENDED_DATA_ITEM.</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct ExtendedDataItem
    {
        [FieldOffset(2)] public ushort ExtType;
        [FieldOffset(6)] public ushort DataSize;
        [FieldOffset(8)] public ulong DataPtr;
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
