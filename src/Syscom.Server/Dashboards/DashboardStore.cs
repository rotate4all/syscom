using System.Text.Json;

namespace Syscom.Server.Dashboards;

/// <summary>
/// This PC's dashboard, kept on the PC so every browser and phone that signs in sees the same one:
/// <list type="bullet">
/// <item>"widgets": the desktop layout (cards and sections); "phone": the phone layout (null = phones show the desktop one)</item>
/// <item>"look": background, card opacity, corner rounding, animations (null = defaults)</item>
/// <item>"saved": named layouts to switch between, [{ id, name, mode, items: [...] }]</item>
/// <item>"current": which saved layout each of the two layouts was last loaded from or saved as</item>
/// </list>
/// The server doesn't interpret the contents; it only checks the shape and size. Stored in dashboard.json next to
/// config.json.
/// </summary>
public sealed class DashboardStore
{
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int MaxItems = 500;
    public const int MaxSaved = 50;

    private readonly string _path;
    private readonly object _lock = new();
    private Saved _saved = new(0, 0, null, null, null, null, null);

    private sealed record Saved(int Rev, long Updated, JsonElement? Widgets, JsonElement? Phone, JsonElement? Look,
        JsonElement? SavedLayouts, JsonElement? Current);

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
                    Prop(root, "widgets", JsonValueKind.Array),
                    Prop(root, "phone", JsonValueKind.Array),
                    Prop(root, "look", JsonValueKind.Object),
                    Prop(root, "saved", JsonValueKind.Array),
                    Prop(root, "current", JsonValueKind.Object));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not read {File}; starting with an empty dashboard", _path);
        }
    }

    private static JsonElement? Prop(JsonElement root, string name, JsonValueKind kind) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == kind ? e.Clone() : null;

    /// <summary>Changes on every save; 0 = nothing saved yet.</summary>
    public int Rev => Volatile.Read(ref _saved).Rev;

    public object Get()
    {
        var s = Volatile.Read(ref _saved);
        return new { rev = s.Rev, updated = s.Updated, widgets = s.Widgets, phone = s.Phone, look = s.Look, saved = s.SavedLayouts, current = s.Current };
    }

    /// <summary>The stored layouts as they are: desktop, phone (null = same as desktop) and the saved ones.</summary>
    public (JsonElement? Desktop, JsonElement? Phone, JsonElement? Saved) Layouts()
    {
        var s = Volatile.Read(ref _saved);
        return (s.Widgets, s.Phone, s.SavedLayouts);
    }

    /// <summary>Saves a new dashboard and returns its revision, or null with a reason when it is not one.</summary>
    public (int? Rev, string? Error) Save(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("widgets", out var widgets) || widgets.ValueKind != JsonValueKind.Array)
            return (null, "Expected {\"widgets\": [...], \"phone\": [...] or null}.");
        if (Optional(body, "phone", JsonValueKind.Array, out var phone) is { } e1) return (null, e1);
        if (Optional(body, "look", JsonValueKind.Object, out var look) is { } e2) return (null, e2);
        if (Optional(body, "saved", JsonValueKind.Array, out var saved) is { } e3) return (null, e3);
        if (Optional(body, "current", JsonValueKind.Object, out var current) is { } e4) return (null, e4);

        var lists = new List<JsonElement> { widgets };
        if (phone is { } pl) lists.Add(pl);
        if (saved is { } sl)
        {
            if (sl.GetArrayLength() > MaxSaved) return (null, $"At most {MaxSaved} saved layouts.");
            foreach (var entry in sl.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    return (null, "Each saved layout must be {\"id\", \"name\", \"items\": [...]}.");
                lists.Add(items);
            }
        }
        foreach (var list in lists)
        {
            if (list.GetArrayLength() > MaxItems) return (null, $"At most {MaxItems} widgets per layout.");
            if (list.EnumerateArray().Any(w => w.ValueKind != JsonValueKind.Object)) return (null, "Each widget must be an object.");
        }

        lock (_lock)
        {
            var next = new Saved(_saved.Rev + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), widgets.Clone(), phone?.Clone(),
                look?.Clone(), saved?.Clone(), current?.Clone());
            var json = JsonSerializer.SerializeToUtf8Bytes(new
            {
                rev = next.Rev, updated = next.Updated, widgets = next.Widgets, phone = next.Phone,
                look = next.Look, saved = next.SavedLayouts, current = next.Current,
            });
            if (json.Length > MaxBytes) return (null, "The dashboard is too large.");
            JsonFile.WriteAtomic(_path, json);
            Volatile.Write(ref _saved, next);
            return (next.Rev, null);
        }
    }

    /// <summary>An optional property: missing or null is fine, otherwise it must be of the given kind.</summary>
    private static string? Optional(JsonElement body, string name, JsonValueKind kind, out JsonElement? value)
    {
        value = null;
        if (!body.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null) return null;
        if (e.ValueKind != kind) return $"\"{name}\" must be {(kind == JsonValueKind.Array ? "a list" : "an object")} or null.";
        value = e;
        return null;
    }
}

internal static class JsonFile
{
    /// <summary>Writes through a temporary file, so a crash never leaves half a file behind.</summary>
    public static void WriteAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }
}
