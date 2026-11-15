using System.Runtime.InteropServices;

namespace Syscom.Tray.Osd;

/// <summary>
/// One group of OSD readings on the screen: a borderless window that is always on top, lets every click and key
/// through to the game below (it never takes the focus, so a full-screen game isn't minimised), stays out of the
/// taskbar and Alt+Tab, and is drawn with per-pixel transparency (a layered window updated with a picture).
/// </summary>
internal sealed class OsdWindow : NativeWindow, IDisposable
{
    private bool _visible;

    public OsdWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "SYSCOM on-screen display",
            Style = unchecked((int)WS_POPUP),
            ExStyle = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
        });
    }

    /// <summary>Puts the picture on the screen at <paramref name="at"/> (physical pixels), with an overall opacity.</summary>
    public void Present(Bitmap picture, Point at, byte opacity)
    {
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = picture.GetHbitmap(Color.FromArgb(0));   // keeps the alpha channel (premultiplied)
        var old = SelectObject(memory, bitmap);
        try
        {
            var size = new SIZE { cx = picture.Width, cy = picture.Height };
            var source = new POINT();
            var dest = new POINT { x = at.X, y = at.Y };
            var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = opacity, AlphaFormat = AC_SRC_ALPHA };
            UpdateLayeredWindow(Handle, screen, ref dest, ref size, memory, ref source, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
        // Games in a borderless window may put themselves on top too: claim the top again with every frame.
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | (_visible ? 0 : SWP_SHOWWINDOW));
        _visible = true;
    }

    public void Hide()
    {
        if (!_visible) return;
        ShowWindow(Handle, SW_HIDE);
        _visible = false;
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_MOUSEACTIVATE:
                m.Result = MA_NOACTIVATE;
                return;
            case WM_NCHITTEST:
                m.Result = HTTRANSPARENT;
                return;
        }
        base.WndProc(ref m);
    }

    public void Dispose() => DestroyHandle();

    private const uint WS_POPUP = 0x80000000;
    private const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000,
        WS_EX_NOACTIVATE = 0x8000000;
    private const int WM_MOUSEACTIVATE = 0x21, WM_NCHITTEST = 0x84;
    private static readonly IntPtr MA_NOACTIVATE = 3, HTTRANSPARENT = -1, HWND_TOPMOST = -1;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    private const int SW_HIDE = 0;
    private const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;
    private const uint ULW_ALPHA = 2;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc,
        ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
