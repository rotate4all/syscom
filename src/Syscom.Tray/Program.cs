using System.Diagnostics;
using Syscom.Shared;

namespace Syscom.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var single = new Mutex(true, @"Global\SYSCOM.Tray", out var first);
        if (!first) return; // already running in this session

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext(openWindow: args.Contains("--open")));
    }
}

/// <summary>Tray icon whose colour reflects overall health; menu for the dashboard, management window and restart.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly AdminClient _client = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 10_000 };
    private MainForm? _form;
    private string _notifiedUpdates = "";
    private Health _shown = (Health)(-1);

    public TrayContext(bool openWindow)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open dashboard", null, (_, _) => OpenDashboard());
        menu.Items.Add("Set up phone…", null, async (_, _) => await Try(async () => { await PhoneSetupForm.ShowAsync(_client); return ""; }));
        menu.Items.Add("Manage SYSCOM…", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Check for updates", null, async (_, _) => { ShowWindow(); await _form!.CheckForUpdatesAsync(); });
        menu.Items.Add("Restart server", null, async (_, _) => await Try(() => _client.RestartAsync()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit tray app", null, (_, _) => ExitThread());

        _icon = new NotifyIcon { Text = "SYSCOM", ContextMenuStrip = menu, Visible = true, Icon = Icons.Status(Health.Unknown) };
        _icon.DoubleClick += (_, _) => ShowWindow();
        _icon.BalloonTipClicked += (_, _) => ShowWindow();

        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
        if (openWindow) ShowWindow();
    }

    private void OpenDashboard() => Process.Start(new ProcessStartInfo(_client.DashboardUrl) { UseShellExecute = true });

    private void ShowWindow()
    {
        if (_form is null || _form.IsDisposed) _form = new MainForm(_client);
        _form.Show();
        _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    private async Task RefreshAsync()
    {
        Health overall;
        string tip;
        try
        {
            var status = await _client.StatusAsync();
            var comps = await _client.ComponentsAsync();
            overall = comps.Select(c => c.Health).DefaultIfEmpty(Health.Ok).Max();
            var updates = comps.Where(c => c.UpdateAvailable && c.CanUpdate).Select(c => $"{c.Name} {c.Latest}").ToList();
            tip = $"SYSCOM · {status?.Clients ?? 0} client(s)" + (updates.Count > 0 ? $" · {updates.Count} update(s)" : "");

            var key = string.Join("|", updates);
            if (updates.Count > 0 && key != _notifiedUpdates)
            {
                _notifiedUpdates = key;
                _icon.ShowBalloonTip(8000, "SYSCOM updates available", string.Join("\n", updates), ToolTipIcon.Info);
            }
        }
        catch (Exception)
        {
            overall = Health.Error;
            tip = AdminClient.ServiceState() is { } state ? $"SYSCOM service {state.ToLowerInvariant()}" : "SYSCOM service not installed";
        }

        if (overall != _shown)
        {
            _shown = overall;
            var old = _icon.Icon;
            _icon.Icon = Icons.Status(overall);
            old?.Dispose();
        }
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    private async Task Try(Func<Task<string>> action)
    {
        try
        {
            var msg = await action();
            if (!string.IsNullOrEmpty(msg)) _icon.ShowBalloonTip(4000, "SYSCOM", msg, ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _icon.ShowBalloonTip(6000, "SYSCOM", ex.Message, ToolTipIcon.Error);
        }
    }

    protected override void ExitThreadCore()
    {
        _timer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _client.Dispose();
        base.ExitThreadCore();
    }
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

    public static Icon Status(Health h)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(14, 17, 22));
            g.FillEllipse(bg, 0, 0, 31, 31);
            using var track = new Pen(Color.FromArgb(60, 70, 85), 4);
            g.DrawArc(track, 6, 6, 19, 19, 135, 270);
            using var arc = new Pen(ColorFor(h), 4) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            g.DrawArc(arc, 6, 6, 19, 19, 135, 190);
            using var dot = new SolidBrush(Color.FromArgb(230, 233, 239));
            g.FillEllipse(dot, 13, 13, 6, 6);
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
