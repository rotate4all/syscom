using System.Text.Json;

namespace Syscom.Server.Dashboards;

/// <summary>
/// The list of PCs a dashboard opened from this PC shows: [{ id, name, url, httpsPort }] in page order. Kept here so
/// it survives a browser forgetting it (another address of the same PC, such as localhost instead of the IP, is a new
/// site to the browser and starts empty) and so every browser and phone that opens the dashboard from this PC shares
/// it. Sign-ins are never stored here; each browser keeps its own. Stored in devices.json next to config.json.
/// </summary>
public sealed class DeviceListStore
{
    public const int MaxDevices = 64;

    private readonly string _path;
    private readonly object _lock = new();
    private (int Rev, JsonElement? Devices) _saved = (0, null);

    public DeviceListStore(string dataDir, ILogger<DeviceListStore> log)
    {
        _path = Path.Combine(dataDir, "devices.json");
        try
        {
            if (File.Exists(_path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(_path));
                var root = doc.RootElement;
                _saved = (root.TryGetProperty("rev", out var r) && r.TryGetInt32(out var rev) ? rev : 1,
                    root.TryGetProperty("devices", out var d) && d.ValueKind == JsonValueKind.Array ? d.Clone() : null);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not read {File}; starting with an empty device list", _path);
        }
    }

    /// <summary>Changes on every save; 0 = nothing saved yet.</summary>
    public int Rev { get { lock (_lock) return _saved.Rev; } }

    public object Get()
    {
        lock (_lock) return new { rev = _saved.Rev, devices = _saved.Devices };
    }

    /// <summary>The address saved for a PC, by its server id.</summary>
    public string? UrlOf(string id)
    {
        lock (_lock)
        {
            if (_saved.Devices is not { } list) return null;
            foreach (var d in list.EnumerateArray())
                if (d.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String && i.GetString() == id &&
                    d.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                    return u.GetString();
            return null;
        }
    }

    public (int? Rev, string? Error) Save(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("devices", out var list) || list.ValueKind != JsonValueKind.Array)
            return (null, "Expected {\"devices\": [...]}.");
        if (list.GetArrayLength() > MaxDevices) return (null, $"At most {MaxDevices} devices.");

        // Keep only what identifies and reaches a PC; never tokens or anything else a browser might send.
        var clean = new List<object>();
        foreach (var d in list.EnumerateArray())
        {
            if (d.ValueKind != JsonValueKind.Object) return (null, "Each device must be an object.");
            string? Str(string name, int max) =>
                d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
                    ? s.Length > max ? s[..max] : s
                    : null;
            var url = Str("url", 300);
            if (url is not null && !(Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")))
                return (null, "A device url must be an http(s) address.");
            int? httpsPort = d.TryGetProperty("httpsPort", out var hp) && hp.TryGetInt32(out var port) && port is > 0 and < 65536 ? port : null;
            var id = Str("id", 64);
            if (id is null && url is null) return (null, "Each device needs an id or a url.");
            clean.Add(new { id, name = Str("name", 80), url, httpsPort });
        }

        lock (_lock)
        {
            var rev = _saved.Rev + 1;
            var json = JsonSerializer.SerializeToUtf8Bytes(new { rev, devices = clean });
            JsonFile.WriteAtomic(_path, json);
            using var doc = JsonDocument.Parse(json);
            _saved = (rev, doc.RootElement.GetProperty("devices").Clone());
            return (rev, null);
        }
    }
}
