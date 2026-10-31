using System.Text.Json;

namespace Syscom.Server.Dashboards;

/// <summary>
/// This PC's dashboard layouts (widgets and sections), kept on the PC so every browser and phone that signs in sees
/// the same ones: "widgets" for desktops, "phone" for phones (null = phones show the desktop layout). The server
/// doesn't interpret the widgets; it only checks the shape and size. Stored in dashboard.json next to config.json.
/// </summary>
public sealed class DashboardStore
{
    public const int MaxBytes = 512 * 1024;
    public const int MaxItems = 500;

    private readonly string _path;
    private readonly object _lock = new();
    private Saved _saved = new(0, 0, null, null);

    private sealed record Saved(int Rev, long Updated, JsonElement? Widgets, JsonElement? Phone);

    public DashboardStore(string dataDir, ILogger<DashboardStore> log)
    {
        _path = Path.Combine(dataDir, "dashboard.json");
        try
        {
            if (File.Exists(_path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(_path));
                var root = doc.RootElement;
                _saved = new Saved(
                    root.TryGetProperty("rev", out var rev) && rev.TryGetInt32(out var r) ? r : 1,
                    root.TryGetProperty("updated", out var up) && up.TryGetInt64(out var u) ? u : 0,
                    root.TryGetProperty("widgets", out var w) && w.ValueKind == JsonValueKind.Array ? w.Clone() : null,
                    root.TryGetProperty("phone", out var ph) && ph.ValueKind == JsonValueKind.Array ? ph.Clone() : null);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not read {File}; starting with an empty dashboard", _path);
        }
    }

    /// <summary>Changes on every save; 0 = nothing saved yet.</summary>
    public int Rev => Volatile.Read(ref _saved).Rev;

    public object Get()
    {
        var s = Volatile.Read(ref _saved);
        return new { rev = s.Rev, updated = s.Updated, widgets = s.Widgets, phone = s.Phone };
    }

    /// <summary>Saves new layouts and returns their revision, or null with a reason when they are not layouts.</summary>
    public (int? Rev, string? Error) Save(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("widgets", out var widgets) || widgets.ValueKind != JsonValueKind.Array)
            return (null, "Expected {\"widgets\": [...], \"phone\": [...] or null}.");
        JsonElement? phone = body.TryGetProperty("phone", out var ph) && ph.ValueKind != JsonValueKind.Null ? ph : null;
        if (phone is { } p && p.ValueKind != JsonValueKind.Array) return (null, "\"phone\" must be a list of widgets or null.");
        foreach (var list in phone is { } pl ? new[] { widgets, pl } : new[] { widgets })
        {
            if (list.GetArrayLength() > MaxItems) return (null, $"At most {MaxItems} widgets per layout.");
            if (list.EnumerateArray().Any(w => w.ValueKind != JsonValueKind.Object)) return (null, "Each widget must be an object.");
        }

        lock (_lock)
        {
            var next = new Saved(_saved.Rev + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), widgets.Clone(), phone?.Clone());
            var json = JsonSerializer.SerializeToUtf8Bytes(new { rev = next.Rev, updated = next.Updated, widgets = next.Widgets, phone = next.Phone });
            if (json.Length > MaxBytes) return (null, "The dashboard is too large.");
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, json);
            File.Move(tmp, _path, overwrite: true);
            Volatile.Write(ref _saved, next);
            return (next.Rev, null);
        }
    }
}
