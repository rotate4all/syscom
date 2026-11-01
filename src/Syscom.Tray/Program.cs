using System.Diagnostics;
using System.Reflection;
using Syscom.Shared;

namespace Syscom.Tray;

internal static class Program
{
    private static Mutex? _single;

    [STAThread]
    private static void Main(string[] args)
    {
        // Opening SYSCOM again (Start menu, Program Files) while it runs brings up the running copy's window.
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _single = new Mutex(true, @"Global\SYSCOM.Tray", out var first);
        if (!first && args.Contains("--restarted"))
        {
            // The previous version is on its way out after an update: wait for it.
            try { first = _single.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { first = true; }
        }
        if (!first)
        {
            show.Set();
            return;
        }

        StartupVersion = VersionOnDisk() ?? RunningVersion;
        Taskbar.AllowCreatedMessage();
        ApplicationConfiguration.Initialize();
        // The sign-in task starts it without arguments; the Start menu passes --open, a restart after an update --restarted.
        Application.Run(new TrayContext(openWindow: args.Contains("--open"), atSignIn: args.Length == 0, show));
        ReleaseSingleInstance();
    }

    public const string ShowEventName = @"Global\SYSCOM.Tray.Show";

    private static void ReleaseSingleInstance()
    {
        var m = Interlocked.Exchange(ref _single, null);
        if (m is null) return;
        try { m.ReleaseMutex(); } catch (ApplicationException) { /* not owned */ }
        m.Dispose();
    }

    /// <summary>This copy's version, like the server reports it ("0.17.1").</summary>
    public static string RunningVersion { get; } = Normalize(
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>The tray app's version in the install folder when this copy started.</summary>
    public static string StartupVersion { get; private set; } = "";

    /// <summary>The version of the tray app now in the install folder, which an update may have replaced.</summary>
    public static string? VersionOnDisk()
    {
        try
        {
            var dll = Path.Combine(AppContext.BaseDirectory, "syscom-tray.dll");
            return File.Exists(dll) ? Normalize(FileVersionInfo.GetVersionInfo(dll).ProductVersion) : null;
        }
        catch (Exception)
        {
            return null;   // being replaced right now
        }
    }

    public static string Normalize(string? version) => (version ?? "").Split('+', '-')[0].Trim();
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
    private bool _restartingTray;
    private IntPtr _taskbar = Taskbar.Handle;
    private readonly RegisteredWaitHandle _showWait;

    public TrayContext(bool openWindow, bool atSignIn, EventWaitHandle show)
    {
        var menu = new ContextMenuStrip();
        // Another start of the tray app (Start menu → SYSCOM) asks this one to show its window.
        var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _showWait = ThreadPool.RegisterWaitForSingleObject(show, (_, _) => ui.Post(_ => ShowWindow(), null), null, Timeout.Infinite, executeOnlyOnce: false);

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
        menu.Items.Add("Restart server", null, async (_, _) => await Try(async () =>
        {
            if (AdminClient.ServiceState() != "STOPPED") return (await _client.RestartAsync()).Message;
            AdminClient.StartService();
            return "Starting SYSCOM.";
        }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit tray app", null, (_, _) => ExitThread());

        _icon = new NotifyIcon { Text = "SYSCOM", ContextMenuStrip = menu, Visible = true, Icon = Icons.Tray(Health.Unknown, TraySettings.Style) };
        // Redraw when the style changes, or when Windows switches the taskbar between light and dark.
        TraySettings.Changed += Redraw;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        _icon.DoubleClick += (_, _) => ShowWindow();
        _icon.BalloonTipClicked += (_, _) => ShowWindow();

        _timer.Tick += async (_, _) => { CheckTaskbar(); await RefreshAsync(); };
        _timer.Start();
        // At sign-in, SYSCOM's service should be running. If it isn't (it failed while Windows started, say), start it,
        // unless someone set it to start manually or disabled it. Only then: an update stops it for a moment, too.
        if (atSignIn)
            _ = Task.Run(() =>
            {
                if (AdminClient.ServiceState() == "STOPPED" && AdminClient.StartsAutomatically()) AdminClient.StartService();
            });
        _ = RefreshAsync();
        if (openWindow) ShowWindow();
        _ = Shortcuts.CleanUpOldFiles();
        Shortcuts.Ensure(_client.DashboardUrl);
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
            // An update replaced this app's files (this copy keeps running from the old ones, renamed aside). Once
            // SYSCOM is back on the same version, start the new tray app in this one's place.
            if (status is not null && Program.VersionOnDisk() is { Length: > 0 } onDisk && onDisk != Program.StartupVersion
                && Program.Normalize(status.Version) == onDisk)
            {
                RestartIntoNewVersion();
                return;
            }
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

    private void RestartIntoNewVersion()
    {
        if (_restartingTray) return;
        _restartingTray = true;
        var args = _form is { IsDisposed: false, Visible: true } ? "--restarted --open" : "--restarted";
        try
        {
            // The new copy waits until this one has exited (Main then releases the single-instance lock).
            Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "syscom-tray.exe"), args) { UseShellExecute = true });
            ExitThread();
        }
        catch (Exception ex)
        {
            // Keep running the old one; the next start of Windows starts the new one.
            _icon.ShowBalloonTip(6000, "SYSCOM", $"Could not restart the tray app: {ex.Message}", ToolTipIcon.Warning);
        }
    }

    /// <summary>
    /// Adds the icon again when a new taskbar shows up. NotifyIcon does that on Explorer's "TaskbarCreated" message
    /// (see <see cref="Taskbar"/>); this covers the case where the message never arrives.
    /// </summary>
    private void CheckTaskbar()
    {
        var taskbar = Taskbar.Handle;
        if (taskbar == _taskbar) return;
        _taskbar = taskbar;
        if (taskbar == IntPtr.Zero) return;
        _icon.Visible = false;
        _icon.Visible = true;
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
        _showWait.Unregister(null);
        TraySettings.Changed -= Redraw;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        _timer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _client.Dispose();
        base.ExitThreadCore();
    }
}
