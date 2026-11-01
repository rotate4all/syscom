using System.Reflection;
using System.Runtime.InteropServices;

namespace Syscom.Tray;

/// <summary>
/// Start menu entries (SYSCOM opens this app's window, SYSCOM dashboard opens the web dashboard), and cleanup of the
/// files an update renamed aside while this app was running.
/// </summary>
internal static class Shortcuts
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "SYSCOM");

    /// <summary>Creates the Start menu entries when missing: copies installed before 0.17.1 and updated in place have none.</summary>
    public static void Ensure(string dashboardUrl)
    {
        try
        {
            var appDir = AppContext.BaseDirectory;
            // Only for an installed copy (the setup puts its uninstaller next to it, install.ps1 uninstall.ps1),
            // never for a build folder.
            if (!File.Exists(Path.Combine(appDir, "unins000.exe")) && !File.Exists(Path.Combine(appDir, "uninstall.ps1"))) return;
            var exe = Path.Combine(appDir, "syscom-tray.exe");
            Directory.CreateDirectory(Folder);
            var lnk = Path.Combine(Folder, "SYSCOM.lnk");
            if (!File.Exists(lnk)) CreateLink(lnk, exe, "--open", "Manage SYSCOM: health, updates and phone setup");
            var url = Path.Combine(Folder, "SYSCOM dashboard.url");
            var content = $"[InternetShortcut]\r\nURL={dashboardUrl}\r\nIconFile={exe}\r\nIconIndex=0\r\n";
            if (!File.Exists(url) || File.ReadAllText(url) != content) File.WriteAllText(url, content);
        }
        catch (Exception)
        {
            // Nice to have; the setup creates them too.
        }
    }

    private static void CreateLink(string path, string target, string arguments, string description)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is not available");
        var shell = Activator.CreateInstance(type)!;
        object? link = null;
        try
        {
            link = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path })!;
            var lt = link.GetType();
            void Set(string name, string value) => lt.InvokeMember(name, BindingFlags.SetProperty, null, link, new object[] { value });
            Set("TargetPath", target);
            Set("Arguments", arguments);
            Set("WorkingDirectory", Path.GetDirectoryName(target)!);
            Set("Description", description);
            Set("IconLocation", target + ",0");
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        }
        finally
        {
            if (link is not null) Marshal.FinalReleaseComObject(link);
            Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>
    /// Deletes this app's files that an update renamed aside (*.syscom-old) while the previous version was running.
    /// That version may still be exiting, so this retries for a little while.
    /// </summary>
    public static async Task CleanUpOldFiles()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var left = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.syscom-old"))
                {
                    try { File.Delete(f); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { left++; }
                }
            }
            catch (Exception) { return; }
            if (left == 0) return;
            await Task.Delay(3000);
        }
    }
}
