using System.Text.Json;
using System.Text.RegularExpressions;
using Syscom.Server.Api;
using Syscom.Shared;

namespace Syscom.Server.Osd;

/// <summary>
/// The on-screen display as stored in dashboard.json ("osd": readings, "osdLook": settings) turned into what the tray
/// app draws and what the API returns: each reading's label, number and unit formatted the way the dashboard shows
/// them (web/js/osd.js does the same for the dashboard's preview), grouped by where on the screen they go.
/// </summary>
public static partial class OsdReadings
{
    /// <summary>Places on the screen, in the order the groups are listed.</summary>
    public static readonly string[] Positions = ["tl", "tc", "tr", "ml", "mr", "bl", "bc", "br"];

    /// <summary>A reading's size relative to the OSD's font size (the dashboard's value sizes).</summary>
    private static readonly Dictionary<string, double> Sizes = new() { ["xs"] = 0.6, ["s"] = 0.8, ["m"] = 1, ["l"] = 1.35, ["xl"] = 1.75 };

    public const int MaxLabel = 40;

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")] private static partial Regex HexColor();

    private static string? Color(string? c) => c is not null && HexColor().IsMatch(c) ? c.ToLowerInvariant() : null;

    /// <summary>The settings with defaults for whatever is missing or out of range.</summary>
    public static OsdLook Look(JsonElement? stored, int minIntervalMs)
    {
        var d = OsdLook.Default;
        if (stored is not { ValueKind: JsonValueKind.Object } l) return d with { Interval = Math.Max(d.Interval, minIntervalMs) };
        string? Str(string name) => Readings.Str(l, name);
        double? Num(string name) => Readings.Num(l, name);
        int Int(string name, int def, int min, int max) => Num(name) is { } v && double.IsFinite(v) ? (int)Math.Clamp(Math.Round(v), min, max) : def;
        var font = Str("font")?.Trim();
        if (string.IsNullOrEmpty(font) || font.Length > 64 || font.Any(char.IsControl)) font = d.Font;
        var hotkey = l.TryGetProperty("hotkey", out var hk) && hk.ValueKind == JsonValueKind.String
            ? Hotkeys.Parse(hk.GetString(), out _)?.Text ?? d.Hotkey
            : d.Hotkey;
        var screen = Str("screen")?.Trim();
        return new OsdLook(
            font,
            l.TryGetProperty("bold", out var b) && b.ValueKind is JsonValueKind.True or JsonValueKind.False ? b.GetBoolean() : d.Bold,
            Num("size") is { } size && double.IsFinite(size) ? Math.Clamp(Math.Round(size, 1), 8, 72) : d.Size,
            Color(Str("color")) ?? d.Color,
            Color(Str("labelColor")) ?? d.LabelColor,
            Int("transparency", d.Transparency, 0, 90),
            Int("backdrop", d.Backdrop, 0, 100),
            Color(Str("backdropColor")) ?? d.BackdropColor,
            Str("outline") is "outline" or "shadow" or "none" ? Str("outline")! : d.Outline,
            Int("margin", d.Margin, 0, 400),
            string.IsNullOrEmpty(screen) || screen.Length > 64 ? null : screen,
            hotkey,
            Int("interval", d.Interval, Math.Max(500, minIntervalMs), 10_000));
    }

    /// <summary>A reading on the OSD: where it goes, and what it shows now.</summary>
    public sealed record Element(string Id, string Pos, bool Beside, string SensorId, OsdText Text, object Api);

    public static List<Element> Elements(JsonElement? items, Readings.Context ctx)
    {
        var list = new List<Element>();
        if (items is not { ValueKind: JsonValueKind.Array } array) return list;
        var n = 0;
        foreach (var x in array.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Object || Readings.Str(x, "sensor") is not { Length: > 0 } sensorId) continue;
            var id = Readings.Str(x, "id") ?? $"osd{n}";
            n++;
            var pos = Readings.Str(x, "pos") is { } p && Positions.Contains(p) ? p : "tl";
            var beside = Readings.Str(x, "stack") == "beside";
            ctx.ById.TryGetValue(sensorId, out var sensor);
            var unit = Readings.DisplayUnit(x, sensor);
            var raw = Readings.ValueOf(ctx.Snap, sensorId);
            var value = raw is { } r ? Math.Round(r * Readings.UnitFactor(sensor?.Unit, unit), 4) : (double?)null;
            var decimals = Readings.Num(x, "decimals") is { } d ? (int)Math.Clamp(d, 0, 6) : 0;
            var (number, shownUnit) = Readings.FormatParts(value, unit, decimals);
            var hideUnit = x.TryGetProperty("hideUnit", out var hu) && hu.ValueKind == JsonValueKind.True;
            // No label stored: the sensor's name. An empty one: none (a reading beside another, say).
            var label = x.TryGetProperty("label", out var lb) && lb.ValueKind == JsonValueKind.String
                ? Trim(lb.GetString()!)
                : sensor?.Name ?? "Sensor";
            var level = Readings.Level(x, value);
            var color = Color(Readings.LevelColor(x, value)) ?? Color(Readings.Str(x, "color"));
            var scale = Readings.Str(x, "size") is { } size && Sizes.TryGetValue(size, out var k) ? k : 1;
            var text = new OsdText(id, label, number, hideUnit ? "" : shownUnit, color, scale);
            var api = new
            {
                id,
                pos,
                stack = beside ? "beside" : "below",
                label,
                sensor = Readings.SensorRef(sensorId, sensor),
                value,
                unit = shownUnit,
                number,
                text = shownUnit.Length > 0 && number != "–" ? $"{number} {shownUnit}" : number,
                color,
                level,
            };
            list.Add(new Element(id, pos, beside, sensorId, text, api));
        }
        return list;
    }

    /// <summary>
    /// The readings by place on the screen, each place row by row: a reading "beside" the previous one in its place
    /// joins its row, any other starts a new row.
    /// </summary>
    public static List<OsdGroup> Groups(IEnumerable<Element> elements)
    {
        var byPos = new Dictionary<string, List<List<OsdText>>>();
        foreach (var e in elements)
        {
            if (!byPos.TryGetValue(e.Pos, out var rows)) byPos[e.Pos] = rows = new List<List<OsdText>>();
            if (e.Beside && rows.Count > 0) rows[^1].Add(e.Text);
            else rows.Add(new List<OsdText> { e.Text });
        }
        return Positions.Where(byPos.ContainsKey)
            .Select(p => new OsdGroup(p, byPos[p].Select(r => (IReadOnlyList<OsdText>)r).ToList()))
            .ToList();
    }

    /// <summary>The sensors the OSD shows (they are the ones it needs read).</summary>
    public static IEnumerable<string> Sensors(JsonElement? items) =>
        items is { ValueKind: JsonValueKind.Array } a
            ? a.EnumerateArray().Select(x => Readings.Str(x, "sensor")).OfType<string>()
            : [];

    private static string Trim(string s)
    {
        s = s.Trim();
        return s.Length > MaxLabel ? s[..MaxLabel] : s;
    }
}
