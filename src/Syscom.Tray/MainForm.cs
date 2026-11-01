using System.Diagnostics;
using Syscom.Shared;

namespace Syscom.Tray;

/// <summary>Management window: server info, sign-in password, and every component's health, version and updates.</summary>
internal sealed class MainForm : Form
{
    private readonly AdminClient _client;
    private readonly Label _title = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 14f) };
    private readonly Label _summary = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly FlowLayoutPanel _urls = new() { AutoSize = true, WrapContents = true, Dock = DockStyle.Top };
    private readonly Label _password = new() { AutoSize = true };
    private readonly ListView _list = new()
    {
        View = View.Details, FullRowSelect = true, CheckBoxes = true, MultiSelect = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable, Dock = DockStyle.Fill, ShowItemToolTips = true,
    };
    private readonly Label _status = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Button _check = new() { Text = "Check for updates", AutoSize = true };
    private readonly Button _updateSelected = new() { Text = "Update selected", AutoSize = true };
    private readonly Button _updateAll = new() { Text = "Update all", AutoSize = true };
    private readonly Button _restart = new() { Text = "Restart server", AutoSize = true };
    private readonly ProgressBar _progress = new()
    {
        Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25, Width = 140, Height = 16, Visible = false,
        Margin = new Padding(10, 9, 0, 0),
    };
    private readonly ComboBox _autoUpdate = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly ComboBox _trayStyle = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };
    private bool _busy;
    private bool _refreshing;
    private bool _settingsLoaded;
    private bool _askedToStart;
    // Set while SYSCOM restarts to install updates (or for a restart): the window waits until the updater reports back.
    private DateTimeOffset? _waitingSince;
    private static readonly TimeSpan WaitLimit = TimeSpan.FromMinutes(4);

    private static readonly (string Value, string Label)[] AutoUpdateChoices =
    {
        ("syscom", "SYSCOM and its dashboard (recommended)"),
        ("all", "Every component"),
        ("off", "Off: only tell me"),
    };

    public MainForm(AdminClient client)
    {
        _client = client;
        Text = "SYSCOM";
        Icon = Icons.Status(Health.Unknown);
        MinimumSize = new Size(760, 520);
        Size = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        Padding = new Padding(16);

        _list.Columns.Add("Component", 190);
        _list.Columns.Add("Installed", 100);
        _list.Columns.Add("Latest", 100);
        _list.Columns.Add("Health", 80);
        _list.Columns.Add("Details", 380);
        _list.SmallImageList = HealthImages();

        // header
        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var left = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        left.Controls.AddRange(new Control[] { _title, _summary, _urls, _password });
        var right = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        var open = new Button { Text = "Open dashboard", AutoSize = true };
        open.Click += (_, _) => Process.Start(new ProcessStartInfo(_client.DashboardUrl) { UseShellExecute = true });
        var phone = new Button { Text = "Set up phone…", AutoSize = true };
        phone.Click += async (_, _) =>
        {
            try { await PhoneSetupForm.ShowAsync(_client); }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
        var pwd = new Button { Text = "Change password…", AutoSize = true };
        pwd.Click += async (_, _) => await ChangePasswordAsync();
        _restart.Click += async (_, _) => await RunAsync("Restarting SYSCOM…", () => _client.RestartAsync());
        var data = new Button { Text = "Open data folder", AutoSize = true };
        data.Click += (_, _) => Process.Start(new ProcessStartInfo(SyscomPaths.DataDir) { UseShellExecute = true });
        right.Controls.AddRange(new Control[] { open, phone, pwd, _restart, data });
        header.Controls.Add(left, 0, 0);
        header.Controls.Add(right, 1, 0);

        var componentsLabel = new Label { Text = "Components", AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f), Dock = DockStyle.Top, Padding = new Padding(0, 12, 0, 6) };

        // Settings: what installs by itself, and how the tray icon looks.
        var settings = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 10, 0, 0) };
        foreach (var (_, label) in AutoUpdateChoices) _autoUpdate.Items.Add(label);
        foreach (var (_, label) in TraySettings.Styles) _trayStyle.Items.Add(label);
        _trayStyle.SelectedIndex = Array.FindIndex(TraySettings.Styles, s => s.Style == TraySettings.Style);
        _trayStyle.SelectedIndexChanged += (_, _) => TraySettings.Style = TraySettings.Styles[_trayStyle.SelectedIndex].Style;
        _autoUpdate.Enabled = false;
        _autoUpdate.SelectedIndexChanged += async (_, _) =>
        {
            if (!_settingsLoaded || _autoUpdate.SelectedIndex < 0) return;
            try
            {
                var now = await _client.SettingsAsync();
                await _client.SaveSettingsAsync(new ServerSettings(AutoUpdateChoices[_autoUpdate.SelectedIndex].Value, now?.UpdateCheckHours ?? 6));
                _status.Text = "Saved.";
            }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
        Label Caption(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        settings.Controls.AddRange(new Control[] { Caption("Install updates automatically:"), _autoUpdate, Caption("   Tray icon:"), _trayStyle });
        TraySettings.Changed += SyncTrayStyle;
        Disposed += (_, _) => TraySettings.Changed -= SyncTrayStyle;

        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 8, 0, 0) };
        footer.Controls.AddRange(new Control[] { _check, _updateSelected, _updateAll, _progress, _status });
        _status.Padding = new Padding(8, 7, 0, 0);

        _check.Click += async (_, _) => await CheckForUpdatesAsync();
        _updateSelected.Click += async (_, _) =>
        {
            var ids = _list.CheckedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToList();
            if (ids.Count == 0) { _status.Text = "Tick the components to update first."; return; }
            await RunAsync("Downloading updates…", () => _client.UpdateAsync(ids));
        };
        _updateAll.Click += async (_, _) => await RunAsync("Downloading updates…", () => _client.UpdateAsync(Array.Empty<string>()));

        Controls.Add(_list);
        Controls.Add(componentsLabel);
        Controls.Add(header);
        Controls.Add(settings);
        Controls.Add(footer);

        _timer.Tick += async (_, _) => await RefreshAsync();
        Shown += async (_, _) => { await RefreshAsync(); _timer.Start(); };
        _status.TextChanged += (_, _) => _status.ForeColor = SystemColors.GrayText;
        FormClosing += (_, e) =>
        {
            // Closing the window keeps the tray app running.
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); _timer.Stop(); }
        };
        VisibleChanged += (_, _) => { if (Visible) _timer.Start(); };
    }

    private void SyncTrayStyle()
    {
        var i = Array.FindIndex(TraySettings.Styles, s => s.Style == TraySettings.Style);
        if (_trayStyle.SelectedIndex != i) _trayStyle.SelectedIndex = i;
    }

    public async Task CheckForUpdatesAsync()
    {
        if (_busy) return;
        SetBusy(true, "Checking for updates…");
        try
        {
            Fill(await _client.CheckAsync());
            var n = _list.Items.Cast<ListViewItem>().Count(i => i.Checked);
            _status.Text = n == 0 ? "Everything is up to date." : $"{n} update(s) available.";
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    // Runs every few seconds, also while busy: the component list then shows what each one is doing (downloading, …).
    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var status = await _client.StatusAsync();
            _askedToStart = false;
            if (_waitingSince is { } since)
            {
                if (status?.LastUpdate is { } done && done.Time >= since) EndWait(done.Message, done.Success);
                else if (DateTimeOffset.Now - since > WaitLimit) EndWait("SYSCOM is running but did not report how the update went. Details: updater.log in the data folder (updates).", false);
            }
            if (status is not null)
            {
                _title.Text = $"{status.Name}";
                _summary.Text = $"SYSCOM {status.Version} · {(status.RunningAsService ? "Windows service" : "console")} · " +
                                $"{status.Clients} client(s) connected · {status.Sensors} sensors";
                if (_urls.Controls.Count != status.Urls.Count)
                {
                    _urls.Controls.Clear();
                    foreach (var url in status.Urls)
                    {
                        var link = new LinkLabel { Text = url, AutoSize = true };
                        link.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                        _urls.Controls.Add(link);
                    }
                }
                if (status.LastUpdate is { } last && string.IsNullOrEmpty(_status.Text))
                    ShowResult($"Last update {last.Time:g}: {last.Message}", last.Success);
            }
            var initial = AdminClient.InitialPassword();
            _password.Text = initial is null
                ? "Browsers sign in with the password you set."
                : $"Sign-in password (generated): {initial}  ·  please change it";
            _password.ForeColor = initial is null ? SystemColors.GrayText : Color.FromArgb(180, 83, 9);
            Fill(await _client.ComponentsAsync());
            if (!_settingsLoaded && await _client.SettingsAsync() is { } st)
            {
                _autoUpdate.SelectedIndex = Math.Max(0, Array.FindIndex(AutoUpdateChoices, c => c.Value == st.AutoUpdate));
                _autoUpdate.Enabled = true;
                _settingsLoaded = true;
            }
        }
        catch (Exception)
        {
            var state = AdminClient.ServiceState();
            if (_waitingSince is { } since)
            {
                // The updater stopped SYSCOM to swap its files; it starts it again (or the previous version) by itself.
                _title.Text = "SYSCOM is restarting…";
                _summary.Text = state is null ? "" : $"Service state: {state}";
                if (DateTimeOffset.Now - since > WaitLimit)
                    EndWait("SYSCOM has not come back. Details: updater.log in the data folder (updates).", false);
            }
            else
            {
                _title.Text = "SYSCOM is not running";
                _summary.Text = state is null ? "The SYSCOM service is not installed. Run install.ps1 as administrator." : $"Service state: {state}";
                // Ask once per stop, not on every refresh.
                if (state == "STOPPED" && !_askedToStart && Visible)
                {
                    _askedToStart = true;
                    if (MessageBox.Show(this, "The SYSCOM service is stopped. Start it now?", "SYSCOM",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        AdminClient.StartService();
                }
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void Fill(List<ComponentStatus> comps)
    {
        var checkedIds = _list.CheckedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToHashSet();
        var firstFill = _list.Items.Count == 0;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var c in comps)
        {
            var detail = c.State is "idle" ? c.LastError ?? c.HealthDetail : $"{Phase(c.State)}…";
            if (c.State == "failed" && c.LastError is not null) detail = "Update failed: " + c.LastError;
            var item = new ListViewItem(new[]
            {
                c.Name,
                c.Installed ?? "not installed",
                c.Latest ?? (c.LastChecked is null ? "not checked" : "–"),
                c.Health.ToString(),
                detail,
            })
            {
                Tag = c.Id,
                ImageIndex = (int)c.Health,
                ToolTipText = $"{c.Description}\n{c.HealthDetail}",
                Checked = c.UpdateAvailable && c.CanUpdate && (firstFill || checkedIds.Contains(c.Id) || !checkedIds.Any()),
            };
            if (c.UpdateAvailable && c.CanUpdate) item.Font = new Font(_list.Font, FontStyle.Bold);
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        _updateAll.Enabled = !_busy && comps.Any(c => c.UpdateAvailable && c.CanUpdate);
        Icon = Icons.Status(comps.Select(c => c.Health).DefaultIfEmpty(Health.Ok).Max());
    }

    /// <summary>
    /// Runs an update or restart with the buttons disabled and a progress bar. When SYSCOM restarts for it, the
    /// window keeps waiting until the updater reports how it went (or a few minutes pass).
    /// </summary>
    private async Task RunAsync(string working, Func<Task<UpdateOutcome>> action)
    {
        if (_busy) return;
        var since = DateTimeOffset.Now.AddSeconds(-2);
        SetBusy(true, working);
        try
        {
            var outcome = await action();
            _status.Text = outcome.Message;
            if (outcome.Restarting) { _waitingSince = since; return; }   // stays busy until SYSCOM is back
        }
        catch (Exception ex)
        {
            ShowResult(ex.Message, false);
        }
        SetBusy(false);
        await RefreshAsync();
    }

    private void EndWait(string message, bool success)
    {
        _waitingSince = null;
        SetBusy(false);
        ShowResult(message, success);
    }

    private void ShowResult(string message, bool success)
    {
        _status.Text = message;
        _status.ForeColor = success ? SystemColors.GrayText : Color.Firebrick;
    }

    private async Task ChangePasswordAsync()
    {
        using var dlg = new PasswordDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK || _busy) return;
        SetBusy(true, "Saving…");
        try { _status.Text = await _client.SetPasswordAsync(dlg.Password); }
        catch (Exception ex) { ShowResult(ex.Message, false); }
        SetBusy(false);
        await RefreshAsync();
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        _check.Enabled = _updateSelected.Enabled = _updateAll.Enabled = _restart.Enabled = !busy;
        _progress.Visible = busy;
        if (message is not null) _status.Text = message;
    }

    private static string Phase(string s) => s switch
    {
        "checking" => "Checking",
        "downloading" => "Downloading",
        "installing" => "Installing",
        "restarting" => "Restarting",
        _ => s,
    };

    private static ImageList HealthImages()
    {
        var list = new ImageList { ImageSize = new Size(12, 12), ColorDepth = ColorDepth.Depth32Bit };
        foreach (var h in new[] { Health.Unknown, Health.Ok, Health.Warning, Health.Error })
        {
            var bmp = new Bitmap(12, 12);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var b = new SolidBrush(h == Health.Unknown ? Color.Silver : Icons.ColorFor(h));
            g.FillEllipse(b, 1, 1, 10, 10);
            list.Images.Add(bmp);
        }
        return list;
    }
}

internal sealed class PasswordDialog : Form
{
    private readonly TextBox _a = new() { UseSystemPasswordChar = true, Width = 260 };
    private readonly TextBox _b = new() { UseSystemPasswordChar = true, Width = 260 };
    public string Password => _a.Text;

    public PasswordDialog()
    {
        Text = "Change sign-in password";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        Font = new Font("Segoe UI", 9.5f);

        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var error = new Label { AutoSize = true, ForeColor = Color.Firebrick };
        ok.Click += (_, e) =>
        {
            if (_a.Text.Length < 6) { error.Text = "Use at least 6 characters."; DialogResult = DialogResult.None; }
            else if (_a.Text != _b.Text) { error.Text = "The passwords don't match."; DialogResult = DialogResult.None; }
        };
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        var buttons = new FlowLayoutPanel { AutoSize = true };
        buttons.Controls.AddRange(new Control[] { ok, cancel });
        layout.Controls.AddRange(new Control[]
        {
            new Label { Text = "New password (all browsers will be signed out)", AutoSize = true }, _a,
            new Label { Text = "Repeat", AutoSize = true }, _b, error, buttons,
        });
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
