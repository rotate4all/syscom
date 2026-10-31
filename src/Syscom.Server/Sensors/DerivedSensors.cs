namespace Syscom.Server.Sensors;

/// <summary>
/// Readings worked out from other readings:
/// <list type="bullet">
/// <item>How full something is, in percent, wherever a device reports "X Used" and "X Total" (for example "GPU Memory
/// Used" out of "GPU Memory Total", which gives VRAM usage on every GPU vendor; LibreHardwareMonitor's own "GPU Memory"
/// load means VRAM on NVIDIA but memory controller activity on AMD). It keeps the used reading's name, with type Load
/// and unit %.</item>
/// <item>For Windows' "D3D Dedicated Memory Used" (the figure Task Manager shows), which comes without a total: a
/// Total (the card's memory size as the driver reports it in "GPU Memory Total"), a Free and the percent.</item>
/// </list>
/// A derived id is the used reading's id plus "/percent", "/total" or "/free", so it stays the same across restarts.
/// </summary>
public sealed class DerivedSensors
{
    private enum Kind { Percent, Total, Free }

    private sealed record Derived(string Id, Kind Kind, string UsedId, string TotalId);

    private Derived[] _derived = Array.Empty<Derived>();

    public IReadOnlyList<SensorInfo> Catalog { get; private set; } = Array.Empty<SensorInfo>();

    /// <summary>Finds the used/total pairs in the combined catalog of all sources.</summary>
    public void Rebuild(IReadOnlyList<SensorInfo> catalog)
    {
        var byKey = new Dictionary<string, SensorInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in catalog.Where(IsSize))
            byKey.TryAdd(Key(s, s.Name), s);

        var derived = new List<Derived>();
        var infos = new List<SensorInfo>();
        foreach (var used in catalog.Where(IsSize))
        {
            if (!used.Name.EndsWith(" Used", StringComparison.OrdinalIgnoreCase)) continue;
            var stem = used.Name[..^" Used".Length];
            if (byKey.TryGetValue(Key(used, stem + " Total"), out var total) && total.Unit == used.Unit)
            {
                Add(Kind.Percent, used.Name, "Load", "%", total);
                continue;
            }

            // Windows' dedicated video memory counter: measured against the card's memory size from the driver.
            if (!stem.Contains("Dedicated", StringComparison.OrdinalIgnoreCase)
                || byKey.ContainsKey(Key(used, stem + " Free"))
                || !byKey.TryGetValue(Key(used, "GPU Memory Total"), out total) || total.Unit != used.Unit)
                continue;
            Add(Kind.Total, stem + " Total", used.Type, used.Unit, total);
            Add(Kind.Free, stem + " Free", used.Type, used.Unit, total);
            Add(Kind.Percent, used.Name, "Load", "%", total);

            void Add(Kind kind, string name, string type, string unit, SensorInfo of)
            {
                var id = $"{used.Id}/{kind.ToString().ToLowerInvariant()}";
                derived.Add(new Derived(id, kind, used.Id, of.Id));
                infos.Add(new SensorInfo(id, name, used.Hardware, used.HardwareType, type, unit));
            }
        }
        _derived = derived.ToArray();
        Catalog = infos;
    }

    /// <summary>Adds the derived readings to this poll's values.</summary>
    public void Update(Dictionary<string, float> values)
    {
        foreach (var d in _derived)
        {
            if (!values.TryGetValue(d.UsedId, out var used) || !values.TryGetValue(d.TotalId, out var total)
                || total <= 0 || !float.IsFinite(used))
                continue;
            values[d.Id] = d.Kind switch
            {
                Kind.Percent => Math.Clamp(used / total * 100f, 0f, 100f),
                Kind.Total => total,
                _ => Math.Max(0f, total - used),
            };
        }
    }

    private static bool IsSize(SensorInfo s) => s.Type is "Data" or "SmallData";

    /// <summary>
    /// Same device = same hardware name and type and the same id prefix (LibreHardwareMonitor ids look like
    /// /gpu-nvidia/0/smalldata/1, so two identical GPUs differ in the prefix even though their names match).
    /// </summary>
    private static string Key(SensorInfo s, string name) => $"{Device(s.Id)}|{s.Hardware}|{s.HardwareType}|{name}";

    private static string Device(string id)
    {
        var cut = id.LastIndexOf('/');
        if (cut > 0) cut = id.LastIndexOf('/', cut - 1);
        return cut > 0 ? id[..cut] : "";
    }
}
