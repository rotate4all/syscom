using System.Drawing.Drawing2D;
using System.Text.Json;
using Microsoft.Win32;
using Syscom.Shared;

namespace Syscom.Tray;

/// <summary>How the notification-area icon looks.</summary>
internal enum IconStyle
{
    /// <summary>A dark round badge with the gauge in the health colour (the original look).</summary>
    Color,
    /// <summary>No badge: the gauge in the health colour on a transparent background.</summary>
    Transparent,
    /// <summary>One colour matching the taskbar (white on a dark taskbar, black on a light one), like most modern
    /// tray icons; a small coloured dot only when something needs attention.</summary>
    Monochrome,
}

/// <summary>This user's tray app preferences, in %LOCALAPPDATA%\SYSCOM\tray.json.</summary>
internal static class TraySettings
{
    private static readonly string File = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SYSCOM", "tray.json");

    private sealed record Saved(string? IconStyle);

    private static IconStyle? _style;

    /// <summary>Raised after a setting changed, so the tray icon is redrawn.</summary>
    public static event Action? Changed;

    public static IconStyle Style
    {
        get
        {
            if (_style is { } s) return s;
            try
            {
                var saved = JsonSerializer.Deserialize<Saved>(System.IO.File.ReadAllText(File), SyscomPaths.Json);
                _style = Enum.TryParse<IconStyle>(saved?.IconStyle, true, out var parsed) ? parsed : IconStyle.Color;
            }
            catch (Exception)
            {
                _style = IconStyle.Color;
            }
            return _style.Value;
        }
        set
        {
            _style = value;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(File)!);
                System.IO.File.WriteAllText(File, JsonSerializer.Serialize(new Saved(value.ToString()), SyscomPaths.Json));
            }
            catch (Exception)
            {
                // not fatal: the choice lasts until the tray app restarts
            }
            Changed?.Invoke();
        }
    }

    public static readonly (IconStyle Style, string Label)[] Styles =
    {
        (IconStyle.Color, "Colour (default)"),
        (IconStyle.Transparent, "Transparent"),
        (IconStyle.Monochrome, "Transparent grayscale"),
    };
}

/// <summary>Draws the tray/window icon at runtime (no binary assets): a gauge arc tinted by health.</summary>
internal static class Icons
{
    public static Color ColorFor(Health h) => h switch
    {
        Health.Ok => Color.FromArgb(34, 197, 94),
        Health.Warning => Color.FromArgb(245, 158, 11),
        Health.Error => Color.FromArgb(239, 68, 68),
        _ => Color.FromArgb(56, 189, 248),
    };

    /// <summary>The window icon: always the colour badge.</summary>
    public static Icon Status(Health h) => Draw(h, IconStyle.Color, 32);

    /// <summary>The notification-area icon in the chosen style, at the size the taskbar shows it.</summary>
    public static Icon Tray(Health h, IconStyle style) =>
        Draw(h, style, Math.Clamp(SystemInformation.SmallIconSize.Width, 16, 64));

    /// <summary>Windows' "taskbar and Start use light mode" setting (dark when it can't be read).</summary>
    public static bool LightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Icon Draw(Health h, IconStyle style, int size)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var k = size / 32f;   // drawn on a 32 px grid, scaled to the icon size
            var ink = LightTaskbar() ? Color.FromArgb(28, 28, 30) : Color.FromArgb(245, 245, 247);

            Color track, arc, hub;
            float stroke, inset;
            switch (style)
            {
                case IconStyle.Transparent:
                    track = Color.FromArgb(110, 128, 128, 140);
                    arc = ColorFor(h);
                    hub = ink;
                    stroke = 4.5f; inset = 3.5f;
                    break;
                case IconStyle.Monochrome:
                    track = Color.FromArgb(90, ink);
                    arc = ink;
                    hub = ink;
                    stroke = 4.5f; inset = 3.5f;
                    break;
                default:
                    using (var bg = new SolidBrush(Color.FromArgb(14, 17, 22))) g.FillEllipse(bg, 0, 0, size - 1, size - 1);
                    track = Color.FromArgb(60, 70, 85);
                    arc = ColorFor(h);
                    hub = Color.FromArgb(230, 233, 239);
                    stroke = 4f; inset = 6f;
                    break;
            }

            var box = new RectangleF(inset * k, inset * k, (32 - 2 * inset - 1) * k, (32 - 2 * inset - 1) * k);
            using (var t = new Pen(track, stroke * k)) g.DrawArc(t, box, 135, 270);
            using (var a = new Pen(arc, stroke * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(a, box, 135, 190);
            using (var d = new SolidBrush(hub)) g.FillEllipse(d, 13 * k, 13 * k, 6 * k, 6 * k);

            // Grayscale: a small dot in the health colour when something needs attention.
            if (style == IconStyle.Monochrome && h is Health.Warning or Health.Error)
            {
                var r = 11 * k;
                var at = new RectangleF(size - r - 0.5f, size - r - 0.5f, r, r);
                using var cut = new SolidBrush(Color.Transparent);
                g.CompositingMode = CompositingMode.SourceCopy;
                g.FillEllipse(cut, RectangleF.Inflate(at, 1.5f * k, 1.5f * k));
                g.CompositingMode = CompositingMode.SourceOver;
                using var dot = new SolidBrush(ColorFor(h));
                g.FillEllipse(dot, at);
            }
        }
        var handle = bmp.GetHicon();
        using var tmp = Icon.FromHandle(handle);
        var icon = (Icon)tmp.Clone();
        DestroyIcon(handle);
        return icon;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
