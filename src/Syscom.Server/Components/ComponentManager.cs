using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Syscom.Server.Config;
using Syscom.Server.Sensors;
using Syscom.Shared;

namespace Syscom.Server.Components;

/// <summary>
/// Tracks health and versions of every component, checks their official sources for updates, and applies updates:
/// in-process when possible (web client, PawnIO), otherwise by handing a plan to syscom-updater, which stops the
/// server, swaps files, restarts it and rolls back if it doesn't come back healthy.
/// Checks run every UpdateCheckHours; depending on AutoUpdate, updates found are then installed by themselves
/// (SYSCOM and its web dashboard by default), preferably while no dashboard is open.
/// </summary>
public sealed class ComponentManager : BackgroundService
{
    private readonly IReadOnlyList<Component> _components;
    private readonly ServerConfig _cfg;
    private readonly ILogger<ComponentManager> _log;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly SensorHub _hub;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, State> _state = new();
    private readonly SemaphoreSlim _busy = new(1, 1);
    // "component version" pairs an automatic update must not try again (it failed or was rolled back).
    private readonly ConcurrentDictionary<string, byte> _autoSkip = new();
    private DateTimeOffset? _autoPendingSince;
    private DateTimeOffset _lastCheck = DateTimeOffset.MinValue;

    private sealed class State
    {
        public LatestRelease? Latest;
        public string Phase = "idle";
        public string? Error;
        public DateTimeOffset? Checked;
    }

    public ComponentManager(IEnumerable<Component> components, ServerConfig cfg, ILogger<ComponentManager> log, IHostApplicationLifetime lifetime, SensorHub hub)
    {
        _components = components.ToList();
        _cfg = cfg;
        _log = log;
        _lifetime = lifetime;
        _hub = hub;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"SYSCOM/{typeof(Program).Assembly.GetName().Version?.ToString(3)}");
        foreach (var c in _components) _state[c.Id] = new State();
    }

    /// <summary>
    /// The outcome of the last update or restart. The updater writes it after this server came up (or after rolling
    /// back), so it is re-read whenever the file changes. A failed update is not installed automatically again.
    /// </summary>
    public UpdateResult? LastUpdate
    {
        get
        {
            lock (_lastLock)
            {
                DateTime stamp;
                try { stamp = File.Exists(SyscomPaths.LastUpdateResult) ? File.GetLastWriteTimeUtc(SyscomPaths.LastUpdateResult) : DateTime.MinValue; }
                catch (Exception) { return _lastUpdate; }
                if (stamp == _lastStamp) return _lastUpdate;
                _lastStamp = stamp;
                _lastUpdate = ReadLastResult();
                if (_lastUpdate is { Success: false } failed)
                    foreach (var c in failed.Components) _autoSkip[c] = 0;   // "syscom 0.17.1"
                return _lastUpdate;
            }
        }
    }
    private readonly object _lastLock = new();
    private UpdateResult? _lastUpdate;
    private DateTime _lastStamp = DateTime.MaxValue;

    public IReadOnlyList<ComponentStatus> Status() => _components.Select(c =>
    {
        var s = _state[c.Id];
        var installed = SafeInstalled(c);
        var (health, detail) = SafeHealth(c);
        var (canUpdate, reason) = c.UpdateSupport();
        var updateAvailable = s.Latest is not null && VersionUtil.IsNewer(s.Latest.Version, installed);
        return new ComponentStatus(c.Id, c.Name, c.Description, installed, s.Latest?.Version, health,
            canUpdate ? detail : $"{detail}. Updates: {reason}", updateAvailable, canUpdate, true, s.Phase, s.Error, s.Checked);
    }).ToList();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _ = LastUpdate;   // an update that failed is not installed automatically again (a newer release is)
        CleanUp();
        foreach (var c in _components)
        {
            try { c.OnStartup(); } catch (Exception ex) { _log.LogWarning(ex, "Startup task for {Component} failed", c.Name); }
        }
        if (!_cfg.AutoCheckUpdates) return;

        // First check shortly after start, then every UpdateCheckHours. Every few minutes in between, updates found
        // earlier are installed when the AutoUpdate setting allows it.
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            if (DateTimeOffset.Now - _lastCheck >= TimeSpan.FromHours(_cfg.UpdateCheckHours)) await CheckAllAsync(ct);
            try { await AutoInstallAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Automatic update failed"); }
        } while (await SafeTick(timer, ct));
    }

    /// <summary>Components the AutoUpdate setting lets a check install by itself, with an update waiting.</summary>
    private List<Component> AutoTargets() => _cfg.AutoUpdate == "off"
        ? new()
        : _components.Where(c =>
                (_cfg.AutoUpdate == "all" || c.Id is "syscom" or "web") &&
                c.UpdateSupport().CanUpdate &&
                _state[c.Id].Latest is { } l && VersionUtil.IsNewer(l.Version, SafeInstalled(c)) &&
                !_autoSkip.ContainsKey($"{c.Id} {l.Version}"))
            .ToList();

    private async Task AutoInstallAsync(CancellationToken ct)
    {
        _ = LastUpdate;
        var targets = AutoTargets();
        if (targets.Count == 0) { _autoPendingSince = null; return; }
        _autoPendingSince ??= DateTimeOffset.Now;
        // Most updates restart SYSCOM, which blanks open dashboards for a few seconds: wait for a moment nobody is
        // watching, but not more than a day (a dashboard on a wall display may never close). The web dashboard alone
        // updates in place, without a restart, so it doesn't wait.
        var restarts = targets.Any(c => c.Id != "web");
        if (restarts && _hub.Clients > 0 && DateTimeOffset.Now - _autoPendingSince < TimeSpan.FromHours(24)) return;

        var versions = targets.ToDictionary(c => c.Id, c => _state[c.Id].Latest!.Version);
        _log.LogInformation("Installing updates automatically: {Updates}", string.Join(", ", versions.Select(v => $"{v.Key} {v.Value}")));
        var outcome = await UpdateAsync(targets.Select(c => c.Id).ToList(), ct);
        _log.LogInformation("Automatic update: {Message}", outcome.Message);
        foreach (var (id, version) in versions)
            if (_state[id].Phase == "failed") _autoSkip[$"{id} {version}"] = 0;
        _autoPendingSince = null;
    }

    public async Task CheckAllAsync(CancellationToken ct)
    {
        _lastCheck = DateTimeOffset.Now;
        await Task.WhenAll(_components.Select(async c =>
        {
            var s = _state[c.Id];
            if (s.Phase != "idle" && s.Phase != "failed") return;
            s.Phase = "checking";
            try
            {
                s.Latest = await c.GetLatestAsync(_http, ct);
                s.Error = null;
            }
            catch (Exception ex)
            {
                s.Error = $"Update check failed: {Root(ex).Message}";
                _log.LogInformation("Update check for {Component} failed: {Error}", c.Name, Root(ex).Message);
            }
            finally
            {
                s.Checked = DateTimeOffset.Now;
                s.Phase = "idle";
            }
        }));
    }

    /// <summary>
    /// Updates the given components (all with an available update when <paramref name="ids"/> is empty).
    /// <c>Restarting</c> is true when the updater took over: SYSCOM restarts and records the outcome in LastUpdate.
    /// </summary>
    public async Task<UpdateOutcome> UpdateAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        if (RestartPending) return new("SYSCOM is already restarting to install updates.", true);
        if (!await _busy.WaitAsync(0, ct)) return new("An update is already running.", false);
        try
        {
            if (_state.Values.All(s => s.Latest is null)) await CheckAllAsync(ct);

            var targets = _components.Where(c =>
                    (ids.Count == 0 || ids.Contains(c.Id)) &&
                    c.UpdateSupport().CanUpdate &&
                    _state[c.Id].Latest is { } l && VersionUtil.IsNewer(l.Version, SafeInstalled(c)))
                .ToList();
            if (targets.Count == 0) return new("Everything is up to date.", false);

            var steps = new List<UpdateStep>();
            var restart = false;
            var runId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            foreach (var c in targets)
            {
                var s = _state[c.Id];
                s.Phase = "downloading";
                s.Error = null;
                try
                {
                    var staging = Path.Combine(SyscomPaths.UpdatesDir, runId, c.Id);
                    if (Directory.Exists(staging)) Directory.Delete(staging, true);
                    Directory.CreateDirectory(staging);
                    var staged = await c.StageAsync(s.Latest!, staging, _http, ct);
                    steps.AddRange(staged.Steps);
                    restart |= staged.NeedsRestart;
                    s.Phase = staged.Steps.Count > 0 || staged.NeedsRestart ? "restarting" : "idle";
                    _log.LogInformation("{Component} {Version} staged", c.Name, s.Latest!.Version);
                }
                catch (Exception ex)
                {
                    s.Phase = "failed";
                    s.Error = Root(ex).Message;
                    _log.LogWarning(ex, "Updating {Component} failed", c.Name);
                }
            }

            var failed = targets.Where(c => _state[c.Id].Phase == "failed").Select(c => c.Name).ToList();
            if (steps.Count > 0 || restart)
            {
                LaunchUpdater(runId, steps);
                return new(failed.Count == 0
                    ? "Installing updates; SYSCOM restarts in a moment."
                    : $"Installing the other updates; SYSCOM restarts in a moment. Failed: {string.Join(", ", failed)}", true);
            }
            return new(failed.Count == 0 ? "Updated." : $"Failed: {string.Join(", ", failed)}", false);
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>Restarts the server through the updater (no files changed), e.g. after a driver install.</summary>
    public UpdateOutcome Restart()
    {
        if (RestartPending) return new("SYSCOM is already restarting.", true);
        LaunchUpdater(DateTime.Now.ToString("yyyyMMdd-HHmmss"), Array.Empty<UpdateStep>());
        return new("Restarting.", true);
    }

    // Set once the updater is started: it stops this server shortly. Expires in case the updater never got going.
    private DateTimeOffset _restartingSince = DateTimeOffset.MinValue;
    private bool RestartPending => DateTimeOffset.Now - _restartingSince < TimeSpan.FromMinutes(3);

    private void LaunchUpdater(string runId, IReadOnlyList<UpdateStep> steps)
    {
        var appDir = AppContext.BaseDirectory;
        var runDir = Path.Combine(SyscomPaths.UpdatesDir, runId);
        Directory.CreateDirectory(runDir);

        // Run the updater from a copy so it can replace its own files in the install folder. When SYSCOM itself is
        // being updated, the new version's updater does the job (it knows how to install that version).
        var updaterDir = Path.Combine(runDir, "updater");
        Directory.CreateDirectory(updaterDir);
        var updaterSource = steps.FirstOrDefault(s => s.Component == "syscom" && UpdaterRunsHere(s.SourceDir, appDir))?.SourceDir ?? appDir;
        foreach (var f in Directory.GetFiles(updaterSource, "syscom-updater*"))
            File.Copy(f, Path.Combine(updaterDir, Path.GetFileName(f)), true);
        _log.LogInformation("Using the updater from {Dir}", updaterSource);

        var asService = WindowsServiceHelpers.IsService();
        var exe = Environment.ProcessPath ?? Path.Combine(appDir, "syscom.exe");
        var serverArgs = Environment.GetCommandLineArgs().Skip(1).ToList();
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            serverArgs.Insert(0, typeof(Program).Assembly.Location); // started as `dotnet syscom.dll`
        var plan = new UpdatePlan(
            InstallDir: appDir,
            ServiceName: asService ? SyscomPaths.ServiceName : "",
            ServerPid: asService ? null : Environment.ProcessId,
            ServerExe: exe,
            ServerArgs: asService ? Array.Empty<string>() : serverArgs,
            // Healthy once the sensors are read, so an update whose sensor library hangs is rolled back.
            HealthUrl: $"http://127.0.0.1:{_cfg.Port}/api/health?ready=true",
            BackupDir: Path.Combine(runDir, "backup"),
            ResultPath: SyscomPaths.LastUpdateResult,
            Steps: steps);
        var planFile = Path.Combine(runDir, "plan.json");
        File.WriteAllText(planFile, JsonSerializer.Serialize(plan, SyscomPaths.Json));

        // The updater is framework-dependent: start it on the bundled runtime when there is one.
        var bundledDotnet = Path.Combine(appDir, "runtime", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var dotnet = File.Exists(bundledDotnet) ? bundledDotnet : "dotnet";
        var psi = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = updaterDir };
        psi.ArgumentList.Add(Path.Combine(updaterDir, "syscom-updater.dll"));
        psi.ArgumentList.Add("--plan");
        psi.ArgumentList.Add(planFile);
        Process.Start(psi);
        _restartingSince = DateTimeOffset.Now;
        _log.LogInformation("Updater started with {Steps} step(s); plan {Plan}", steps.Count, planFile);

        // A console-mode server shuts itself down; a service is stopped by the updater through the SCM.
        if (!asService) _ = Task.Delay(1500).ContinueWith(_ => _lifetime.StopApplication());
    }

    /// <summary>
    /// True when the updater in <paramref name="dir"/> can run on the .NET runtime bundled in <paramref name="appDir"/>
    /// (a new SYSCOM may need a newer runtime than the one installed; then the installed updater does the job).
    /// </summary>
    private bool UpdaterRunsHere(string dir, string appDir)
    {
        try
        {
            if (!File.Exists(Path.Combine(dir, "syscom-updater.dll"))) return false;
            var config = Path.Combine(dir, "syscom-updater.runtimeconfig.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(config));
            var opts = doc.RootElement.GetProperty("runtimeOptions");
            var needed = (opts.TryGetProperty("framework", out var fw) ? fw : opts.GetProperty("frameworks")[0]).GetProperty("version").GetString();
            if (!Version.TryParse(needed, out var want)) return false;
            var shared = Path.Combine(appDir, "runtime", "shared", "Microsoft.NETCore.App");
            if (!Directory.Exists(shared)) return true; // no bundled runtime: the machine-wide one runs both
            return Directory.GetDirectories(shared).Any(d =>
                Version.TryParse(Path.GetFileName(d).Split('-')[0], out var have) && have.Major == want.Major && have >= want);
        }
        catch (Exception ex)
        {
            _log.LogInformation("Not using the new updater: {Error}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Removes what earlier updates left behind: program files renamed aside because they were in use (the tray app's,
    /// typically) and all but the newest update folders.
    /// </summary>
    private void CleanUp()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.syscom-old", SearchOption.AllDirectories))
            {
                try { File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* still in use */ }
            }
            if (!Directory.Exists(SyscomPaths.UpdatesDir)) return;
            var runs = Directory.GetDirectories(SyscomPaths.UpdatesDir)
                .Where(d => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(d), @"^\d{8}-\d{6}$"))
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal);
            foreach (var dir in runs.Skip(5))
            {
                try { Directory.Delete(dir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex)
        {
            _log.LogInformation("Cleaning up after updates failed: {Error}", ex.Message);
        }
    }

    private static UpdateResult? ReadLastResult()
    {
        try
        {
            return File.Exists(SyscomPaths.LastUpdateResult)
                ? JsonSerializer.Deserialize<UpdateResult>(File.ReadAllText(SyscomPaths.LastUpdateResult), SyscomPaths.Json)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? SafeInstalled(Component c)
    {
        try { return c.InstalledVersion(); } catch { return null; }
    }

    private static (Health, string) SafeHealth(Component c)
    {
        try { return c.CheckHealth(); } catch (Exception ex) { return (Health.Error, ex.Message); }
    }

    private static Exception Root(Exception ex) => ex is AggregateException { InnerException: { } i } ? Root(i) : ex.InnerException is HttpRequestException h ? h : ex;

    private static async Task<bool> SafeTick(PeriodicTimer t, CancellationToken ct)
    {
        try { return await t.WaitForNextTickAsync(ct); } catch (OperationCanceledException) { return false; }
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}

/// <summary>Detects whether we're running under the Windows Service Control Manager.</summary>
public static class WindowsServiceHelpers
{
    public static bool IsService()
    {
#if WINDOWS
        return Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();
#else
        return false;
#endif
    }
}
