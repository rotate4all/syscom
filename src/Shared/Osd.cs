// The on-screen display (OSD): what the server sends the tray app to draw over games, what the tray app reports back,
// and the keyboard shortcut format both understand. Shared between the server and the tray app.
namespace Syscom.Shared;

/// <summary>
/// The OSD's settings, as the dashboard's OSD view saves them (dashboard.json "osdLook"), with defaults filled in.
/// Sizes are in pixels at 100 % display scaling; the tray app scales them with the screen the OSD is on.
/// </summary>
/// <param name="Font">Font family installed on the PC.</param>
/// <param name="Size">Font size of the readings, 8 to 72 px.</param>
/// <param name="Color">Colour of the numbers (a reading's own colour or threshold colour replaces it), #rrggbb.</param>
/// <param name="LabelColor">Colour of the labels and units, #rrggbb.</param>
/// <param name="Transparency">0 (solid) to 90 %, for everything the OSD draws.</param>
/// <param name="Backdrop">Opacity of the plate behind each group of readings, 0 (none) to 100 %.</param>
/// <param name="Outline">"outline", "shadow" or "none": what keeps the text readable over bright scenes.</param>
/// <param name="Margin">Distance from the screen's edges, 0 to 400 px.</param>
/// <param name="Screen">Which screen (its Windows device name, e.g. \\.\DISPLAY2); null = the main screen.</param>
/// <param name="Hotkey">Shortcut that shows and hides the OSD, e.g. "Ctrl+Shift+S"; "" = none.</param>
/// <param name="Interval">How often the OSD's readings refresh, in ms.</param>
public sealed record OsdLook(
    string Font,
    bool Bold,
    double Size,
    string Color,
    string LabelColor,
    int Transparency,
    int Backdrop,
    string BackdropColor,
    string Outline,
    int Margin,
    string? Screen,
    string Hotkey,
    int Interval)
{
    public const string DefaultHotkey = "Ctrl+Shift+S";

    public static readonly OsdLook Default = new("Segoe UI", true, 18, "#ffffff", "#9cc3ff", 0, 40, "#000000", "outline", 16,
        null, DefaultHotkey, 1000);
}

/// <summary>One reading on the OSD: its label, number and unit, already formatted the way the dashboard shows it.</summary>
/// <param name="Color">The number's colour (the reading's own, or the threshold it reached); null = the OSD's.</param>
/// <param name="Scale">Size relative to the OSD's font size.</param>
public sealed record OsdText(string Id, string Label, string Number, string Unit, string? Color, double Scale);

/// <summary>The readings in one place on the screen ("tl" top left … "br" bottom right), row by row.</summary>
public sealed record OsdGroup(string Pos, IReadOnlyList<IReadOnlyList<OsdText>> Rows);

/// <summary>What the tray app draws. Groups is null when it asked without readings (the OSD is hidden).</summary>
public sealed record OsdFrame(int Rev, bool Shown, OsdLook Look, long T, IReadOnlyList<OsdGroup>? Groups);

/// <summary>A screen of the PC, as the tray app sees it.</summary>
/// <param name="Id">Windows' device name (\\.\DISPLAY1).</param>
/// <param name="Width">In pixels.</param>
/// <param name="Scale">Windows' display scaling for it (1.5 = 150 %).</param>
public sealed record OsdScreen(string Id, string Name, int Width, int Height, double Scale, bool Primary);

/// <summary>What the tray app tells the server: whether the shortcut works, the screens and the installed fonts.</summary>
/// <param name="HotkeyError">Why the shortcut couldn't be set up (usually: another app uses it); null = it works.</param>
public sealed record OsdTrayReport(string Hotkey, bool HotkeyRegistered, string? HotkeyError, IReadOnlyList<OsdScreen> Screens,
    IReadOnlyList<string> Fonts);

/// <summary>Shows (true), hides (false) or toggles (null) the OSD.</summary>
public sealed record OsdShownRequest(bool? Shown);

/// <summary>
/// Keyboard shortcuts as text: modifiers (Ctrl, Alt, Shift, Win) and one key, joined with "+", e.g. "Ctrl+Shift+S",
/// "Alt+F10", "Ctrl+Num 0". The dashboard writes them, the server checks them and the tray app registers them with
/// Windows (RegisterHotKey).
/// </summary>
public static class Hotkeys
{
    [Flags]
    public enum Mods { None = 0, Alt = 1, Ctrl = 2, Shift = 4, Win = 8 }

    /// <summary>Key names and their Windows virtual-key codes.</summary>
    public static readonly IReadOnlyDictionary<string, int> Keys = BuildKeys();

    private static Dictionary<string, int> BuildKeys()
    {
        var k = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var c = 'A'; c <= 'Z'; c++) k[c.ToString()] = c;
        for (var c = '0'; c <= '9'; c++) k[c.ToString()] = c;
        for (var i = 1; i <= 24; i++) k[$"F{i}"] = 0x70 + i - 1;
        for (var i = 0; i <= 9; i++) k[$"Num {i}"] = 0x60 + i;
        k["Num *"] = 0x6A; k["Num +"] = 0x6B; k["Num -"] = 0x6D; k["Num ."] = 0x6E; k["Num /"] = 0x6F;
        k["Space"] = 0x20; k["PageUp"] = 0x21; k["PageDown"] = 0x22; k["End"] = 0x23; k["Home"] = 0x24;
        k["Left"] = 0x25; k["Up"] = 0x26; k["Right"] = 0x27; k["Down"] = 0x28; k["Insert"] = 0x2D; k["Delete"] = 0x2E;
        k["Pause"] = 0x13; k["ScrollLock"] = 0x91;
        k[";"] = 0xBA; k["="] = 0xBB; k[","] = 0xBC; k["-"] = 0xBD; k["."] = 0xBE; k["/"] = 0xBF; k["`"] = 0xC0;
        k["["] = 0xDB; k["\\"] = 0xDC; k["]"] = 0xDD; k["'"] = 0xDE;
        return k;
    }

    /// <summary>Keys that are fine on their own; any other key needs Ctrl, Alt or Win, so typing isn't swallowed.</summary>
    private static bool StandsAlone(string key) =>
        key.Length > 1 && (key.StartsWith('F') || key.StartsWith("Num ", StringComparison.Ordinal)
            || key is "Pause" or "ScrollLock" or "Insert" or "Home" or "End" or "PageUp" or "PageDown");

    /// <summary>
    /// Reads a shortcut: (modifiers, virtual key, the shortcut written the standard way). Null when it isn't one,
    /// with the reason. "" is a valid answer for "no shortcut" (virtual key 0).
    /// </summary>
    public static (Mods Mods, int Key, string Text)? Parse(string? text, out string? error)
    {
        error = null;
        text = (text ?? "").Trim();
        if (text.Length == 0) return (Mods.None, 0, "");
        var mods = Mods.None;
        string? key = null;
        // The keypad's plus is the one key name with a "+" in it.
        var parts = text.Replace("Num +", "Num Plus", StringComparison.OrdinalIgnoreCase).Split('+');
        foreach (var raw in parts)
        {
            var p = raw.Trim();
            if (p.Length == 0) continue;
            switch (p.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= Mods.Ctrl; continue;
                case "alt": mods |= Mods.Alt; continue;
                case "shift": mods |= Mods.Shift; continue;
                case "win" or "windows" or "meta": mods |= Mods.Win; continue;
            }
            if (key is not null) { error = "A shortcut has one key besides Ctrl, Alt, Shift and Win."; return null; }
            if (p.Equals("Num Plus", StringComparison.OrdinalIgnoreCase)) p = "Num +";
            var name = Keys.Keys.FirstOrDefault(k => k.Equals(p, StringComparison.OrdinalIgnoreCase));
            if (name is null) { error = $"\"{p}\" is not a key SYSCOM can use for a shortcut."; return null; }
            key = name;
        }
        if (key is null) { error = "Add a key to the modifiers, e.g. Ctrl+Shift+S."; return null; }
        if ((mods & (Mods.Ctrl | Mods.Alt | Mods.Win)) == 0 && !StandsAlone(key))
        {
            error = $"{key} on its own (or with Shift) would get in the way of typing: add Ctrl or Alt.";
            return null;
        }
        var standard = string.Join("+", new[]
        {
            (mods & Mods.Ctrl) != 0 ? "Ctrl" : null, (mods & Mods.Alt) != 0 ? "Alt" : null,
            (mods & Mods.Shift) != 0 ? "Shift" : null, (mods & Mods.Win) != 0 ? "Win" : null, key,
        }.Where(x => x is not null));
        return (mods, Keys[key], standard);
    }
}
