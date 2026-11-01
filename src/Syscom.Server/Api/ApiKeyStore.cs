using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Syscom.Server.Dashboards;

namespace Syscom.Server.Api;

/// <summary>What an API key may do: read everything the dashboard shows, or also change the dashboard.</summary>
public enum KeyAccess { Read, Full }

/// <summary>An API key as listed (never the key itself: only a hash of it is stored).</summary>
public sealed record ApiKeyInfo(string Id, string Name, string Access, string Prefix, long Created, long? LastUsed);

/// <summary>
/// API keys for scripts and other apps, created and revoked by a signed-in user in the dashboard. A key is
/// "syscom_" + 43 random characters (256 bits); only its SHA-256 is stored (apikeys.json next to config.json), so a
/// lost key can't be shown again, only revoked and replaced. Plain SHA-256 is enough for a random 256-bit secret:
/// there is nothing to guess.
/// </summary>
public sealed class ApiKeyStore
{
    public const string KeyPrefix = "syscom_";
    public const int MaxKeys = 50;
    private static readonly TimeSpan SaveLastUsedEvery = TimeSpan.FromMinutes(5);

    private readonly string _path;
    private readonly ILogger _log;
    private readonly object _lock = new();
    private List<Entry> _keys = new();
    private DateTime _lastUsedSaved = DateTime.MinValue;
    private bool _lastUsedDirty;

    private sealed class Entry
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required KeyAccess Access { get; init; }
        public required string Prefix { get; init; }
        public required string Hash { get; init; }
        public required long Created { get; init; }
        public long? LastUsed { get; set; }
    }

    private sealed record Stored(string Id, string Name, string Access, string Prefix, string Hash, long Created, long? LastUsed);

    public ApiKeyStore(string dataDir, ILogger<ApiKeyStore> log)
    {
        _path = Path.Combine(dataDir, "apikeys.json");
        _log = log;
        try
        {
            if (File.Exists(_path))
            {
                var stored = JsonSerializer.Deserialize<List<Stored>>(File.ReadAllBytes(_path), Syscom.Shared.SyscomPaths.Json) ?? new();
                _keys = stored
                    .Where(s => !string.IsNullOrEmpty(s.Id) && s.Hash is { Length: 64 })
                    .Select(s => new Entry
                    {
                        Id = s.Id, Name = s.Name ?? "", Access = ParseAccess(s.Access) ?? KeyAccess.Read, Prefix = s.Prefix ?? "",
                        Hash = s.Hash, Created = s.Created, LastUsed = s.LastUsed,
                    })
                    .ToList();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not read {File}; no API keys until one is created", _path);
        }
    }

    public static KeyAccess? ParseAccess(string? s) => s?.ToLowerInvariant() switch
    {
        "read" => KeyAccess.Read,
        "full" => KeyAccess.Full,
        _ => null,
    };

    private static string Name(KeyAccess a) => a == KeyAccess.Full ? "full" : "read";

    public IReadOnlyList<ApiKeyInfo> List()
    {
        lock (_lock) return _keys.Select(Info).ToList();
    }

    private static ApiKeyInfo Info(Entry e) => new(e.Id, e.Name, Name(e.Access), e.Prefix, e.Created, e.LastUsed);

    /// <summary>Creates a key; the secret is returned this once and never stored.</summary>
    public (ApiKeyInfo? Key, string? Secret, string? Error) Create(string? name, KeyAccess access)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) name = "API key";
        if (name.Length > 60) name = name[..60];
        var secret = KeyPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        lock (_lock)
        {
            if (_keys.Count >= MaxKeys) return (null, null, $"At most {MaxKeys} keys; revoke one first.");
            var entry = new Entry
            {
                Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)),
                Name = name,
                Access = access,
                Prefix = secret[..(KeyPrefix.Length + 4)],
                Hash = HashOf(secret),
                Created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            _keys = [.. _keys, entry];
            Save();
            return (Info(entry), secret, null);
        }
    }

    public bool Revoke(string id)
    {
        lock (_lock)
        {
            var left = _keys.Where(k => k.Id != id).ToList();
            if (left.Count == _keys.Count) return false;
            _keys = left;
            Save();
            return true;
        }
    }

    /// <summary>The access a key gives, or null when it isn't one of ours (or was revoked).</summary>
    public KeyAccess? Validate(string? key)
    {
        if (string.IsNullOrEmpty(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal) || key.Length > 100) return null;
        var hash = HashOf(key);
        var keys = Volatile.Read(ref _keys);
        foreach (var k in keys)
        {
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(k.Hash), Encoding.ASCII.GetBytes(hash))) continue;
            k.LastUsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _lastUsedDirty = true;
            if (DateTime.UtcNow - _lastUsedSaved > SaveLastUsedEvery)
                lock (_lock) { if (_lastUsedDirty) Save(); }
            return k.Access;
        }
        return null;
    }

    private static string HashOf(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>Call with the lock held.</summary>
    private void Save()
    {
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(
                _keys.Select(k => new Stored(k.Id, k.Name, Name(k.Access), k.Prefix, k.Hash, k.Created, k.LastUsed)).ToList(),
                Syscom.Shared.SyscomPaths.Json);
            JsonFile.WriteAtomic(_path, json);
            _lastUsedSaved = DateTime.UtcNow;
            _lastUsedDirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not save {File}", _path);
        }
    }
}
