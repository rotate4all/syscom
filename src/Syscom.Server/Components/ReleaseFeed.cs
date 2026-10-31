using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Syscom.Server.Config;
using Syscom.Server.Web;
using Syscom.Shared;

namespace Syscom.Server.Components;

/// <summary>
/// SYSCOM's own release feed (feed.json published with each GitHub release, see docs/UPDATES.md):
/// <code>{ "syscom": { "version", "url", "sha256" }, "web": { "version", "url", "sha256" } }</code>
/// </summary>
public sealed class ReleaseFeed
{
    private readonly ServerConfig _cfg;
    private JsonDocument? _cached;
    private DateTime _fetched;

    public ReleaseFeed(ServerConfig cfg) => _cfg = cfg;

    public bool Configured => !string.IsNullOrWhiteSpace(_cfg.UpdateFeedUrl);

    public async Task<LatestRelease?> GetAsync(HttpClient http, string key, CancellationToken ct)
    {
        if (!Configured) return null;
        if (_cached is null || DateTime.UtcNow - _fetched > TimeSpan.FromMinutes(5))
        {
            var json = await http.GetStringAsync(_cfg.UpdateFeedUrl, ct);
            _cached = JsonDocument.Parse(json);
            _fetched = DateTime.UtcNow;
        }
        if (!_cached.RootElement.TryGetProperty(key, out var e)) return null;
        return new LatestRelease(
            e.GetProperty("version").GetString()!,
            e.GetProperty("url").GetString(),
            e.TryGetProperty("sha256", out var h) ? h.GetString() : null);
    }
}

/// <summary>The server, updater and tray app executables.</summary>
public sealed class SyscomAppComponent : Component
{
    private readonly ReleaseFeed _feed;
    public SyscomAppComponent(ReleaseFeed feed) => _feed = feed;

    public override string Id => "syscom";
    public override string Name => "SYSCOM";
    public override string Description => "Server, updater and tray app";

    public override string? InstalledVersion() =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];

    public override (Health, string) CheckHealth() => (Health.Ok, "Running");

    public override (bool, string?) UpdateSupport() =>
        _feed.Configured ? (true, null) : (false, "No update feed configured");

    public override Task<LatestRelease?> GetLatestAsync(HttpClient http, CancellationToken ct) => _feed.GetAsync(http, "syscom", ct);

    public override async Task<StagedUpdate> StageAsync(LatestRelease release, string staging, HttpClient http, CancellationToken ct)
    {
        var zip = await DownloadAsync(http, release.Url!, Path.Combine(staging, "syscom.zip"), ct);
        VerifyHash(zip, release.Sha256, null);
        var files = Path.Combine(staging, "files");
        ZipFile.ExtractToDirectory(zip, files, overwriteFiles: true);
        return new StagedUpdate(new[] { new UpdateStep(Id, release.Version, files, "") }, NeedsRestart: true);
    }
}

/// <summary>The browser dashboard, served from the web\ folder. Updated in place without a restart.</summary>
public sealed class WebClientComponent : Component
{
    private readonly ReleaseFeed _feed;
    public WebClientComponent(ReleaseFeed feed) => _feed = feed;

    public override string Id => "web";
    public override string Name => "Web dashboard";
    public override string Description => "Browser client served to phones and PCs";

    private static string WebDir => Path.Combine(AppDir, "web");

    public override string? InstalledVersion()
    {
        try
        {
            var file = Path.Combine(WebDir, "version.json");
            var json = File.Exists(file) ? File.ReadAllText(file) : WebClient.EmbeddedText("version.json");
            return json is null ? null : JsonDocument.Parse(json).RootElement.GetProperty("version").GetString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public override (Health, string) CheckHealth()
    {
        if (File.Exists(Path.Combine(WebDir, "index.html"))) return (Health.Ok, "Serving web\\ folder");
        return WebClient.EmbeddedText("index.html") is not null
            ? (Health.Warning, "web\\ folder missing; serving built-in copy")
            : (Health.Error, "No web client found");
    }

    public override (bool, string?) UpdateSupport() =>
        _feed.Configured ? (true, null) : (false, "No update feed configured");

    public override Task<LatestRelease?> GetLatestAsync(HttpClient http, CancellationToken ct) => _feed.GetAsync(http, "web", ct);

    public override async Task<StagedUpdate> StageAsync(LatestRelease release, string staging, HttpClient http, CancellationToken ct)
    {
        var zip = await DownloadAsync(http, release.Url!, Path.Combine(staging, "web.zip"), ct);
        VerifyHash(zip, release.Sha256, null);
        var fresh = Path.Combine(staging, "web");
        ZipFile.ExtractToDirectory(zip, fresh, overwriteFiles: true);
        if (!File.Exists(Path.Combine(fresh, "index.html"))) throw new InvalidOperationException("Web package has no index.html.");

        // Swap folders; keep the previous one until the swap succeeded.
        var backup = WebDir + ".previous";
        if (Directory.Exists(backup)) Directory.Delete(backup, true);
        if (Directory.Exists(WebDir)) Directory.Move(WebDir, backup);
        try
        {
            CopyDir(fresh, WebDir);
        }
        catch
        {
            if (Directory.Exists(WebDir)) Directory.Delete(WebDir, true);
            if (Directory.Exists(backup)) Directory.Move(backup, WebDir);
            throw;
        }
        return new StagedUpdate(Array.Empty<UpdateStep>(), NeedsRestart: false);
    }

    private static void CopyDir(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), true);
    }
}
