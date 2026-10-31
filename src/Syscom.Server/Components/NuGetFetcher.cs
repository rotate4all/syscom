using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace Syscom.Server.Components;

/// <summary>
/// Minimal NuGet client: finds the newest stable version of a package and extracts the Windows x64 assemblies of
/// the package and its dependencies (skipping anything the .NET shared framework already provides).
/// Every package is verified against the SHA-512 hash recorded in nuget.org's catalog.
/// </summary>
public sealed class NuGetFetcher
{
    private const string FlatContainer = "https://api.nuget.org/v3-flatcontainer";
    private const string Registration = "https://api.nuget.org/v3/registration5-semver1";

    private static readonly string[] TfmPreference =
        { "net10.0", "net9.0", "net8.0", "net7.0", "net6.0", "net5.0", "netcoreapp3.1", "netstandard2.1", "netstandard2.0" };

    private readonly HttpClient _http;
    public NuGetFetcher(HttpClient http) => _http = http;

    public async Task<string?> LatestStableAsync(string id, CancellationToken ct)
    {
        var json = await _http.GetStringAsync($"{FlatContainer}/{id.ToLowerInvariant()}/index.json", ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("versions").EnumerateArray()
            .Select(v => v.GetString()!)
            .Where(v => !VersionUtil.IsPrerelease(v))
            .OrderBy(VersionUtil.Parse)
            .LastOrDefault();
    }

    /// <summary>Downloads <paramref name="id"/>@<paramref name="version"/> plus dependencies; writes DLLs into <paramref name="outDir"/>.</summary>
    public async Task<IReadOnlyList<string>> ExtractWithDependenciesAsync(string id, string version, string outDir, string workDir, CancellationToken ct)
    {
        Directory.CreateDirectory(outDir);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extracted = new List<string>();
        var queue = new Queue<(string Id, string Version)>();
        queue.Enqueue((id, version));

        while (queue.Count > 0)
        {
            var (pid, pver) = queue.Dequeue();
            if (!done.Add(pid) || ProvidedBySharedFramework(pid)) continue;

            var nupkg = Path.Combine(workDir, $"{pid}.{pver}.nupkg".ToLowerInvariant());
            await DownloadVerifiedAsync(pid, pver, nupkg, ct);

            using var zip = ZipFile.OpenRead(nupkg);
            extracted.AddRange(ExtractAssets(zip, outDir));
            foreach (var dep in Dependencies(zip)) queue.Enqueue(dep);
        }
        return extracted;
    }

    private async Task DownloadVerifiedAsync(string id, string version, string target, CancellationToken ct)
    {
        var lid = id.ToLowerInvariant();
        var lver = version.ToLowerInvariant();
        var bytes = await _http.GetByteArrayAsync($"{FlatContainer}/{lid}/{lver}/{lid}.{lver}.nupkg", ct);

        // registration leaf → catalog entry → packageHash (base64 SHA-512)
        using var leaf = JsonDocument.Parse(await _http.GetStringAsync($"{Registration}/{lid}/{lver}.json", ct));
        var catalogUrl = leaf.RootElement.GetProperty("catalogEntry").GetString()!;
        using var catalog = JsonDocument.Parse(await _http.GetStringAsync(catalogUrl, ct));
        var expected = catalog.RootElement.GetProperty("packageHash").GetString()!;
        var actual = Convert.ToBase64String(SHA512.HashData(bytes));
        if (actual != expected) throw new InvalidOperationException($"Checksum mismatch for NuGet package {id} {version}.");

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target, bytes, ct);
    }

    private static IEnumerable<string> ExtractAssets(ZipArchive zip, string outDir)
    {
        var entries = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
        foreach (var tfm in TfmPreference)
        {
            foreach (var prefix in new[] { $"runtimes/win-x64/lib/{tfm}/", $"runtimes/win/lib/{tfm}/", $"lib/{tfm}/" })
            {
                var dlls = entries.Where(e => e.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                              && e.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                                              && e.LastIndexOf('/') == prefix.Length - 1).ToList();
                if (dlls.Count == 0) continue;

                var natives = entries.Where(e => e.StartsWith("runtimes/win-x64/native/", StringComparison.OrdinalIgnoreCase)
                                                 && e.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
                var result = new List<string>();
                foreach (var name in dlls.Concat(natives))
                {
                    var entry = zip.GetEntry(name) ?? zip.Entries.First(e => e.FullName.Replace('\\', '/') == name);
                    var dest = Path.Combine(outDir, Path.GetFileName(name));
                    entry.ExtractToFile(dest, overwrite: true);
                    result.Add(Path.GetFileName(name));
                }
                return result;
            }
        }
        return Array.Empty<string>();
    }

    private static IEnumerable<(string Id, string Version)> Dependencies(ZipArchive zip)
    {
        var nuspec = zip.Entries.First(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        using var s = nuspec.Open();
        var doc = XDocument.Load(s);
        XNamespace ns = doc.Root!.Name.Namespace;
        var groups = doc.Descendants(ns + "dependencies").Elements(ns + "group")
            .Select(g => (Tfm: NormalizeTfm((string?)g.Attribute("targetFramework")), Group: g))
            .Where(g => g.Tfm is not null)
            .ToList();

        foreach (var tfm in TfmPreference)
        {
            var match = groups.FirstOrDefault(g => g.Tfm == tfm);
            if (match.Group is null) continue;
            foreach (var d in match.Group.Elements(ns + "dependency"))
            {
                var range = ((string?)d.Attribute("version") ?? "0.0.0").Trim('[', ']', '(', ')', ' ');
                yield return ((string)d.Attribute("id")!, range.Split(',')[0].Trim());
            }
            yield break;
        }
    }

    private static string? NormalizeTfm(string? tfm)
    {
        if (string.IsNullOrEmpty(tfm)) return null;
        var t = tfm.ToLowerInvariant();
        if (t.StartsWith(".netstandard")) return "netstandard" + t[".netstandard".Length..];
        if (t.StartsWith(".netcoreapp")) return "netcoreapp" + t[".netcoreapp".Length..];
        if (t.StartsWith(".netframework") || t.StartsWith("net4")) return null;
        return t;
    }

    /// <summary>True when the running shared framework already ships an assembly with the package's name.</summary>
    private static bool ProvidedBySharedFramework(string id)
    {
        var dir = RuntimeEnvironment.GetRuntimeDirectory();
        if (File.Exists(Path.Combine(dir, id + ".dll"))) return true;
        var asp = Path.Combine(Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar))!, "..", "Microsoft.AspNetCore.App",
            Environment.Version.ToString(3), id + ".dll");
        return File.Exists(asp);
    }
}
