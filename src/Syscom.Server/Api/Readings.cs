using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Syscom.Server.Sensors;

namespace Syscom.Server.Api;

/// <summary>
/// What the API returns: sensors with their live values, and the dashboard's cards and sections with the readings
/// they show, worked out the way the dashboard does it (web/js/widgets.js): the unit a card shows, its scale, which
/// threshold colour it is at, its extra readings.
/// </summary>
public static partial class Readings
{
    /// <summary>Which part of the PC a sensor belongs to (the dashboard's sensor picker tabs).</summary>
    public static readonly (string Id, string Name, string[] Aliases)[] Categories =
    [
        ("cpu", "CPU", []),
        ("gpu", "GPU", []),
        ("game", "Game", ["frames"]),
        ("memory", "Memory", ["mem", "ram"]),
        ("storage", "Drives", ["disk", "drive", "drives"]),
        ("network", "Network", ["net"]),
        ("motherboard", "Motherboard", ["board"]),
        ("other", "Other", []),
    ];

    /// <summary>What kind of reading it is (the pills under the picker tabs).</summary>
    public static readonly (string Id, string Name, string[] Aliases)[] Kinds =
    [
        ("temperature", "Temperature", ["temp"]),
        ("load", "Load", []),
        ("clock", "Clock", []),
        ("power", "Power", []),
        ("size", "Memory & space", ["data"]),
        ("fan", "Fans", ["fans"]),
        ("throughput", "Data rate", ["rate"]),
        ("fps", "Frames", ["frames"]),
        ("other", "Other", []),
    ];

    public static string CategoryOf(SensorInfo s) => s.HardwareType switch
    {
        "Cpu" => "cpu",
        var t when t.StartsWith("Gpu", StringComparison.Ordinal) => "gpu",
        "Game" => "game",
        "Memory" => "memory",
        "Storage" or "Drive" => "storage",
        "Network" => "network",
        "Motherboard" or "SuperIO" or "EmbeddedController" or "Cooler" or "Psu" or "Battery" => "motherboard",
        _ => "other",
    };

    public static string KindOf(SensorInfo s)
    {
        // How full memory or a drive is sits with the sizes; NVIDIA's power as a share of its limit is power.
        if (s.Unit == "%" && Fullness().IsMatch(s.Name)) return "size";
        if (s.Type == "Load" && PowerWord().IsMatch(s.Name)) return "power";
        return s.Type switch
        {
            "Temperature" => "temperature",
            "Load" or "Level" or "Factor" => "load",
            "Clock" or "Frequency" => "clock",
            "Power" or "Voltage" or "Current" or "Energy" => "power",
            "Data" or "SmallData" => "size",
            "Fan" or "Control" or "Flow" => "fan",
            "Throughput" => "throughput",
            "Fps" or "Frametime" => "fps",
            _ => "other",
        };
    }

    [GeneratedRegex(@"\bused\b|\bspace\b", RegexOptions.IgnoreCase)] private static partial Regex Fullness();
    [GeneratedRegex(@"\bpower\b", RegexOptions.IgnoreCase)] private static partial Regex PowerWord();

    /// <summary>A category or kind id from a query (its aliases too); null = not one of them.</summary>
    public static string? Canonical((string Id, string Name, string[] Aliases)[] list, string value) =>
        list.FirstOrDefault(c => c.Id.Equals(value, StringComparison.OrdinalIgnoreCase)
            || c.Aliases.Any(a => a.Equals(value, StringComparison.OrdinalIgnoreCase))).Id;

    /// <summary>Filters for /api/sensors and /api/values. Every one is optional; together they all have to match.</summary>
    public sealed record Filter(string? Category, string? Kind, string? Hardware, string? Type, string? Search, string[]? Ids)
    {
        public static (Filter? Filter, string? Error) From(HttpRequest r)
        {
            string? Q(string name) => r.Query[name].ToString() is { Length: > 0 } v ? v : null;
            var category = Q("category");
            var kind = Q("kind");
            if (category is not null && Canonical(Categories, category) is not { } c)
                return (null, $"Unknown category \"{category}\". One of: {string.Join(", ", Categories.Select(x => x.Id))}.");
            if (kind is not null && Canonical(Kinds, kind) is not { } k)
                return (null, $"Unknown kind \"{kind}\". One of: {string.Join(", ", Kinds.Select(x => x.Id))}.");
            var ids = Q("ids")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return (new Filter(category is null ? null : Canonical(Categories, category), kind is null ? null : Canonical(Kinds, kind),
                Q("hardware"), Q("type"), Q("q"), ids), null);
        }

        public IEnumerable<SensorInfo> Apply(IEnumerable<SensorInfo> catalog)
        {
            var ids = Ids is null ? null : new HashSet<string>(Ids, StringComparer.Ordinal);
            return catalog.Where(s =>
                (ids is null || ids.Contains(s.Id))
                && (Category is null || CategoryOf(s) == Category)
                && (Kind is null || KindOf(s) == Kind)
                && (Type is null || s.Type.Equals(Type, StringComparison.OrdinalIgnoreCase))
                && (Hardware is null || s.Hardware.Contains(Hardware, StringComparison.OrdinalIgnoreCase))
                && (Search is null || s.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
                    || s.Hardware.Contains(Search, StringComparison.OrdinalIgnoreCase) || s.Id.Contains(Search, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public static double? ValueOf(Snapshot snap, string id) =>
        snap.Values.TryGetValue(id, out var v) && float.IsFinite(v) ? Math.Round(v, 2) : null;

    public static object Sensor(SensorInfo s, Snapshot snap) => new
    {
        id = s.Id,
        name = s.Name,
        hardware = s.Hardware,
        hardwareType = s.HardwareType,
        type = s.Type,
        unit = s.Unit,
        category = CategoryOf(s),
        kind = KindOf(s),
        value = ValueOf(snap, s.Id),
    };

    public static object CategoryList(IReadOnlyList<SensorInfo> catalog) => Categories
        .Select(c => (c, sensors: catalog.Where(s => CategoryOf(s) == c.Id).ToList()))
        .Where(x => x.sensors.Count > 0)
        .Select(x => new
        {
            id = x.c.Id,
            name = x.c.Name,
            count = x.sensors.Count,
            hardware = x.sensors.Select(s => s.Hardware).Distinct().ToList(),
            kinds = Kinds.Select(k => new { id = k.Id, name = k.Name, count = x.sensors.Count(s => KindOf(s) == k.Id) })
                .Where(k => k.count > 0).ToList(),
        })
        .ToList();

    // ---------------------------------------------------------------- the dashboard's cards

    /// <summary>One layout's items: the desktop one, the phone one (falls back to desktop) or a saved one (by id or name).</summary>
    public static (JsonElement? Items, string Name, string? Error) Layout(JsonElement? desktop, JsonElement? phone, JsonElement? saved, string? layout)
    {
        layout = string.IsNullOrWhiteSpace(layout) ? "desktop" : layout.Trim();
        if (layout.Equals("desktop", StringComparison.OrdinalIgnoreCase)) return (desktop, "desktop", null);
        if (layout.Equals("phone", StringComparison.OrdinalIgnoreCase)) return (phone ?? desktop, "phone", null);
        if (saved is { ValueKind: JsonValueKind.Array } list)
        {
            foreach (var s in list.EnumerateArray())
            {
                if (Str(s, "id") == layout || string.Equals(Str(s, "name"), layout, StringComparison.OrdinalIgnoreCase))
                    return (s.TryGetProperty("items", out var items) ? items : null, Str(s, "name") ?? layout, null);
            }
        }
        return (null, layout, $"No layout \"{layout}\". Use desktop, phone, or the id or name of a saved layout.");
    }

    /// <summary>Cards grouped by section, in dashboard order; cards before the first section have section null.</summary>
    public static List<(JsonElement? Section, List<JsonElement> Cards)> Groups(JsonElement? items)
    {
        var groups = new List<(JsonElement? Section, List<JsonElement> Cards)> { (null, new List<JsonElement>()) };
        if (items is not { ValueKind: JsonValueKind.Array } list) return groups;
        foreach (var x in list.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Object) continue;
            if (Str(x, "kind") == "section") groups.Add((x, new List<JsonElement>()));
            else groups[^1].Cards.Add(x);
        }
        return groups;
    }

    public sealed class Context(IReadOnlyList<SensorInfo> catalog, Snapshot snap)
    {
        public IReadOnlyList<SensorInfo> Catalog { get; } = catalog;
        public Snapshot Snap { get; } = snap;
        public Dictionary<string, SensorInfo> ById { get; } = catalog.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
    }

    public static object Section(JsonElement? section, List<JsonElement> cards, Context ctx) => new
    {
        id = section is { } s ? Str(s, "id") : null,
        title = section is { } t ? Str(t, "title") ?? "Section" : null,
        widgets = cards.Select(c => Widget(c, section is { } sec ? Str(sec, "id") : null, ctx)).ToList(),
    };

    public static object Widget(JsonElement w, string? sectionId, Context ctx)
    {
        var sensorId = Str(w, "sensor") ?? "";
        ctx.ById.TryGetValue(sensorId, out var sensor);
        var unit = DisplayUnit(w, sensor);
        var factor = UnitFactor(sensor?.Unit, unit);
        var raw = ValueOf(ctx.Snap, sensorId);
        var value = raw is { } r ? Math.Round(r * factor, 4) : (double?)null;
        var decimals = Num(w, "decimals") is { } d ? (int)Math.Clamp(d, 0, 6) : 0;

        // Scale: the card's own maximum, or a live total (e.g. VRAM used out of VRAM total), or automatic (null).
        double? max = Num(w, "max");
        if (max is null && MaxFrom(w, sensor, ctx.Catalog) is { Count: > 0 } from)
        {
            double? sum = 0;
            foreach (var id in from) sum = ValueOf(ctx.Snap, id) is { } v && sum is { } acc ? acc + v : null;
            max = sum is { } total ? Math.Round(total * factor, 4) : null;
        }

        return new
        {
            id = Str(w, "id"),
            section = sectionId,
            type = Str(w, "type") ?? "value",
            label = NonEmpty(Str(w, "label")) ?? sensor?.Name ?? "Sensor",
            subtitle = NonEmpty(Str(w, "sub")) ?? sensor?.Hardware,
            interval = Num(w, "interval") is { } iv and >= 500 and <= 60_000 ? (int)iv : (int?)null,
            sensor = SensorRef(sensorId, sensor),
            value,
            unit,
            text = Format(value, unit, decimals),
            min = Num(w, "min") ?? 0,
            max,
            level = Level(w, value),
            extra = Extras(w, sensor, ctx),
        };
    }

    private static object SensorRef(string id, SensorInfo? s) => s is null
        ? new { id, name = (string?)null, hardware = (string?)null, unit = (string?)null, available = false }
        : new { id, name = (string?)s.Name, hardware = (string?)s.Hardware, unit = (string?)s.Unit, available = true };

    private static List<object> Extras(JsonElement w, SensorInfo? main, Context ctx)
    {
        var list = new List<object>();
        if (!w.TryGetProperty("extra", out var extra) || extra.ValueKind != JsonValueKind.Array) return list;
        foreach (var x in extra.EnumerateArray().Take(2))
        {
            if (x.ValueKind != JsonValueKind.Object || Str(x, "sensor") is not { } id) continue;
            ctx.ById.TryGetValue(id, out var s);
            var unit = DisplayUnit(x, s);
            var raw = ValueOf(ctx.Snap, id);
            var value = raw is { } r ? Math.Round(r * UnitFactor(s?.Unit, unit), 4) : (double?)null;
            // The dashboard names an extra reading only when it is told to, or when it has the main reading's unit.
            var label = x.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()
                : s is not null && main is not null && s.Unit == main.Unit ? s.Name : "";
            list.Add(new { sensor = SensorRef(id, s), label, value, unit, text = ReadingText(value, unit) });
        }
        return list;
    }

    /// <summary>The highest threshold the value has reached (the card's colour); null = below them all, or none set.</summary>
    private static object? Level(JsonElement w, double? v)
    {
        if (v is null || !w.TryGetProperty("thresholds", out var ts) || ts.ValueKind != JsonValueKind.Array) return null;
        object? reached = null;
        foreach (var (t, i) in ts.EnumerateArray().Select((t, i) => (t, i))
                     .Where(x => x.t.ValueKind == JsonValueKind.Object && Num(x.t, "value") is not null)
                     .OrderBy(x => Num(x.t, "value")))
        {
            if (v >= Num(t, "value")) reached = new { step = new[] { "min", "med", "max" }.ElementAtOrDefault(i) ?? "max", at = Num(t, "value"), color = Str(t, "color") };
        }
        return reached;
    }

    // ---- units, the way widgets.js converts and formats them

    private static readonly Dictionary<string, double> SizeUnits = new()
    {
        ["B"] = 1, ["KB"] = 1L << 10, ["MB"] = 1L << 20, ["GB"] = 1L << 30, ["TB"] = 1L << 40,
    };

    private static readonly Dictionary<string, double> RateUnits = new()
    {
        ["B/s"] = 1, ["KB/s"] = 1L << 10, ["MB/s"] = 1L << 20, ["GB/s"] = 1L << 30, ["Mbit/s"] = 125_000,
    };

    private static string DisplayUnit(JsonElement w, SensorInfo? s)
    {
        var native = s?.Unit ?? "";
        var chosen = Str(w, "unit");
        if (chosen is null) return native;
        var ok = SizeUnits.ContainsKey(native) ? SizeUnits.ContainsKey(chosen)
            : native == "B/s" && RateUnits.ContainsKey(chosen);
        return ok ? chosen : native;
    }

    private static double UnitFactor(string? from, string to)
    {
        if (from is null) return 1;
        if (SizeUnits.TryGetValue(from, out var a) && SizeUnits.TryGetValue(to, out var b)) return a / b;
        if (RateUnits.TryGetValue(from, out var c) && RateUnits.TryGetValue(to, out var d)) return c / d;
        return 1;
    }

    /// <summary>Like the dashboard shows it: "37 %", "7.4 GB", data rates scaled to fit ("12.5 MB/s").</summary>
    public static string Format(double? v, string unit, int decimals)
    {
        if (v is not { } x) return "–";
        string n;
        if (unit == "B/s")
        {
            string[] steps = ["B/s", "KB/s", "MB/s", "GB/s"];
            var i = 0;
            while (Math.Abs(x) >= 1024 && i < steps.Length - 1) { x /= 1024; i++; }
            n = x.ToString(i == 0 ? "F0" : "F1", CultureInfo.InvariantCulture);
            unit = steps[i];
        }
        else n = x.ToString("F" + decimals, CultureInfo.InvariantCulture);
        return unit.Length > 0 ? $"{n} {unit}" : n;
    }

    private static string ReadingText(double? v, string unit)
    {
        string[] whole = ["%", "°C", "RPM", "MHz", "Hz", "FPS", "B", "KB", "MB", "dBA", "mWh", "L/h"];
        var decimals = unit == "V" ? 3 : whole.Contains(unit) || Math.Abs(v ?? 0) >= 100 ? 0 : 1;
        return Format(v, unit, decimals);
    }

    // ---- the live scale maximum (widgets.js effectiveMaxFrom / totalFor)

    private static List<string>? MaxFrom(JsonElement w, SensorInfo? s, IReadOnlyList<SensorInfo> catalog)
    {
        if (w.TryGetProperty("maxFrom", out var mf))
        {
            if (mf.ValueKind != JsonValueKind.Array) return null;
            var ids = mf.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
            return ids.Count > 0 ? ids : null;
        }
        return TotalFor(s, catalog);
    }

    private static List<string>? TotalFor(SensorInfo? s, IReadOnlyList<SensorInfo> catalog)
    {
        if (s is null || s.Type is not ("Data" or "SmallData")) return null;
        var siblings = catalog.Where(x => x.Hardware == s.Hardware && x.Unit == s.Unit && x.Id != s.Id).ToList();
        SensorInfo? Named(string n) => siblings.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        string Swap(string from, string to) => new Regex(from, RegexOptions.IgnoreCase).Replace(s.Name, to, 1);
        if (Regex.IsMatch(s.Name, "used", RegexOptions.IgnoreCase))
        {
            if (Named(Swap("used", "Total")) is { } total) return [total.Id];
            if ((Named(Swap("used", "Free")) ?? Named(Swap("used", "Available"))) is { } free) return [s.Id, free.Id];
        }
        foreach (var word in new[] { "free", "available" })
        {
            if (!Regex.IsMatch(s.Name, word, RegexOptions.IgnoreCase)) continue;
            if (Named(Swap(word, "Total")) is { } total) return [total.Id];
            if (Named(Swap(word, "Used")) is { } used) return [s.Id, used.Id];
        }
        if (Regex.IsMatch(s.Name, "used|free|available", RegexOptions.IgnoreCase) && !Regex.IsMatch(s.Name, "shared|virtual", RegexOptions.IgnoreCase))
        {
            var total = siblings.FirstOrDefault(x => Regex.IsMatch(x.Name, "total", RegexOptions.IgnoreCase)
                && Regex.IsMatch(x.Name, "memory", RegexOptions.IgnoreCase) && !Regex.IsMatch(x.Name, "shared|virtual", RegexOptions.IgnoreCase));
            if (total is not null) return [total.Id];
        }
        return null;
    }

    // ---- JSON helpers

    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
