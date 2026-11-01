namespace Syscom.Server.Components;

/// <summary>
/// The "SYSCOM" entry in Windows' Installed apps, made by the setup (install/syscom.iss). In-app updates replace
/// SYSCOM without the setup, so the entry's version is brought up to date here when the server starts.
/// </summary>
public static class InstallEntry
{
    /// <summary>The setup's AppId plus "_is1", as Inno Setup names the uninstall key.</summary>
    private const string Key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{6B0E9B0C-5E1F-4C1D-9D57-6A3C2F1E8B47}_is1";

    public static void SyncVersion(string version, ILogger log)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(Key, writable: true);
            if (key is null || key.GetValue("DisplayVersion") as string == version) return;   // not installed by the setup
            key.SetValue("DisplayVersion", version);
            key.SetValue("DisplayName", "SYSCOM");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            log.LogDebug(ex, "Could not update the Installed apps entry");
        }
    }
}
