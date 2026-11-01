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
        var style = new ToolStripMenuItem("Tray icon");
        foreach (var (value, label) in TraySettings.Styles)
            style.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => TraySettings.Style = value) { Tag = value });
        style.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem item in style.DropDownItems) item.Checked = (IconStyle)item.Tag! == TraySettings.Style;
        };
        menu.Items.Add(style);
        menu.Items.Add("Restart server", null, async (_, _) => await Try(() => _client.RestartAsync()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit tray app", null, (_, _) => ExitThread());

        _icon = new NotifyIcon { Text = "SYSCOM", ContextMenuStrip = menu, Visible = true, Icon = Icons.Tray(Health.Unknown, TraySettings.Style) };
        // Redraw when the style changes, or when Windows switches the taskbar between light and dark.
        TraySettings.Changed += Redraw;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
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
            Redraw();
        }
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    private void Redraw()
    {
        var old = _icon.Icon;
        _icon.Icon = Icons.Tray(_shown < 0 ? Health.Unknown : _shown, TraySettings.Style);
        old?.Dispose();
    }

    private void OnPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category is Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.VisualStyle) Redraw();
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
        TraySettings.Changed -= Redraw;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        _timer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _client.Dispose();
        base.ExitThreadCore();
    }
}
