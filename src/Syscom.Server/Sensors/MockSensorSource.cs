namespace Syscom.Server.Sensors;

/// <summary>Synthetic readings for developing the web client on machines without real sensors (run with --mock).</summary>
public sealed class MockSensorSource : ISensorSource
{
    private readonly Random _rng = new();
    private readonly Dictionary<string, float> _state = new();
    private bool _first = true;

    private static readonly (string Id, string Name, string Hw, string HwType, string Type, float Min, float Max)[] Defs =
    {
        ("/mock/cpu/load/0", "CPU Total", "Mock CPU 8-Core", "Cpu", "Load", 2, 100),
        ("/mock/cpu/temperature/0", "CPU Package", "Mock CPU 8-Core", "Cpu", "Temperature", 35, 95),
        ("/mock/cpu/clock/0", "Core Average", "Mock CPU 8-Core", "Cpu", "Clock", 800, 5200),
        ("/mock/cpu/power/0", "Package", "Mock CPU 8-Core", "Cpu", "Power", 10, 180),
        ("/mock/gpu/load/0", "GPU Core", "Mock GPU 16GB", "GpuNvidia", "Load", 0, 100),
        ("/mock/gpu/temperature/0", "GPU Core", "Mock GPU 16GB", "GpuNvidia", "Temperature", 30, 88),
        ("/mock/gpu/clock/0", "GPU Core", "Mock GPU 16GB", "GpuNvidia", "Clock", 300, 2800),
        ("/mock/gpu/fan/0", "GPU Fan 1", "Mock GPU 16GB", "GpuNvidia", "Fan", 0, 3000),
        ("/mock/gpu/control/0", "GPU Fan 1", "Mock GPU 16GB", "GpuNvidia", "Control", 0, 100),
        ("/mock/gpu/power/0", "GPU Package", "Mock GPU 16GB", "GpuNvidia", "Power", 15, 250),
        ("/mock/gpu/smalldata/2", "D3D Dedicated Memory Used", "Mock GPU 16GB", "GpuNvidia", "SmallData", 500, 9000),
        ("/mock/gpu/smalldata/3", "D3D Shared Memory Used", "Mock GPU 16GB", "GpuNvidia", "SmallData", 50, 400),
        ("/mock/gpu/smalldata/0", "GPU Memory Used", "Mock GPU 16GB", "GpuNvidia", "SmallData", 500, 9000),
        ("/mock/gpu/smalldata/1", "GPU Memory Total", "Mock GPU 16GB", "GpuNvidia", "SmallData", 16376, 16376),
        ("/frames/fps", "Frame rate", "MockGame", "Game", "Fps", 50, 165),
        ("/frames/fps-native", "Native frame rate", "MockGame", "Game", "Fps", 25, 83),
        ("/frames/frametime", "Frame time", "MockGame", "Game", "Frametime", 6, 20),
        ("/frames/frametime-worst", "Frame time (worst)", "MockGame", "Game", "Frametime", 8, 40),
        ("/mock/ram/load/0", "Memory", "Mock Memory", "Memory", "Load", 20, 95),
        ("/mock/ram/data/0", "Memory Used", "Mock Memory", "Memory", "Data", 4, 20),
        ("/mock/ram/data/1", "Memory Available", "Mock Memory", "Memory", "Data", 12, 12),
        ("/mock/nvme/temperature/0", "Temperature", "Mock NVMe SSD", "Storage", "Temperature", 30, 70),
        ("/mock/nvme/load/1", "Total Activity", "Mock NVMe SSD", "Storage", "Load", 0, 100),
        ("/mock/net/throughput/0", "Download Speed", "Mock Ethernet", "Network", "Throughput", 0, 12_000_000),
        ("/mock/net/throughput/1", "Upload Speed", "Mock Ethernet", "Network", "Throughput", 0, 3_000_000),
    };

    public string Name => "Mock";
    public IReadOnlyList<SensorInfo> Catalog { get; } =
        Defs.Select(d => new SensorInfo(d.Id, d.Name, d.Hw, d.HwType, d.Type, Units.For(d.Type))).ToList();

    public bool Update(Dictionary<string, float> values)
    {
        foreach (var d in Defs)
        {
            var span = d.Max - d.Min;
            var cur = _state.TryGetValue(d.Id, out var c) ? c : d.Min + span * 0.4f;
            // Random walk that drifts back toward the middle of the range.
            cur += (float)(_rng.NextDouble() - 0.5) * span * 0.12f + (d.Min + span * 0.45f - cur) * 0.05f;
            cur = Math.Clamp(cur, d.Min, d.Max);
            _state[d.Id] = cur;
            values[d.Id] = cur;
        }
        // Keep related readings consistent: 32 GB of RAM, frame time matching the frame rate.
        values["/mock/ram/data/1"] = 32 - values["/mock/ram/data/0"];
        values["/frames/frametime"] = 1000 / values["/frames/fps"];
        values["/frames/fps-native"] = values["/frames/fps"] / 2;   // as with frame generation adding one frame per rendered one
        values["/mock/gpu/control/0"] = values["/mock/gpu/fan/0"] / 30;   // fan % matching its RPM
        values["/frames/frametime-worst"] = Math.Max(values["/frames/frametime-worst"], values["/frames/frametime"] * 1.3f);
        var changed = _first;
        _first = false;
        return changed;
    }

    public void Dispose() { }
}
