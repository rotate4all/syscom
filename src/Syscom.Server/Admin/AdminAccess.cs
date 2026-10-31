using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Syscom.Shared;

namespace Syscom.Server.Admin;

/// <summary>
/// Guards the management API used by the tray app. A request must come from this machine (loopback) and carry the
/// key stored in %ProgramData%\SYSCOM\admin.key, a folder only SYSTEM and Administrators can read.
/// </summary>
public sealed class AdminAccess
{
    private readonly byte[] _key;

    public AdminAccess(ILogger<AdminAccess> log)
    {
        Directory.CreateDirectory(SyscomPaths.DataDir);
        RestrictDataDir(log);

        var file = SyscomPaths.AdminKeyFile;
        if (!File.Exists(file) || new FileInfo(file).Length < 32)
        {
            File.WriteAllText(file, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        }
        _key = Encoding.ASCII.GetBytes(File.ReadAllText(file).Trim());
    }

    public bool IsAuthorized(HttpContext ctx)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote is null || !System.Net.IPAddress.IsLoopback(remote)) return false;
        var supplied = Encoding.ASCII.GetBytes(ctx.Request.Headers[SyscomPaths.AdminHeader].ToString());
        return CryptographicOperations.FixedTimeEquals(supplied, _key);
    }

    /// <summary>Config (password hash, token secret) and the admin key live here: SYSTEM + Administrators only.</summary>
    private static void RestrictDataDir(ILogger log)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!Elevation.IsAdministrator())
        {
            // Locking the folder to Administrators from a non-elevated run would lock this process out of its own
            // config on the next start. The service and elevated runs restrict it.
            log.LogWarning("Not running as administrator: {Dir} is not locked down", SyscomPaths.DataDir);
            return;
        }
        try
        {
            using var p = Process.Start(new ProcessStartInfo("icacls.exe",
                $"\"{SyscomPaths.DataDir}\" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F /T /C /Q")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            p.WaitForExit(15_000);
            if (p.ExitCode != 0) log.LogWarning("Could not restrict access to {Dir} (icacls exit {Code})", SyscomPaths.DataDir, p.ExitCode);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not restrict access to {Dir}", SyscomPaths.DataDir);
        }
    }
}
