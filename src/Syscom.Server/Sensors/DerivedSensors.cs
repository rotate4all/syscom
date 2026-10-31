namespace Syscom.Server.Sensors;

/// <summary>
/// Readings worked out from other readings. Today: how full something is, in percent, wherever a device reports
/// "X Used" and "X Total" (for example "GPU Memory Used" out of "GPU Memory Total", which gives VRAM usage on every
/// GPU vendor; LibreHardwareMonitor's own "GPU Memory" load means VRAM on NVIDIA but memory controller activity on AMD).
/// The derived sensor keeps the used reading's name, with type Load and unit %, and its id is the used reading's id
/// plus "/percent", so it stays the same across restarts.
/// </summary>
public sealed class DerivedSensors
{
    private sealed record Ratio(string Id, string UsedId, string TotalId);

    private Ratio[] _ratios = Array.Empty<Ratio>();

    public IReadOnlyList<SensorInfo> Catalog { get; private set; } = Array.Empty<SensorInfo>();

    /// <summary>Finds the used/total pairs in the combined catalog of all sources.</summary>
    public void Rebuild(IReadOnlyList<SensorInfo> catalog)
    {
        var byKey = new Dictionary<string, SensorInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in catalog.Where(IsSize))
            byKey.TryAdd(Key(s, s.Name), s);

        var ratios = new List<Ratio>();
        var infos = new List<SensorInfo>();
        foreach (var used in catalog.Where(IsSize))
        {
            if (!used.Name.EndsWith(" Used", StringComparison.OrdinalIgnoreCase)) continue;
            var totalName = used.Name[..^" Used".Length] + " Total";
            if (!byKey.TryGetValue(Key(used, totalName), out var total) || total.Unit != used.Unit) continue;
            var id = used.Id + "/percent";
            ratios.Add(new Ratio(id, used.Id, total.Id));
            infos.Add(new SensorInfo(id, used.Name, used.Hardware, used.HardwareType, "Load", "%"));
        }
        _ratios = ratios.ToArray();
        Catalog = infos;
    }

    /// <summary>Adds the derived readings to this poll's values.</summary>
    public void Update(Dictionary<string, float> values)
    {
        foreach (var r in _ratios)
        {
            if (values.TryGetValue(r.UsedId, out var used) && values.TryGetValue(r.TotalId, out var total)
                && total > 0 && float.IsFinite(used))
                values[r.Id] = Math.Clamp(used / total * 100f, 0f, 100f);
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
