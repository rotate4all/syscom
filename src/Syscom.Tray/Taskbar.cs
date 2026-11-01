using System.Runtime.InteropServices;

namespace Syscom.Tray;

/// <summary>
/// The taskbar the tray icon lives in. The tray app starts as Windows signs in, often before Explorer has made the
/// taskbar, and an icon added then is lost. Explorer announces a new taskbar (at sign-in, or after it restarted) with
/// the "TaskbarCreated" message, which NotifyIcon answers by adding its icon again, but Windows doesn't pass that
/// message on to an app running as administrator unless the app lets it through.
/// </summary>
internal static class Taskbar
{
    /// <summary>Lets Explorer's "TaskbarCreated" message through to this (elevated) app.</summary>
    public static void AllowCreatedMessage() => ChangeWindowMessageFilter(RegisterWindowMessage("TaskbarCreated"), MsgFltAdd);

    /// <summary>The taskbar's window, or zero while there is none (Explorer not started yet, or restarting).</summary>
    public static IntPtr Handle => FindWindow("Shell_TrayWnd", null);

    private const uint MsgFltAdd = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilter(uint message, uint flag);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);
}
