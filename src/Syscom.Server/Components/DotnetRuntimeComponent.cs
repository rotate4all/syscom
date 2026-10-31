using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Syscom.Shared;

namespace Syscom.Server.Components;

/// <summary>
/// The private .NET runtime in runtime\ next to the executables. Patch releases come from Microsoft's official
/// release metadata and are installed side by side; the apps roll forward to the newest patch on restart.
/// </summary>
public sealed class DotnetRuntimeComponent : Component
{
    private static readonly string[] Feeds =
    {
        "https://builds.dotnet.microsoft.com/dotnet/release-metadata/{0}/releases.json",
        "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/{0}/releases.json",
    };

    private string? _supportPhase;

    public override string Id => "dotnet";
    public override string Name => ".NET runtime";
    public override string Description => "Bundled .NET, ASP.NET Core and Windows Desktop runtime";

    private static string RuntimeDir => Path.Combine(AppDir, "runtime");
    private static bool Bundled => Directory.Exists(Path.Combine(RuntimeDir, "shared"));
    private static string Channel => $"{Environment.Version.Major}.{Environment.Version.Minor}";

    public override string? InstalledVersion() => Environment.Version.ToString(3);

    public override (Health, string) CheckHealth()
    {
        var where = Bundled ? "bundled" : "system-wide";
        return _supportPhase switch
        {
            "eol" => (Health.Error, $".NET {Channel} is out of support; a SYSCOM update is needed"),
            "maintenance" => (Health.Ok, $"{RuntimeInformation.FrameworkDescription} ({where}, maintenance support)"),
            _ => (Health.Ok, $"{RuntimeInformation.FrameworkDescription} ({where})"),
        };
    }

    public override (bool, string?) UpdateSupport() => !OperatingSystem.IsWindows()
        ? (false, "Windows only")
        : Bundled ? (true, null) : (false, "Uses the system-wide .NET; update it with Windows Update or winget");

    public override async Task<LatestRelease?> GetLatestAsync(HttpClient http, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var feed in Feeds)
        {
            try
            {
                var json = await http.GetStringAsync(string.Format(feed, Channel), ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                _supportPhase = root.TryGetProperty("support-phase", out var sp) ? sp.GetString() : null;
                var latest = root.GetProperty("latest-runtime").GetString()!;

                // Find the release entry and its win-x64 zips (ASP.NET Core zip includes the base runtime).
                foreach (var rel in root.GetProperty("releases").EnumerateArray())
                {
                    if (rel.GetProperty("runtime").GetProperty("version").GetString() != latest) continue;
                    var files = new List<(string Url, string Sha512)>();
                    foreach (var section in new[] { "aspnetcore-runtime", "windowsdesktop" })
                    {
                        if (!rel.TryGetProperty(section, out var s) || s.ValueKind != JsonValueKind.Object) continue;
                        foreach (var f in s.GetProperty("files").EnumerateArray())
                        {
                            if (f.GetProperty("rid").GetString() == "win-x64" && f.GetProperty("name").GetString()!.EndsWith(".zip"))
                                files.Add((f.GetProperty("url").GetString()!, f.GetProperty("hash").GetString()!));
                        }
                    }
                    return new LatestRelease(latest, Extra: files);
                }
                return new LatestRelease(latest);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException)
            {
                last = ex;
            }
        }
        throw new InvalidOperationException("Could not read .NET release metadata", last);
    }

    public override async Task<StagedUpdate> StageAsync(LatestRelease release, string staging, HttpClient http, CancellationToken ct)
    {
        if (release.Extra is not List<(string Url, string Sha512)> files || files.Count < 2)
            throw new InvalidOperationException("Release metadata lists no Windows runtime downloads.");

        var target = Path.Combine(staging, "runtime");
        var i = 0;
        foreach (var (url, sha512) in files)
        {
            var zip = await DownloadAsync(http, url, Path.Combine(staging, $"runtime{i++}.zip"), ct);
            VerifyHash(zip, null, sha512);
            ZipFile.ExtractToDirectory(zip, target, overwriteFiles: true);
        }
        return new StagedUpdate(new[] { new UpdateStep(Id, release.Version, target, "runtime") }, NeedsRestart: true);
    }

    /// <summary>Removes runtime versions older than the one running now, keeping one previous version for rollback.</summary>
    public override void OnStartup()
    {
        if (!Bundled) return;
        var running = Environment.Version;
        foreach (var framework in new[] { "Microsoft.NETCore.App", "Microsoft.AspNetCore.App", "Microsoft.WindowsDesktop.App" })
        {
            var dir = Path.Combine(RuntimeDir, "shared", framework);
            if (!Directory.Exists(dir)) continue;
            var old = Directory.GetDirectories(dir)
                .Select(d => (Dir: d, Ver: VersionUtil.Parse(Path.GetFileName(d))))
                .Where(x => x.Ver is not null && x.Ver.Major == running.Major && x.Ver < VersionUtil.Parse(running.ToString(3)))
                .OrderByDescending(x => x.Ver)
                .Skip(1); // keep the newest older version
            foreach (var (d, _) in old)
            {
                try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
