using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
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

    public AdminAccess()
    {
        Directory.CreateDirectory(SyscomPaths.DataDir);

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
}

/// <summary>
/// Config (password hash, token secret), the admin key and the HTTPS keys live in the data folder: SYSTEM and
/// Administrators only.
/// </summary>
public static class DataDirAccess
{
    /// <summary>
    /// Locks the folder to SYSTEM + Administrators and makes everything inside simply inherit that. Runs before
    /// anything in the folder is read, so it also repairs files that 0.1.0 previews locked everyone out of.
    /// Returns a warning, or null when all is well.
    /// </summary>
    public static string? Secure()
    {
        if (!OperatingSystem.IsWindows()) return null;
        // Locking the folder from a non-elevated run would lock this process out of its own settings.
        if (!Elevation.IsAdministrator()) return $"Not running as administrator: {SyscomPaths.DataDir} is not locked down";
        try
        {
            Directory.CreateDirectory(SyscomPaths.DataDir);
            var dir = SyscomPaths.DataDir.TrimEnd('\\');
            // Locked down by an earlier start: what's inside was reset then, and SYSCOM's own files inherit. Resetting
            // every file again (thousands, with the update backups) runs before the service reports to Windows, and
            // while Windows starts that can take long enough for Windows to give up on the service.
            if (IsLockedDown(dir)) return null;
            // The folder itself: no inherited permissions, full control for SYSTEM and Administrators, passed on to
            // everything inside. (Inheritance flags only apply to folders, so this is never run recursively.)
            var root = Icacls($"\"{dir}\" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F /Q");
            if (root != 0) return $"Could not restrict access to {dir} (icacls exit {root})";
            // Everything inside: drop any explicit or broken permissions and inherit the folder's.
            if (Directory.EnumerateFileSystemEntries(dir).Any())
            {
                var children = Icacls($"\"{dir}\\*\" /reset /T /C /Q");
                if (children != 0) return $"Could not reset permissions inside {dir} (icacls exit {children})";
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not restrict access to {SyscomPaths.DataDir}: {ex.Message}";
        }
    }

    /// <summary>
    /// Whether the folder has exactly the permissions <see cref="Secure"/> gives it: none inherited, full control for
    /// SYSTEM and Administrators, passed on to everything inside.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static bool IsLockedDown(string dir)
    {
        var acl = new DirectoryInfo(dir).GetAccessControl(AccessControlSections.Access);
        if (!acl.AreAccessRulesProtected) return false;
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        var owners = new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) };
        return rules.Count == owners.Length && owners.All(sid => rules.Any(r =>
            r.IdentityReference.Equals(sid) && r.AccessControlType == AccessControlType.Allow &&
            (r.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl &&
            r.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) &&
            r.PropagationFlags == PropagationFlags.None));
    }

    private static int Icacls(string args)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("icacls.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return p.HasExited ? p.ExitCode : -1;
    }
}
