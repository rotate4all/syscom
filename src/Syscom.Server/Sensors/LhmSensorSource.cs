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
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private List<(ISensor Sensor, string Id)> _sensors = new();

    // LibreHardwareMonitor adds some sensors only once it has read them (NVIDIA's "GPU Package" power, PCIe and D3D
    // readings, some drive and Super I/O sensors), and drops sensors that stop reporting. Catalog changes are sent to
    // every browser, so after the first one they're batched to at most one per few seconds.
    private static readonly TimeSpan RebuildEvery = TimeSpan.FromSeconds(3);

    public LhmSensorSource(ILogger log)
    {
        _log = log;
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
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
        _computer.Open();
    }

    public string Name => "LibreHardwareMonitor";
    public IReadOnlyList<SensorInfo> Catalog { get; private set; } = Array.Empty<SensorInfo>();

    public bool Update(Dictionary<string, float> values)
    {
        foreach (var hw in _computer.Hardware) UpdateRecursive(hw);

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
            if (sensor.Value is { } v && float.IsFinite(v)) values[id] = v;
        }
        return changed;
    }

    private static void UpdateRecursive(IHardware hw)
    {
        hw.Update();
        foreach (var sub in hw.SubHardware) UpdateRecursive(sub);
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

    public void Dispose() => _computer.Close();
}
#endif
