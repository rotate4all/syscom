namespace Syscom.Server.Sensors;

/// <summary>Free/used space per fixed drive, from the .NET runtime itself (no extra dependency).</summary>
public sealed class DriveSpaceSource : ISensorSource
{
    private const double GB = 1024d * 1024 * 1024;
    private string _signature = "";
    private long _lastScan;
    private DriveInfo[] _drives = Array.Empty<DriveInfo>();

    public string Name => "Drive space";
    public IReadOnlyList<SensorInfo> Catalog { get; private set; } = Array.Empty<SensorInfo>();

    public bool Update(Dictionary<string, float> values)
    {
        var changed = false;
        var now = Environment.TickCount64;
        if (now - _lastScan > 10_000 || _lastScan == 0)
        {
            _lastScan = now;
            changed = Rescan();
        }

        foreach (var d in _drives)
        {
            try
            {
                if (!d.IsReady) continue;
                var key = Key(d);
                var total = d.TotalSize;
                var free = d.TotalFreeSpace;
                values[$"/drive/{key}/used"] = total > 0 ? (float)(100.0 * (total - free) / total) : 0;
                values[$"/drive/{key}/free"] = (float)(free / GB);
                values[$"/drive/{key}/total"] = (float)(total / GB);
            }
            catch (IOException) { /* drive went away; next rescan drops it */ }
        }
        return changed;
    }

    private bool Rescan()
    {
        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && SafeReady(d) && d.TotalSize > 0)
            .Where(d => OperatingSystem.IsWindows() || !d.Name.StartsWith("/sys") && !d.Name.StartsWith("/proc") && !d.Name.StartsWith("/dev"))
            .ToArray();
        var signature = string.Join('|', drives.Select(d => d.Name));
        _drives = drives;
        if (signature == _signature) return false;
        _signature = signature;

        var catalog = new List<SensorInfo>();
        foreach (var d in drives)
        {
            var key = Key(d);
            var label = d.Name.TrimEnd('\\');
            try { if (!string.IsNullOrWhiteSpace(d.VolumeLabel)) label += $" ({d.VolumeLabel})"; } catch { }
            catalog.Add(new SensorInfo($"/drive/{key}/used", "Used space", label, "Drive", "Load", "%"));
            catalog.Add(new SensorInfo($"/drive/{key}/free", "Free space", label, "Drive", "Data", "GB"));
            catalog.Add(new SensorInfo($"/drive/{key}/total", "Total space", label, "Drive", "Data", "GB"));
        }
        Catalog = catalog;
        return true;
    }

    private static string Key(DriveInfo d)
    {
        var k = d.Name.Trim('\\', '/', ':').Replace('\\', '_').Replace('/', '_').Replace(":", "");
        return string.IsNullOrEmpty(k) ? "root" : k.ToLowerInvariant();
    }

    private static bool SafeReady(DriveInfo d)
    {
        try { return d.IsReady; } catch { return false; }
    }

    public void Dispose() { }
}
