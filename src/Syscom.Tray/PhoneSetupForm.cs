using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using Syscom.Shared;

namespace Syscom.Tray;

/// <summary>
/// "Set up a phone": a QR code that opens this PC's setup page (/trust) on the phone, where it trusts the PC's own
/// certificate and installs the dashboard as an app. Shows the certificate fingerprint so it can be compared.
/// </summary>
internal sealed class PhoneSetupForm : Form
{
    private const int QrPixels = 264;

    private readonly ServerStatus _status;
    private readonly ComboBox _address = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = QrPixels };
    private readonly PictureBox _qr = new() { Size = new Size(QrPixels, QrPixels), SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Color.White };
    private readonly LinkLabel _link = new() { AutoSize = true };
    private static PhoneSetupForm? _open;

    /// <summary>Opens the window (or brings the open one to the front) with fresh server details.</summary>
    public static async Task ShowAsync(AdminClient client)
    {
        if (_open is { IsDisposed: false }) { _open.Activate(); return; }
        var status = await client.StatusAsync() ?? throw new InvalidOperationException("SYSCOM did not answer.");
        _open = new PhoneSetupForm(status);
        _open.Show();
    }

    public PhoneSetupForm(ServerStatus status)
    {
        _status = status;
        Text = "Set up a phone";
        Icon = Icons.Status(Health.Ok);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(18);
        Font = new Font("Segoe UI", 9.5f);

        var https = status.HttpsPort is not null;
        var width = QrPixels + 120;
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };

        layout.Controls.Add(new Label { Text = $"Use {status.Name} on your phone", AutoSize = true, Font = new Font("Segoe UI Semibold", 13f) });
        layout.Controls.Add(Wrapped(https
            ? "Scan this code with the phone's camera. The page that opens walks you through trusting this PC's " +
              "certificate once; after that the dashboard opens securely and can be installed as an app."
            : "HTTPS is turned off on this PC (HttpsEnabled in config.json), so phones can open the dashboard but only " +
              "add a shortcut, not install it as an app. Scan this code to open the dashboard.", width));
        layout.Controls.Add(Wrapped("The phone must be on the same network as this PC.", width, SystemColors.GrayText));

        var addresses = Order(status.Urls).ToList();
        if (addresses.Count == 0)
        {
            layout.Controls.Add(Wrapped("This PC has no network address right now. Connect it to your network and open this window again.", width, Color.Firebrick));
        }
        else
        {
            if (addresses.Count > 1)
            {
                layout.Controls.Add(new Label { Text = "Network address", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
                _address.Items.AddRange(addresses.Cast<object>().ToArray());
                _address.SelectedIndexChanged += (_, _) => ShowCode();
                layout.Controls.Add(_address);
            }
            else
            {
                _address.Items.Add(addresses[0]);
            }
            layout.Controls.Add(_qr);
            _link.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(_link.Text) { UseShellExecute = true });
            var copy = new Button { Text = "Copy link", AutoSize = true };
            copy.Click += (_, _) => Clipboard.SetText(_link.Text);
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            _link.Padding = new Padding(0, 6, 8, 0);
            row.Controls.AddRange(new Control[] { _link, copy });
            layout.Controls.Add(row);
            _address.SelectedIndex = 0;
        }

        if (https && status.CaFingerprint is { } fp)
        {
            layout.Controls.Add(Wrapped("Certificate fingerprint (SHA-256). The setup page on the phone shows the same value:", width, SystemColors.GrayText));
            var groups = string.Join(" ", Enumerable.Range(0, fp.Length / 4).Select(i => fp.Substring(i * 4, 4)));
            layout.Controls.Add(new Label
            {
                Text = groups,
                Font = new Font("Consolas", 9.5f),
                AutoSize = true,
                MaximumSize = new Size(width, 0),
            });
        }

        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Close();
        layout.Controls.Add(close);
        AcceptButton = CancelButton = close;
        Controls.Add(layout);
    }

    private void ShowCode()
    {
        var baseUrl = ((string)_address.SelectedItem!).TrimEnd('/');
        var url = _status.HttpsPort is null ? baseUrl + "/" : baseUrl + "/trust";
        _link.Text = url;
        var old = _qr.Image;
        _qr.Image = Render(QrCode.Encode(url), QrPixels);
        old?.Dispose();
    }

    /// <summary>Home networks first (192.168/10/172.16), then VPN and link-local addresses.</summary>
    private static IEnumerable<string> Order(IEnumerable<string> urls) => urls.OrderBy(u =>
    {
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || !IPAddress.TryParse(uri.Host, out var ip)) return 9;
        var b = ip.GetAddressBytes();
        return b[0] switch
        {
            192 when b[1] == 168 => 0,
            10 => 1,
            172 when b[1] is >= 16 and < 32 => 2,
            100 when b[1] is >= 64 and < 128 => 4,
            169 when b[1] == 254 => 8,
            _ => 5,
        };
    });

    /// <summary>Draws the code at the largest whole-pixel scale that fits, with the standard 4-module quiet zone.</summary>
    private static Bitmap Render(QrCode code, int maxPixels)
    {
        var modules = code.Size + 8;
        var scale = Math.Max(1, maxPixels / modules);
        var px = modules * scale;
        var bmp = new Bitmap(px, px);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.None;
        g.Clear(Color.White);
        using var dark = new SolidBrush(Color.Black);
        for (var y = 0; y < code.Size; y++)
            for (var x = 0; x < code.Size; x++)
                if (code[x, y]) g.FillRectangle(dark, (x + 4) * scale, (y + 4) * scale, scale, scale);
        return bmp;
    }

    private static Label Wrapped(string text, int width, Color? color = null) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(width, 0),
        ForeColor = color ?? SystemColors.ControlText,
        Padding = new Padding(0, 2, 0, 4),
    };
}
