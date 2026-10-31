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
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };
    private bool _busy;

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
        var pwd = new Button { Text = "Change password…", AutoSize = true };
        pwd.Click += async (_, _) => await ChangePasswordAsync();
        var restart = new Button { Text = "Restart server", AutoSize = true };
        restart.Click += async (_, _) => await RunAsync(() => _client.RestartAsync());
        var data = new Button { Text = "Open data folder", AutoSize = true };
        data.Click += (_, _) => Process.Start(new ProcessStartInfo(SyscomPaths.DataDir) { UseShellExecute = true });
        right.Controls.AddRange(new Control[] { open, pwd, restart, data });
        header.Controls.Add(left, 0, 0);
        header.Controls.Add(right, 1, 0);

        var componentsLabel = new Label { Text = "Components", AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f), Dock = DockStyle.Top, Padding = new Padding(0, 12, 0, 6) };

        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 8, 0, 0) };
        footer.Controls.AddRange(new Control[] { _check, _updateSelected, _updateAll, _status });
        _status.Padding = new Padding(8, 7, 0, 0);

        _check.Click += async (_, _) => await CheckForUpdatesAsync();
        _updateSelected.Click += async (_, _) =>
        {
            var ids = _list.CheckedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToList();
            if (ids.Count == 0) { _status.Text = "Tick the components to update first."; return; }
            await RunAsync(() => _client.UpdateAsync(ids));
        };
        _updateAll.Click += async (_, _) => await RunAsync(() => _client.UpdateAsync(Array.Empty<string>()));

        Controls.Add(_list);
        Controls.Add(componentsLabel);
        Controls.Add(header);
        Controls.Add(footer);

        _timer.Tick += async (_, _) => await RefreshAsync();
        Shown += async (_, _) => { await RefreshAsync(); _timer.Start(); };
        FormClosing += (_, e) =>
        {
            // Closing the window keeps the tray app running.
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); _timer.Stop(); }
        };
        VisibleChanged += (_, _) => { if (Visible) _timer.Start(); };
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

    private async Task RefreshAsync()
    {
        if (_busy) return;
        try
        {
            var status = await _client.StatusAsync();
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
                    _status.Text = $"Last update {last.Time:g}: {last.Message}";
            }
            var initial = AdminClient.InitialPassword();
            _password.Text = initial is null
                ? "Browsers sign in with the password you set."
                : $"Sign-in password (generated): {initial}  ·  please change it";
            _password.ForeColor = initial is null ? SystemColors.GrayText : Color.FromArgb(180, 83, 9);
            Fill(await _client.ComponentsAsync());
        }
        catch (Exception)
        {
            var state = AdminClient.ServiceState();
            _title.Text = "SYSCOM is not running";
            _summary.Text = state is null ? "The SYSCOM service is not installed. Run install.ps1 as administrator." : $"Service state: {state}";
            if (state == "STOPPED" && MessageBox.Show(this, "The SYSCOM service is stopped. Start it now?", "SYSCOM",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                AdminClient.StartService();
            }
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

    private async Task RunAsync(Func<Task<string>> action)
    {
        if (_busy) return;
        SetBusy(true, "Working…");
        try
        {
            _status.Text = await action();
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

    private async Task ChangePasswordAsync()
    {
        using var dlg = new PasswordDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        await RunAsync(() => _client.SetPasswordAsync(dlg.Password));
        await RefreshAsync();
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        _check.Enabled = _updateSelected.Enabled = _updateAll.Enabled = !busy;
        UseWaitCursor = busy;
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
