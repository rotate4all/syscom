namespace Syscom.Server.Sensors;

/// <summary>
/// LibreHardwareMonitor talks to low-level sensors (CPU MSRs, Super I/O, SMU) through the PawnIO driver, which is
/// installed separately. We only ever use it, never install it, and warn when it is missing or older than allowed.
/// </summary>
public static class PawnIoCheck
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

    public static string? InstalledVersion()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(UninstallKey);
            return key?.GetValue("DisplayVersion") as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Null when fine, otherwise a human-readable warning.</summary>
    public static string? Warning(string minimum)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var installed = InstalledVersion();
        if (installed is null)
            return "PawnIO driver not found: CPU temperatures and motherboard sensors will be missing. install.ps1 installs it; for a run from source use: winget install namazso.PawnIO (then restart SYSCOM)";
        if (Version.TryParse(installed, out var have) && Version.TryParse(minimum, out var need) && have < need)
            return $"PawnIO {installed} is older than {minimum} (older drivers had security issues). Update with: winget upgrade namazso.PawnIO";
        return null;
    }
}
