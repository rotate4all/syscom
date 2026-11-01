using System.Security.Cryptography;

namespace Syscom.Server.Dashboards;

/// <summary>
/// The dashboard's background picture, one per PC (the browser shrinks it before upload). Stored as
/// background.&lt;ext&gt; next to config.json; its id (a hash of the content) goes in the dashboard's "look", so
/// browsers know when to fetch it again.
/// </summary>
public sealed class BackgroundStore
{
    public const int MaxBytes = 8 * 1024 * 1024;

    private static readonly Dictionary<string, string> Types = new()
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp",
        ["image/gif"] = ".gif",
        ["image/avif"] = ".avif",
    };

    private readonly string _dir;
    private readonly object _lock = new();

    public BackgroundStore(string dataDir) => _dir = dataDir;

    public static bool Accepts(string? contentType) => contentType is not null && Types.ContainsKey(contentType.Split(';')[0].Trim().ToLowerInvariant());

    /// <summary>The stored picture: (file, content type, id), or null when there is none.</summary>
    public (string File, string Type, string Id)? Get()
    {
        lock (_lock)
        {
            foreach (var (type, ext) in Types)
            {
                var file = Path.Combine(_dir, "background" + ext);
                if (File.Exists(file)) return (file, type, IdOf(File.ReadAllBytes(file)));
            }
            return null;
        }
    }

    /// <summary>Replaces the picture; returns its id.</summary>
    public string Save(byte[] bytes, string contentType)
    {
        var type = contentType.Split(';')[0].Trim().ToLowerInvariant();
        if (!Types.TryGetValue(type, out var ext)) throw new ArgumentException("Only JPEG, PNG, WebP, GIF or AVIF pictures.");
        if (!LooksLike(bytes, type)) throw new ArgumentException("That file is not a picture of the type it claims.");
        lock (_lock)
        {
            Delete();
            JsonFile.WriteAtomic(Path.Combine(_dir, "background" + ext), bytes);
            return IdOf(bytes);
        }
    }

    public void Delete()
    {
        lock (_lock)
        {
            foreach (var ext in Types.Values)
            {
                var file = Path.Combine(_dir, "background" + ext);
                if (File.Exists(file)) File.Delete(file);
            }
        }
    }

    private static string IdOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();

    /// <summary>Checks the file signature, so only pictures are stored and served back.</summary>
    private static bool LooksLike(byte[] b, string type) => type switch
    {
        "image/jpeg" => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF,
        "image/png" => b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47,
        "image/gif" => b.Length > 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46,
        "image/webp" => b.Length > 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50,
        "image/avif" => b.Length > 12 && b[4] == 0x66 && b[5] == 0x74 && b[6] == 0x79 && b[7] == 0x70,
        _ => false,
    };
}
