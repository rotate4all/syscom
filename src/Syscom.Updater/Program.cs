// syscom-updater: applies a staged update while the SYSCOM server is stopped.
//   1. stop the server (Windows service or console process)
//   2. back up every file it is about to overwrite, then copy the new files in (a file another program has open,
//      such as the running tray app, is renamed aside first: Windows allows that, and the old copy goes later)
//   3. start the server and wait for /api/health
//   4. if it doesn't come back healthy: restore the backup and start the old version again
using System.Diagnostics;
using System.Text.Json;
using Syscom.Shared;

if (args.Length != 2 || args[0] != "--plan")
{
    Console.Error.WriteLine("usage: syscom-updater --plan <plan.json>");
    return 2;
}

var planPath = Path.GetFullPath(args[1]);
var plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(planPath), SyscomPaths.Json)
           ?? throw new InvalidOperationException("Empty plan");
var log = new Log(Path.Combine(Path.GetDirectoryName(planPath)!, "updater.log"));
var components = plan.Steps.Select(s => $"{s.Component} {s.Version}").ToList();
log.Write($"Plan: {(components.Count == 0 ? "restart only" : string.Join(", ", components))}");

Process? started = null;            // console-mode server we launched
var backedUp = new List<string>();   // relative paths with a copy in BackupDir
var created = new List<string>();    // relative paths that did not exist before
var movedAside = new List<(string Rel, string Aside)>();   // in-use files renamed so the new one could take their place
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

try
{
    StopServer();
    ApplySteps();
    StartServer();
    if (await Healthy(TimeSpan.FromSeconds(60)))
    {
        Finish(true, false, components.Count == 0 ? "Restarted." : $"Updated {string.Join(", ", components)}.");
        return 0;
    }
    log.Write("Server did not become healthy; rolling back.");
}
catch (Exception ex)
{
    log.Write($"Update failed: {ex}");
}

// ---- rollback
try
{
    StopServer();
    Restore();
    StartServer();
    var ok = await Healthy(TimeSpan.FromSeconds(60));
    Finish(false, true, ok
        ? "Update failed and was rolled back; the previous version is running."
        : "Update failed; rollback done but SYSCOM did not start. See updater.log.");
}
catch (Exception ex)
{
    log.Write($"Rollback failed: {ex}");
    Finish(false, false, "Update and rollback failed. See updater.log.");
}
return 1;

// ------------------------------------------------------------------

void StopServer()
{
    if (!string.IsNullOrEmpty(plan.ServiceName))
    {
        Sc("stop", plan.ServiceName);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline && !Sc("query", plan.ServiceName).Contains("STOPPED")) Thread.Sleep(500);
    }
    else if (plan.ServerPid is { } pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.WaitForExit(30_000)) { p.Kill(entireProcessTree: true); p.WaitForExit(10_000); }
        }
        catch (ArgumentException) { /* already gone */ }
    }
    // Give Windows a moment to release file handles (not for a shared `dotnet` host outside the install folder).
    if (Path.GetFullPath(plan.ServerExe).StartsWith(Path.GetFullPath(plan.InstallDir), StringComparison.OrdinalIgnoreCase))
        WaitUnlocked(plan.ServerExe, TimeSpan.FromSeconds(20));
    log.Write("Server stopped.");
}

void StartServer()
{
    if (!string.IsNullOrEmpty(plan.ServiceName))
    {
        Sc("start", plan.ServiceName);
    }
    else
    {
        var psi = new ProcessStartInfo(plan.ServerExe) { UseShellExecute = false, WorkingDirectory = plan.InstallDir };
        foreach (var a in plan.ServerArgs) psi.ArgumentList.Add(a);
        started = Process.Start(psi);
    }
    log.Write("Server started.");
}

async Task<bool> Healthy(TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        try
        {
            using var resp = await http.GetAsync(plan.HealthUrl);
            if (resp.IsSuccessStatusCode) { log.Write("Health check passed."); return true; }
        }
        catch (Exception) { /* not up yet */ }

        // Fail fast when the new version crashed on start instead of waiting out the timeout.
        if (started is { HasExited: true }) { log.Write($"Server exited with code {started.ExitCode}."); return false; }
        if (!string.IsNullOrEmpty(plan.ServiceName) && DateTime.UtcNow > deadline - timeout + TimeSpan.FromSeconds(10)
            && Sc("query", plan.ServiceName).Contains("STOPPED"))
        {
            log.Write("Service stopped on its own.");
            return false;
        }
        await Task.Delay(1000);
    }
    return false;
}

void ApplySteps()
{
    foreach (var step in plan.Steps)
    {
        var targetRoot = Path.Combine(plan.InstallDir, step.TargetSubdir);
        foreach (var src in Directory.EnumerateFiles(step.SourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.Combine(step.TargetSubdir, Path.GetRelativePath(step.SourceDir, src));
            var dest = Path.Combine(plan.InstallDir, rel);
            if (File.Exists(dest))
            {
                if (SameContent(src, dest)) continue;
                var backup = Path.Combine(plan.BackupDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(dest, backup, true);
                backedUp.Add(rel);
            }
            else
            {
                created.Add(rel);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (CopyWithRetry(src, dest)) continue;

            // The runtime's dotnet.exe muxer may be in use by this very process; it is version-independent.
            if (Path.GetFileName(dest).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) { log.Write($"Skipped busy {rel}"); continue; }
            // Open in another program (the tray app runs from this folder): Windows won't overwrite a running program's
            // files but lets them be renamed. The program keeps running from the renamed file; the server deletes it
            // on a later start, and the tray app restarts into the new version.
            var aside = AsideName(dest);
            try { File.Move(dest, aside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new IOException($"Could not replace {rel}: {ex.Message}", ex); }
            movedAside.Add((rel, aside));
            if (!CopyWithRetry(src, dest)) throw new IOException($"Could not replace {rel}");
            log.Write($"Replaced in-use {rel} (old copy kept as {Path.GetFileName(aside)})");
        }
        log.Write($"Applied {step.Component} {step.Version} into \\{step.TargetSubdir}");
    }
}

void Restore()
{
    foreach (var rel in created)
    {
        try { File.Delete(Path.Combine(plan.InstallDir, rel)); } catch (Exception ex) { log.Write($"Could not remove {rel}: {ex.Message}"); }
    }
    // Files renamed aside go back under their own name (they are the originals, still in use).
    var putBack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var (rel, aside) in movedAside)
    {
        var dest = Path.Combine(plan.InstallDir, rel);
        try
        {
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(aside, dest);
            putBack.Add(rel);
        }
        catch (Exception ex) { log.Write($"Could not put back {rel}: {ex.Message}"); }
    }
    foreach (var rel in backedUp.Where(r => !putBack.Contains(r)))
    {
        if (!CopyWithRetry(Path.Combine(plan.BackupDir, rel), Path.Combine(plan.InstallDir, rel))) log.Write($"Could not restore {rel}");
    }
    log.Write($"Restored {backedUp.Count} file(s), removed {created.Count} new file(s).");
}

// "syscom-tray.exe" → "syscom-tray.exe.syscom-old" (or ".2.syscom-old", … when an older one is still in use too).
static string AsideName(string path)
{
    for (var i = 1; ; i++)
    {
        var aside = i == 1 ? path + ".syscom-old" : $"{path}.{i}.syscom-old";
        if (!File.Exists(aside)) return aside;
        try { File.Delete(aside); return aside; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* still in use: next name */ }
    }
}

void Finish(bool success, bool rolledBack, string message)
{
    log.Write(message);
    var result = new UpdateResult(DateTimeOffset.Now, success, rolledBack, components, message);
    Directory.CreateDirectory(Path.GetDirectoryName(plan.ResultPath)!);
    File.WriteAllText(plan.ResultPath, JsonSerializer.Serialize(result, SyscomPaths.Json));
}

static bool CopyWithRetry(string src, string dest, int attempts = 6)
{
    for (var i = 0; i < attempts; i++)
    {
        try { File.Copy(src, dest, true); return true; }
        catch (IOException) { Thread.Sleep(500); }
        catch (UnauthorizedAccessException) { Thread.Sleep(500); }
    }
    return false;
}

static bool SameContent(string a, string b)
{
    var fa = new FileInfo(a);
    var fb = new FileInfo(b);
    if (fa.Length != fb.Length) return false;
    return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
}

static void WaitUnlocked(string file, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline && File.Exists(file))
    {
        try { using var _ = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return; }
        catch (IOException) { Thread.Sleep(500); }
        catch (UnauthorizedAccessException) { return; }
    }
}

static string Sc(string verb, string service)
{
    using var p = Process.Start(new ProcessStartInfo("sc.exe", $"{verb} \"{service}\"")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
    })!;
    var output = p.StandardOutput.ReadToEnd();
    p.WaitForExit(30_000);
    return output;
}

sealed class Log(string path)
{
    public void Write(string line)
    {
        var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}";
        Console.WriteLine(text);
        try { File.AppendAllText(path, text + Environment.NewLine); } catch (IOException) { }
    }
}
