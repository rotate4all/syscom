using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Syscom.Server.Web;

/// <summary>
/// Serves the browser dashboard. Files come from the web\ folder next to the executable (so the dashboard can be
/// updated on its own, without a restart); a copy embedded in the executable is the fallback.
/// </summary>
public static class WebClient
{
    private sealed record Asset(byte[] Content, string ContentType, string ETag, DateTime Stamp);

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json",
        [".webmanifest"] = "application/manifest+json",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".ico"] = "image/x-icon",
    };

    private static readonly Lazy<Dictionary<string, Asset>> Embedded = new(LoadEmbedded);
    private static readonly ConcurrentDictionary<string, Asset> DiskCache = new(StringComparer.OrdinalIgnoreCase);

    public static string WebDir => Path.Combine(AppContext.BaseDirectory, "web");

    public static string? EmbeddedText(string path) =>
        Embedded.Value.TryGetValue(path, out var a) ? Encoding.UTF8.GetString(a.Content) : null;

    public static void MapWebClient(this WebApplication app)
    {
        app.MapGet("/{**path}", (HttpContext ctx, string? path) =>
        {
            path = string.IsNullOrEmpty(path) ? "index.html" : path.Replace('\\', '/');
            var asset = Find(path);
            if (asset is null && !Path.HasExtension(path)) asset = Find(path + ".html") ?? Find("index.html"); // /trust → trust.html, else app shell
            if (asset is null) return Results.NotFound();

            if (ctx.Request.Headers.IfNoneMatch == asset.ETag) return Results.StatusCode(StatusCodes.Status304NotModified);
            ctx.Response.Headers.ETag = asset.ETag;
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Bytes(asset.Content, asset.ContentType);
        });
    }

    private static Asset? Find(string path)
    {
        if (path.Contains("..")) return null;
        var file = Path.GetFullPath(Path.Combine(WebDir, path));
        if (file.StartsWith(Path.GetFullPath(WebDir), StringComparison.OrdinalIgnoreCase) && File.Exists(file))
        {
            var stamp = File.GetLastWriteTimeUtc(file);
            if (DiskCache.TryGetValue(file, out var cached) && cached.Stamp == stamp) return cached;
            try
            {
                var asset = Create(File.ReadAllBytes(file), file, stamp);
                DiskCache[file] = asset;
                return asset;
            }
            catch (IOException)
            {
                // mid-update; fall through to the embedded copy
            }
        }
        return Embedded.Value.GetValueOrDefault(path);
    }

    private static Asset Create(byte[] bytes, string name, DateTime stamp) => new(
        bytes,
        Types.GetValueOrDefault(Path.GetExtension(name), "application/octet-stream"),
        "\"" + Convert.ToHexString(SHA256.HashData(bytes))[..16] + "\"",
        stamp);

    private static Dictionary<string, Asset> LoadEmbedded()
    {
        var asm = Assembly.GetExecutingAssembly();
        var result = new Dictionary<string, Asset>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in asm.GetManifestResourceNames())
        {
            var normalized = name.Replace('\\', '/');
            if (!normalized.StartsWith("wwwroot/")) continue;
            using var stream = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            result[normalized["wwwroot/".Length..]] = Create(ms.ToArray(), normalized, DateTime.MinValue);
        }
        return result;
    }
}
