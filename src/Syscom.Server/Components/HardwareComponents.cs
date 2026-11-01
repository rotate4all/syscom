using System.Diagnostics;
using System.Text.Json;
using Syscom.Server.Config;
using Syscom.Server.Sensors;
using Syscom.Shared;

namespace Syscom.Server.Components;

/// <summary>LibreHardwareMonitorLib and its own dependencies, from nuget.org.</summary>
public sealed class LhmComponent : Component
{
    private const string PackageId = "LibreHardwareMonitorLib";
    private readonly SensorHub _hub;

    public LhmComponent(SensorHub hub) => _hub = hub;

    public override string Id => "lhm";
    public override string Name => "LibreHardwareMonitor";
    public override string Description => "Hardware sensor library (CPU, GPU, RAM, disks, fans, network)";

    private static string DllPath => Path.Combine(AppDir, PackageId + ".dll");

    public override string? InstalledVersion()
    {
        if (!File.Exists(DllPath)) return null;
        var info = FileVersionInfo.GetVersionInfo(DllPath);
        return (info.ProductVersion ?? info.FileVersion)?.Split('+')[0];
    }

    public override (Health, string) CheckHealth()
    {
        if (!OperatingSystem.IsWindows()) return (Health.Unknown, "Windows only (mock sensors in use)");
        if (!File.Exists(DllPath)) return (Health.Error, "Library missing; no hardware sensors");
        if (_hub.SourceError("LibreHardwareMonitor") is { } err) return (Health.Error, err);
        if (!_hub.Ready) return (Health.Unknown, "Finding sensors…");
        var count = _hub.Catalog.Count(s => !s.Id.StartsWith("/drive/"));
        return count > 0 ? (Health.Ok, $"{count} sensors") : (Health.Warning, "Loaded, but no sensors found");
    }

    public override (bool, string?) UpdateSupport() => OperatingSystem.IsWindows() ? (true, null) : (false, "Windows only");

    public override async Task<LatestRelease?> GetLatestAsync(HttpClient http, CancellationToken ct)
    {
        var v = await new NuGetFetcher(http).LatestStableAsync(PackageId, ct);
        return v is null ? null : new LatestRelease(v);
    }

    public override async Task<StagedUpdate> StageAsync(LatestRelease release, string staging, HttpClient http, CancellationToken ct)
    {
        var files = Path.Combine(staging, "files");
        var extracted = await new NuGetFetcher(http).ExtractWithDependenciesAsync(PackageId, release.Version, files, Path.Combine(staging, "packages"), ct);
        if (!extracted.Any(f => f.Equals(PackageId + ".dll", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The package contained no Windows x64 library.");
        return new StagedUpdate(new[] { new UpdateStep(Id, release.Version, files, "") }, NeedsRestart: true);
    }
}

/// <summary>The PawnIO kernel driver LibreHardwareMonitor uses for low-level sensors. Installed from its official releases.</summary>
public sealed class PawnIoComponent : Component
{
    private const string ReleasesApi = "https://api.github.com/repos/namazso/PawnIO.Setup/releases/latest";
    private readonly ServerConfig _cfg;

    public PawnIoComponent(ServerConfig cfg) => _cfg = cfg;

    public override string Id => "pawnio";
    public override string Name => "PawnIO driver";
    public override string Description => "Kernel driver for CPU temperatures and motherboard sensors";

    public override string? InstalledVersion() => PawnIoCheck.InstalledVersion();

    public override (Health, string) CheckHealth()
    {
        if (!OperatingSystem.IsWindows()) return (Health.Unknown, "Windows only");
        var installed = InstalledVersion();
        if (installed is null) return (Health.Warning, "Not installed: CPU temperatures and motherboard sensors are missing");
        if (VersionUtil.IsNewer(_cfg.MinPawnIoVersion, installed))
            return (Health.Error, $"Older than {_cfg.MinPawnIoVersion}, which has known security issues. Update now.");
        return (Health.Ok, "Installed");
    }

    public override (bool, string?) UpdateSupport() => OperatingSystem.IsWindows() ? (true, null) : (false, "Windows only");

    public override async Task<LatestRelease?> GetLatestAsync(HttpClient http, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var version = root.GetProperty("tag_name").GetString()!.TrimStart('v');
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString()!;
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !name.Contains("setup", StringComparison.OrdinalIgnoreCase)) continue;
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null; // "sha256:…"
            var sha256 = digest is not null && digest.StartsWith("sha256:") ? digest["sha256:".Length..] : null;
            return new LatestRelease(version, asset.GetProperty("browser_download_url").GetString(), sha256);
        }
        return new LatestRelease(version);
    }

    public override async Task<StagedUpdate> StageAsync(LatestRelease release, string staging, HttpClient http, CancellationToken ct)
    {
        if (release.Url is null) throw new InvalidOperationException("The PawnIO release has no installer.");
        var setup = await DownloadAsync(http, release.Url, Path.Combine(staging, "PawnIO_setup.exe"), ct);
        VerifyHash(setup, release.Sha256, null);

        using var p = Process.Start(new ProcessStartInfo(setup, _cfg.PawnIoInstallArgs) { UseShellExecute = false, CreateNoWindow = true })!;
        await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(5), ct);
        if (p.ExitCode != 0) throw new InvalidOperationException($"PawnIO setup failed (exit code {p.ExitCode}).");

        // LibreHardwareMonitor binds to the driver at startup, so restart to pick up the new one.
        return new StagedUpdate(Array.Empty<UpdateStep>(), NeedsRestart: true);
    }
}
