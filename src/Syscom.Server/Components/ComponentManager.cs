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

    public UpdateResult? LastUpdate { get; private set; }

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
        LastUpdate = ReadLastResult();
        // An update that was rolled back is not installed automatically again (a newer release is).
        if (LastUpdate is { RolledBack: true } failed)
            foreach (var c in failed.Components) _autoSkip[c] = 0;
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
        var message = await UpdateAsync(targets.Select(c => c.Id).ToList(), ct);
        _log.LogInformation("Automatic update: {Message}", message);
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

    /// <summary>Updates the given components (all with an available update when <paramref name="ids"/> is empty).</summary>
    public async Task<string> UpdateAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        if (!await _busy.WaitAsync(0, ct)) return "An update is already running.";
        try
        {
            if (_state.Values.All(s => s.Latest is null)) await CheckAllAsync(ct);

            var targets = _components.Where(c =>
                    (ids.Count == 0 || ids.Contains(c.Id)) &&
                    c.UpdateSupport().CanUpdate &&
                    _state[c.Id].Latest is { } l && VersionUtil.IsNewer(l.Version, SafeInstalled(c)))
                .ToList();
            if (targets.Count == 0) return "Everything is up to date.";

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

            if (steps.Count > 0 || restart)
            {
                LaunchUpdater(runId, steps);
                return "Installing updates; SYSCOM will restart in a moment.";
            }
            var failed = targets.Where(c => _state[c.Id].Phase == "failed").Select(c => c.Name).ToList();
            return failed.Count == 0 ? "Updated." : $"Failed: {string.Join(", ", failed)}";
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>Restarts the server through the updater (no files changed), e.g. after a driver install.</summary>
    public void Restart() => LaunchUpdater(DateTime.Now.ToString("yyyyMMdd-HHmmss"), Array.Empty<UpdateStep>());

    private void LaunchUpdater(string runId, IReadOnlyList<UpdateStep> steps)
    {
        var appDir = AppContext.BaseDirectory;
        var runDir = Path.Combine(SyscomPaths.UpdatesDir, runId);
        Directory.CreateDirectory(runDir);

        // Run the updater from a copy so it can replace its own files in the install folder.
        var updaterDir = Path.Combine(runDir, "updater");
        Directory.CreateDirectory(updaterDir);
        foreach (var f in Directory.GetFiles(appDir, "syscom-updater*"))
            File.Copy(f, Path.Combine(updaterDir, Path.GetFileName(f)), true);

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
            HealthUrl: $"http://127.0.0.1:{_cfg.Port}/api/health",
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
        _log.LogInformation("Updater started with {Steps} step(s); plan {Plan}", steps.Count, planFile);

        // A console-mode server shuts itself down; a service is stopped by the updater through the SCM.
        if (!asService) _ = Task.Delay(1500).ContinueWith(_ => _lifetime.StopApplication());
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
