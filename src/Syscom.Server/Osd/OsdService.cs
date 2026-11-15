using System.Text.Json;
using Syscom.Server.Config;
using Syscom.Server.Dashboards;
using Syscom.Server.Sensors;
using Syscom.Shared;

namespace Syscom.Server.Osd;

/// <summary>
/// Whether the on-screen display shows, and what the tray app (which draws it, in the signed-in user's session) last
/// reported: its shortcut, the screens and the installed fonts. The shortcut, the dashboard and the API all switch
/// it here; the tray app picks the state up when it asks for the next frame. Shown or hidden survives a restart
/// (osd-state.json next to config.json).
///
/// While it shows and the tray app is drawing it, the sensors are read at the OSD's own refresh rate (a lease on the
/// hub, like a dashboard's). Hidden, or with no tray app asking, it costs nothing.
/// </summary>
public sealed class OsdService : IDisposable
{
    /// <summary>The tray app asks at least every 1.5 s; after this long without a word it counts as not running.</summary>
    public const int TrayTimeoutMs = 6000;

    private readonly SensorHub _hub;
    private readonly DashboardStore _dashboards;
    private readonly ServerConfig _cfg;
    private readonly ILogger _log;
    private readonly string _statePath;
    private readonly object _lock = new();
    private readonly Timer _timer;
    private bool _shown;
    private int _rev = 1;
    private OsdTrayReport? _tray;
    private long _traySeen;          // Environment.TickCount64 of the tray app's last call; 0 = never
    private bool _trayWasRunning;
    private SensorHub.Lease? _lease;

    public OsdService(SensorHub hub, DashboardStore dashboards, ServerConfig cfg, string dataDir, ILogger log)
    {
        _hub = hub;
        _dashboards = dashboards;
        _cfg = cfg;
        _log = log;
        _statePath = Path.Combine(dataDir, "osd-state.json");
        try
        {
            if (File.Exists(_statePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(_statePath));
                _shown = doc.RootElement.TryGetProperty("shown", out var s) && s.ValueKind == JsonValueKind.True;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not read {File}; the on-screen display starts hidden", _statePath);
        }
        _timer = new Timer(_ => Check(), null, 2000, 2000);
    }

    public bool Shown { get { lock (_lock) return _shown; } }

    /// <summary>Changes whenever the OSD is shown or hidden, or what the tray app reports changes.</summary>
    public int Rev => Volatile.Read(ref _rev);

    public bool TrayRunning
    {
        get
        {
            var seen = Volatile.Read(ref _traySeen);
            return seen != 0 && Environment.TickCount64 - seen < TrayTimeoutMs;
        }
    }

    public OsdTrayReport? Tray { get { lock (_lock) return _tray; } }

    /// <summary>Hub leases the OSD holds (0 or 1), so the tray app's client count leaves it out.</summary>
    public int Leases { get { lock (_lock) return _lease is null ? 0 : 1; } }

    public OsdLook Look() => OsdReadings.Look(_dashboards.Osd().Look, _cfg.MinIntervalMs);

    /// <summary>Shows (true), hides (false) or toggles (null) the OSD; returns whether it shows now.</summary>
    public bool SetShown(bool? shown)
    {
        lock (_lock)
        {
            var next = shown ?? !_shown;
            if (next == _shown) return _shown;
            _shown = next;
            Interlocked.Increment(ref _rev);
            try { JsonFile.WriteAtomic(_statePath, JsonSerializer.SerializeToUtf8Bytes(new { shown = _shown })); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Could not save whether the on-screen display shows");
            }
            if (!_shown) DropLease();
            return _shown;
        }
    }

    /// <summary>
    /// The tray app asked for a frame. With readings (it is drawing the OSD): keep the sensors read at the OSD's rate.
    /// </summary>
    public void TrayAsked(bool drawing)
    {
        Volatile.Write(ref _traySeen, Environment.TickCount64);
        if (!_trayWasRunning) Check();
        if (!drawing) return;
        var interval = _hub.ClampInterval(Look().Interval);
        lock (_lock)
        {
            if (!_shown) return;
            if (_lease is null) _lease = _hub.Subscribe(interval);
            else if (_lease.Interval != interval) _lease.SetInterval(interval);
        }
    }

    public void Report(OsdTrayReport report)
    {
        Volatile.Write(ref _traySeen, Environment.TickCount64);
        lock (_lock)
        {
            var same = _tray is not null && _tray.Hotkey == report.Hotkey && _tray.HotkeyRegistered == report.HotkeyRegistered
                && _tray.HotkeyError == report.HotkeyError && _tray.Screens.SequenceEqual(report.Screens)
                && _tray.Fonts.SequenceEqual(report.Fonts);
            _tray = report;
            if (!same) Interlocked.Increment(ref _rev);
        }
        Check();
    }

    /// <summary>Every 2 s: lets go of the sensors when nobody draws the OSD, and notices the tray app coming and going.</summary>
    private void Check()
    {
        var running = TrayRunning;
        lock (_lock)
        {
            if (running != _trayWasRunning)
            {
                _trayWasRunning = running;
                Interlocked.Increment(ref _rev);
            }
            if (_lease is not null && (!_shown || !running)) DropLease();
        }
    }

    private void DropLease()
    {
        _lease?.Dispose();
        _lease = null;
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_lock) DropLease();
    }
}
