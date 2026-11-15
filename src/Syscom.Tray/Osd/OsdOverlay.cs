using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Syscom.Shared;

namespace Syscom.Tray.Osd;

/// <summary>
/// The on-screen display: readings drawn over games and everything else on this PC's screen, in the corners and
/// edges the dashboard's OSD view puts them. The service can't draw on the signed-in user's screen (it runs in
/// session 0), so the tray app does: it asks the service for the readings (already formatted), draws each group in
/// its own click-through window, and registers the shortcut that shows and hides it (Ctrl+Shift+S by default).
///
/// Shown or hidden is kept by the service, so the shortcut, the tray menu, the dashboard and the API all switch the
/// same one. Hidden, the tray app only asks every 1.5 s whether that changed (no sensors are read for it).
///
/// Games in exclusive full screen own the screen and nothing else can draw over them; borderless / windowed full
/// screen (most games today, and what Windows' full-screen optimisations turn exclusive full screen into) shows it.
/// </summary>
internal sealed class OsdOverlay : IDisposable
{
    private const int HiddenPollMs = 1500;
    private const int OfflinePollMs = 5000;
    private const int ReportEveryMs = 30_000;

    private readonly AdminClient _client;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private readonly HotkeyWindow _hotkey;
    private readonly Dictionary<string, OsdWindow> _windows = new();
    private readonly Dictionary<string, (string Shape, List<float> Widths)> _numberWidths = new();
    private readonly CancellationTokenSource _stop = new();
    private OsdFrame? _frame;
    private bool _busy;
    private bool _again;
    private bool _disposed;
    private string _reported = "";
    private long _reportedAt;
    private IReadOnlyList<string>? _fonts;

    /// <summary>Raised when the OSD was shown or hidden, or its shortcut changed (the tray menu shows both).</summary>
    public event Action? Changed;

    public bool Shown => _frame?.Shown == true;

    /// <summary>The shortcut as set in the dashboard; "" = none.</summary>
    public string Hotkey => _hotkey.Text;

    public bool HotkeyWorks => _hotkey.Registered;

    public OsdOverlay(AdminClient client)
    {
        _client = client;
        _hotkey = new HotkeyWindow(() => _ = ToggleAsync(null));
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.InstalledFontsChanged += OnFontsChanged;
    }

    /// <summary>Shows, hides (or with null toggles) the OSD; it changes on screen right away.</summary>
    public async Task ToggleAsync(bool? shown)
    {
        try
        {
            await _client.SetOsdShownAsync(shown);
        }
        catch (Exception)
        {
            return;   // SYSCOM isn't running: nothing to show
        }
        await TickAsync();
    }

    private async Task TickAsync()
    {
        if (_disposed) return;
        if (_busy) { _again = true; return; }   // a request is on its way: ask again right after it
        _again = false;
        _busy = true;
        _timer.Stop();
        var next = HiddenPollMs;
        try
        {
            var before = _frame;
            var frame = await _client.OsdAsync(values: before?.Shown == true, _stop.Token);
            // Just shown: ask again, with the readings.
            if (frame is { Shown: true, Groups: null }) frame = await _client.OsdAsync(values: true, _stop.Token);
            if (frame is null) throw new InvalidOperationException("no answer");
            _frame = frame;
            _hotkey.Set(frame.Look.Hotkey);
            Draw(frame);
            if (before?.Shown != frame.Shown || before?.Look.Hotkey != frame.Look.Hotkey) Changed?.Invoke();
            await ReportAsync(frame.Look.Hotkey);
            next = frame.Shown ? Math.Max(250, frame.Look.Interval) : HiddenPollMs;
        }
        catch (Exception)
        {
            // SYSCOM restarting or not running (or the tray app closing): nothing to draw until it's back.
            if (!_disposed)
            {
                var was = _frame;
                _frame = null;
                HideAll();
                if (was is not null) Changed?.Invoke();
            }
            next = OfflinePollMs;
        }
        finally
        {
            _busy = false;
            if (!_disposed)
            {
                _timer.Interval = _again ? 1 : next;
                _timer.Start();
            }
        }
    }

    private void Draw(OsdFrame frame)
    {
        if (!frame.Shown || frame.Groups is null)
        {
            HideAll();
            return;
        }
        var look = frame.Look;
        var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName.Equals(look.Screen, StringComparison.OrdinalIgnoreCase))
            ?? Screen.PrimaryScreen ?? Screen.AllScreens[0];
        var scale = Dpi.ScaleOf(screen);
        var bounds = screen.Bounds;   // the whole screen: full-screen games cover the taskbar
        var margin = (int)Math.Round(look.Margin * scale);
        var opacity = (byte)Math.Round(255 * (100 - Math.Clamp(look.Transparency, 0, 90)) / 100.0);
        var used = new HashSet<string>();
        foreach (var group in frame.Groups)
        {
            if (group.Rows.Count == 0) continue;
            used.Add(group.Pos);
            // A reading gaining a digit widens its column for good (until the layout changes), so the group stays put.
            var shape = $"{look.Font}|{look.Size}|{look.Bold}|{scale}|" + string.Join(";", group.Rows.Select(r => string.Join(",", r.Select(t => t.Id))));
            if (!_numberWidths.TryGetValue(group.Pos, out var memory) || memory.Shape != shape)
                _numberWidths[group.Pos] = memory = (shape, new List<float>());
            using var picture = OsdRenderer.Render(group, look, scale, memory.Widths);
            if (!_windows.TryGetValue(group.Pos, out var window)) _windows[group.Pos] = window = new OsdWindow();
            window.Present(picture, Place(group.Pos, bounds, margin, picture.Size), opacity);
        }
        foreach (var (pos, window) in _windows)
            if (!used.Contains(pos)) window.Hide();
    }

    /// <summary>Where a group goes: tl top left, tc top center, tr top right, ml / mr middle, bl / bc / br bottom.</summary>
    private static Point Place(string pos, Rectangle screen, int margin, Size size)
    {
        var x = pos[1] switch
        {
            'l' => screen.Left + margin,
            'r' => screen.Right - margin - size.Width,
            _ => screen.Left + (screen.Width - size.Width) / 2,
        };
        var y = pos[0] switch
        {
            't' => screen.Top + margin,
            'b' => screen.Bottom - margin - size.Height,
            _ => screen.Top + (screen.Height - size.Height) / 2,
        };
        return new Point(x, y);
    }

    private void HideAll()
    {
        foreach (var w in _windows.Values) w.Hide();
    }

    /// <summary>Tells SYSCOM whether the shortcut works and which screens and fonts there are (when that changes,
    /// and now and then, since SYSCOM forgets it when it restarts).</summary>
    private async Task ReportAsync(string hotkey)
    {
        var screens = Screen.AllScreens.Select((s, i) => new OsdScreen(s.DeviceName, $"Screen {i + 1}", s.Bounds.Width, s.Bounds.Height,
            Math.Round(Dpi.ScaleOf(s), 2), s.Primary)).ToList();
        _fonts ??= InstalledFonts();
        var key = $"{hotkey}|{_hotkey.Registered}|{_hotkey.Error}|{string.Join(";", screens)}|{_fonts.Count}";
        if (key == _reported && Environment.TickCount64 - _reportedAt < ReportEveryMs) return;
        await _client.ReportOsdAsync(new OsdTrayReport(_hotkey.Text, _hotkey.Registered, _hotkey.Error, screens, _fonts));
        _reported = key;
        _reportedAt = Environment.TickCount64;
    }

    private static List<string> InstalledFonts()
    {
        try
        {
            using var fonts = new InstalledFontCollection();
            return fonts.Families.Select(f => f.Name).Where(n => !n.StartsWith('@')).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        _reported = "";
        _numberWidths.Clear();
        if (_frame is not null) Draw(_frame);
    }

    private void OnFontsChanged(object? sender, EventArgs e)
    {
        _fonts = null;
        _reported = "";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.InstalledFontsChanged -= OnFontsChanged;
        _timer.Dispose();
        _hotkey.Dispose();
        foreach (var w in _windows.Values) w.Dispose();
        _windows.Clear();
    }
}

/// <summary>The OSD's shortcut, registered with Windows for this session (RegisterHotKey), on a message-only window.</summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int Id = 0x5359;   // "SY"
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
    private const int RetryMs = 30_000;
    private readonly Action _pressed;
    private long _triedAt;

    public HotkeyWindow(Action pressed)
    {
        _pressed = pressed;
        CreateHandle(new CreateParams { Caption = "SYSCOM shortcut", Parent = new IntPtr(-3) });   // HWND_MESSAGE
    }

    /// <summary>The shortcut asked for ("" = none).</summary>
    public string Text { get; private set; } = "";
    public bool Registered { get; private set; }
    /// <summary>Why it isn't registered; null when it is (or none was asked for).</summary>
    public string? Error { get; private set; }

    public void Set(string text)
    {
        // Taken by another app: try again now and then, in case that app let go of it.
        var retry = Error is not null && Environment.TickCount64 - _triedAt > RetryMs;
        if (text == Text && (Registered || text.Length == 0 || (Error is not null && !retry))) return;
        _triedAt = Environment.TickCount64;
        if (Registered) UnregisterHotKey(Handle, Id);
        Registered = false;
        Error = null;
        Text = text;
        if (text.Length == 0) return;
        if (Hotkeys.Parse(text, out var why) is not { } key)
        {
            Error = why;
            return;
        }
        var mods = (uint)key.Mods | MOD_NOREPEAT;   // the enum's values are Windows' MOD_ALT, MOD_CONTROL, MOD_SHIFT, MOD_WIN
        if (RegisterHotKey(Handle, Id, mods, (uint)key.Key)) Registered = true;
        else Error = Marshal.GetLastWin32Error() == ERROR_HOTKEY_ALREADY_REGISTERED
            ? $"Another app on this PC already uses {key.Text}."
            : $"Windows didn't accept {key.Text} as a shortcut.";
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam == Id) _pressed();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Registered) UnregisterHotKey(Handle, Id);
        Registered = false;
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}

/// <summary>Windows' display scaling per screen (the tray app is per-monitor DPI aware).</summary>
internal static class Dpi
{
    public static double ScaleOf(Screen screen)
    {
        try
        {
            var center = new POINT { x = screen.Bounds.Left + screen.Bounds.Width / 2, y = screen.Bounds.Top + screen.Bounds.Height / 2 };
            var monitor = MonitorFromPoint(center, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out var dpiX, out _) == 0 && dpiX > 0) return dpiX / 96.0;
        }
        catch (Exception) { /* older Windows: assume 100 % */ }
        return 1;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
