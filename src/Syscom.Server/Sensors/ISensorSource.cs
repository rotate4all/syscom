namespace Syscom.Server.Sensors;

/// <summary>Describes one sensor. <see cref="Id"/> is stable across restarts for the same hardware.</summary>
public sealed record SensorInfo(
    string Id,
    string Name,
    string Hardware,
    string HardwareType,
    string Type,
    string Unit);

/// <summary>A provider of sensor readings. <see cref="Update"/> is only ever called from the hub's polling thread.</summary>
public interface ISensorSource : IDisposable
{
    string Name { get; }

    /// <summary>Current sensor list. Replaced (never mutated) when hardware changes.</summary>
    IReadOnlyList<SensorInfo> Catalog { get; }

    /// <summary>Refreshes readings and writes them into <paramref name="values"/>. Returns true if the catalog changed.</summary>
    bool Update(Dictionary<string, float> values);
}

public static class Units
{
    /// <summary>Unit labels for LibreHardwareMonitor sensor types (matched by name so new enum members never break the build).</summary>
    public static string For(string sensorType) => sensorType switch
    {
        "Voltage" => "V",
        "Current" => "A",
        "Power" => "W",
        "Clock" => "MHz",
        "Temperature" => "°C",
        "Load" or "Control" or "Level" or "Humidity" => "%",
        "Frequency" => "Hz",
        "Fan" => "RPM",
        "Flow" => "L/h",
        "Data" => "GB",
        "SmallData" => "MB",
        "Throughput" => "B/s",
        "TimeSpan" => "s",
        "Energy" => "mWh",
        "Noise" => "dBA",
        "Fps" => "FPS",
        _ => "",
    };
}
