#if LHM
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
    private List<(ISensor Sensor, string Id)> _sensors = new();

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
        _computer.HardwareAdded += _ => _dirty = true;
        _computer.HardwareRemoved += _ => _dirty = true;
        _computer.Open();
    }

    public string Name => "LibreHardwareMonitor";
    public IReadOnlyList<SensorInfo> Catalog { get; private set; } = Array.Empty<SensorInfo>();

    public bool Update(Dictionary<string, float> values)
    {
        foreach (var hw in _computer.Hardware) UpdateRecursive(hw);

        var changed = false;
        if (_dirty)
        {
            _dirty = false;
            RebuildCatalog();
            changed = true;
        }

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

    private void RebuildCatalog()
    {
        var sensors = new List<(ISensor, string)>();
        var catalog = new List<SensorInfo>();
        foreach (var hw in _computer.Hardware) Collect(hw, sensors, catalog);
        _sensors = sensors;
        Catalog = catalog;
        _log.LogInformation("LibreHardwareMonitor: {Count} sensors on {Hw} devices", catalog.Count, _computer.Hardware.Count);
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
