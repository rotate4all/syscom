#if LHM
using System.Diagnostics;
using LibreHardwareMonitor.Hardware;

namespace Syscom.Server.Sensors;

/// <summary>
/// Reads CPU, GPU (NVIDIA/AMD/Intel), memory, motherboard (Super I/O fans/voltages), storage (SMART, usage,
/// throughput), network, battery and PSU sensors through LibreHardwareMonitorLib.
/// Low-level sensors (CPU MSR temps, Super I/O) need administrator rights and the PawnIO driver.
/// </summary>
public sealed class LhmSensorSource : ISensorSource
{
    private readonly Computer _computer;
    private readonly ILogger _log;
    private volatile bool _dirty = true;
    private long _rebuiltAt;
    private bool _gpuPowerChecked;
    private long _startedAt;
    private bool _opened;
    private Exception? _openError;
    private List<(ISensor Sensor, string Id)> _sensors = new();

    // Graphics cards being switched off (a driver install) aren't read: see GpuStateWatch.
    private readonly GpuStateWatch? _gpuWatch = GpuStateWatch.TryStart();
    private Dictionary<string, bool> _gpusAtOpen = new();
    private long _gpuChangesAtOpen;
    private bool _gpusOpened;
    private bool _gpusPaused;
    private string _gpuState = "";
    private long _gpuStateSince;

    // LibreHardwareMonitor adds some sensors only once it has read them (NVIDIA's "GPU Package" power, PCIe and D3D
    // readings, some drive and Super I/O sensors), and drops sensors that stop reporting. Catalog changes are sent to
    // every browser, so after the first one they're batched to at most one per few seconds.
    private static readonly TimeSpan RebuildEvery = TimeSpan.FromSeconds(3);
    // A card that's back is opened again once nothing has changed for this long (its driver has finished starting),
    private static readonly TimeSpan GpuSettle = TimeSpan.FromSeconds(3);
    // and the others are opened again without one that stays off this long (disabled, or its driver removed).
    private static readonly TimeSpan GpuGiveUp = TimeSpan.FromSeconds(20);

    public LhmSensorSource(ILogger log)
    {
        _log = log;
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = false, // opened on the first read, once SYSCOM can answer Windows about them (GpuStateWatch)
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
            IsNetworkEnabled = true,
            IsBatteryEnabled = true,
            IsPsuEnabled = true,
            IsControllerEnabled = false, // fan controllers / AIO pumps: not needed for monitoring-only
        };
        _computer.HardwareAdded += hw => { Watch(hw); _dirty = true; };
        _computer.HardwareRemoved += hw => { Unwatch(hw); _dirty = true; };
    }

    public string Name => "LibreHardwareMonitor";
    public IReadOnlyList<SensorInfo> Catalog { get; private set; } = Array.Empty<SensorInfo>();

    public bool Update(Dictionary<string, float> values)
    {
        // Finding the hardware (every drive's health data included) can take more than half a minute while Windows
        // starts, so it happens on the first read, on the sensor loop, not while the service is starting: Windows
        // gives a service 30 s to report that it started, and doesn't try again if it doesn't.
        if (!_opened)
        {
            _opened = true;
            _gpuChangesAtOpen = _gpuWatch?.Changes ?? 0;
            var cardsOpen = _gpuWatch?.CardsOpen ?? 0;
            try { _computer.Open(); }
            catch (Exception ex) { _openError = ex; }
            _gpusAtOpen = GpuStateWatch.Adapters();
            _startedAt = Stopwatch.GetTimestamp();
            if (_gpuWatch is not null && _gpusAtOpen.Count > 0)
                _log.LogInformation(cardsOpen > 0
                    ? "Graphics cards: Windows asks SYSCOM before switching one off (driver installs), and waits for its readings to finish"
                    : "Graphics cards: checked before every reading for a card being switched off (driver installs). Run as administrator so Windows also asks SYSCOM first");
        }
        if (_openError is not null) throw new InvalidOperationException(_openError.Message, _openError);

        foreach (var hw in _computer.Hardware)
            if (!IsGpu(hw)) UpdateRecursive(hw);
        // The graphics cards last, right after checking that none of them is being switched off, and while holding the
        // watch's read gate: Windows waits for that before it removes a card.
        var gpusReadable = GpusReadable(out var reopen);
        reopen |= !_gpusOpened;
        if (gpusReadable && (_gpuWatch?.TryBeginRead() ?? true))
        {
            try
            {
                if (reopen) OpenGpus();
                foreach (var hw in _computer.Hardware)
                    if (IsGpu(hw)) UpdateRecursive(hw);
            }
            finally { _gpuWatch?.EndRead(); }
        }
        else gpusReadable = false;

        var changed = false;
        if (_dirty && (_rebuiltAt == 0 || Stopwatch.GetElapsedTime(_rebuiltAt) >= RebuildEvery))
        {
            _dirty = false;
            _rebuiltAt = Stopwatch.GetTimestamp();
            changed = RebuildCatalog();
        }
        CheckGpuPower();

        foreach (var (sensor, id) in _sensors)
        {
            if (!gpusReadable && IsGpu(sensor.Hardware)) continue; // no stale readings while a card is off
            if (sensor.Value is { } v && float.IsFinite(v)) values[id] = v;
        }
        return changed;
    }

    private static void UpdateRecursive(IHardware hw)
    {
        hw.Update();
        foreach (var sub in hw.SubHardware) UpdateRecursive(sub);
    }

    private static bool IsGpu(IHardware hw) => hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    /// <summary>
    /// Whether the graphics cards may be read now: every card that was running when they were opened still runs, and
    /// none went off, came back or was asked for since. Otherwise their readings pause, and once the cards are back
    /// (or a card stays off), <paramref name="reopen"/> says to open them again, so the vendors' libraries start over
    /// with the new driver.
    /// </summary>
    private bool GpusReadable(out bool reopen)
    {
        reopen = false;
        if (_gpuWatch is null) return true;
        var adapters = GpuStateWatch.Adapters();
        var changes = _gpuWatch.Changes;
        var allBack = _gpusAtOpen.All(a => !a.Value || adapters.GetValueOrDefault(a.Key));
        if (!_gpusPaused)
        {
            if (allBack && changes == _gpuChangesAtOpen) return true;
            _gpusPaused = true;
            _log.LogInformation(_gpuWatch.RemovalAsked
                ? "Windows is switching a graphics card off (it asked SYSCOM first, as a graphics driver install does). " +
                  "The graphics card readings pause until it's back"
                : "A graphics card is being switched off or restarted (as a graphics driver install does). The graphics " +
                  "card readings pause until it's back, so its driver's libraries aren't called meanwhile");
        }

        // Wait until nothing has changed for a while: the cards' states and Windows' reports of them.
        var state = $"{changes};{string.Join(';', adapters.Select(a => $"{a.Key}={a.Value}"))}";
        if (state != _gpuState)
        {
            _gpuState = state;
            _gpuStateSince = Stopwatch.GetTimestamp();
            return false;
        }
        var quiet = Stopwatch.GetElapsedTime(_gpuStateSince);
        if (!(allBack ? quiet >= GpuSettle : quiet >= GpuGiveUp)) return false;

        _gpuWatch.Resume(); // watch the cards anew, and allow reads
        _gpusAtOpen = adapters;
        _gpuChangesAtOpen = _gpuWatch.Changes;
        _gpusPaused = false;
        reopen = true;
        return true;
    }

    /// <summary>
    /// Finds the graphics cards in LibreHardwareMonitor, closing them first if they were open, so the vendors'
    /// libraries start over (with a new driver, after an install).
    /// </summary>
    private void OpenGpus()
    {
        var again = _gpusOpened;
        _gpusOpened = true;
        try
        {
            _computer.IsGpuEnabled = false;
            _computer.IsGpuEnabled = true;
            if (again) _log.LogInformation("Reading the graphics cards again");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "The graphics cards could not be opened");
        }
        _rebuiltAt = 0; // list the cards' sensors now
    }

    private void Watch(IHardware hw)
    {
        hw.SensorAdded += OnSensorsChanged;
        hw.SensorRemoved += OnSensorsChanged;
        foreach (var sub in hw.SubHardware) Watch(sub);
    }

    private void Unwatch(IHardware hw)
    {
        hw.SensorAdded -= OnSensorsChanged;
        hw.SensorRemoved -= OnSensorsChanged;
        foreach (var sub in hw.SubHardware) Unwatch(sub);
    }

    private void OnSensorsChanged(ISensor _) => _dirty = true;

    /// <summary>Re-reads the sensor list. Returns whether it differs from the last one.</summary>
    private bool RebuildCatalog()
    {
        var sensors = new List<(ISensor, string)>();
        var catalog = new List<SensorInfo>();
        foreach (var hw in _computer.Hardware) Collect(hw, sensors, catalog);
        _sensors = sensors;
        if (catalog.SequenceEqual(Catalog)) return false;
        var added = catalog.Count - Catalog.Count;
        if (Catalog.Count == 0)
            _log.LogInformation("LibreHardwareMonitor: {Count} sensors on {Hw} devices", catalog.Count, _computer.Hardware.Count);
        else
            _log.LogInformation("LibreHardwareMonitor: now {Count} sensors ({Change:+#;-#;0})", catalog.Count, added);
        Catalog = catalog;
        return true;
    }

    /// <summary>
    /// Says in the log, once, when a graphics card still reports no power draw a little after startup (late sensors
    /// have shown up by then). Helps answer "where is my GPU power?".
    /// </summary>
    private void CheckGpuPower()
    {
        if (_gpuPowerChecked || Stopwatch.GetElapsedTime(_startedAt) < TimeSpan.FromSeconds(15)) return;
        _gpuPowerChecked = true;
        foreach (var gpu in Catalog.Where(s => s.HardwareType.StartsWith("Gpu", StringComparison.Ordinal))
                     .GroupBy(s => s.Hardware)
                     .Where(g => !g.Any(s => s.Type == "Power")))
            _log.LogWarning("LibreHardwareMonitor reports no power draw (W) for {Gpu}", gpu.Key);
    }

    private static void Collect(IHardware hw, List<(ISensor, string)> sensors, List<SensorInfo> catalog)
    {
        foreach (var s in hw.Sensors)
        {
            var id = s.Identifier.ToString();
            var type = s.SensorType.ToString();
            sensors.Add((s, id));
            catalog.Add(new SensorInfo(id, s.Name, hw.Name, hw.HardwareType.ToString(), type, Units.For(type)));
        }
        foreach (var sub in hw.SubHardware) Collect(sub, sensors, catalog);
    }

    public void Dispose()
    {
        _gpuWatch?.Dispose();
        if (_opened) _computer.Close();
    }
}
#endif
