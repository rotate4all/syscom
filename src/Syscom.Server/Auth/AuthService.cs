using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Syscom.Server.Config;

namespace Syscom.Server.Auth;

/// <summary>
/// Password login that issues stateless, HMAC-signed bearer tokens. Tokens survive server restarts and
/// are all revoked when the password changes (the signing secret is rotated with it).
/// </summary>
public sealed class AuthService
{
    private const int Iterations = 210_000;
    private const int MaxFailures = 5;

    private readonly ServerConfig _cfg;
    private readonly ConcurrentDictionary<IPAddress, (int Failures, DateTime LockedUntil)> _attempts = new();

    public AuthService(ServerConfig cfg) => _cfg = cfg;

    public bool HasPassword => !string.IsNullOrEmpty(_cfg.PasswordHash);

    public static void SetPassword(ServerConfig cfg, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        cfg.PasswordSalt = Convert.ToBase64String(salt);
        cfg.PasswordHash = Convert.ToBase64String(Hash(password, salt));
        cfg.TokenSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    public static string GeneratePassword()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        return string.Create(10, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        });
    }

    /// <summary>Returns a token on success, null on bad password; sets <paramref name="retryAfter"/> when locked out.</summary>
    public string? Login(IPAddress? remote, string? password, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        remote ??= IPAddress.None;
        var now = DateTime.UtcNow;

        if (_attempts.TryGetValue(remote, out var state) && state.LockedUntil > now)
        {
            retryAfter = state.LockedUntil - now;
            return null;
        }

        if (!string.IsNullOrEmpty(password) && VerifyPassword(password))
        {
            _attempts.TryRemove(remote, out _);
            return IssueToken();
        }

        var failures = state.Failures + 1;
        var lockedUntil = failures >= MaxFailures
            ? now.AddSeconds(Math.Min(300, 15 * Math.Pow(2, failures - MaxFailures)))
            : DateTime.MinValue;
        _attempts[remote] = (failures, lockedUntil);
        return null;
    }

    public bool ValidateToken(string? token)
    {
        if (string.IsNullOrEmpty(token) || _cfg.TokenSecret is null) return false;
        var dot = token.LastIndexOf('.');
        if (dot <= 0) return false;

        byte[] payload, sig;
        try
        {
            payload = Base64UrlDecode(token[..dot]);
            sig = Base64UrlDecode(token[(dot + 1)..]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(Sign(payload), sig)) return false;

        var parts = Encoding.ASCII.GetString(payload).Split('.');
        return parts.Length == 2
               && long.TryParse(parts[0], out var exp)
               && DateTimeOffset.UtcNow.ToUnixTimeSeconds() < exp;
    }

    /// <summary>A fresh token for a browser that is already signed in, so a session lasts as long as it's used
    /// (TokenLifetimeDays after the last renewal) instead of expiring mid-use.</summary>
    public string Renew() => IssueToken();

    private bool VerifyPassword(string password)
    {
        if (_cfg.PasswordHash is null || _cfg.PasswordSalt is null) return false;
        var expected = Convert.FromBase64String(_cfg.PasswordHash);
        var actual = Hash(password, Convert.FromBase64String(_cfg.PasswordSalt));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private string IssueToken()
    {
        var exp = DateTimeOffset.UtcNow.AddDays(_cfg.TokenLifetimeDays).ToUnixTimeSeconds();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        var payload = Encoding.ASCII.GetBytes($"{exp}.{nonce}");
        return Base64UrlEncode(payload) + "." + Base64UrlEncode(Sign(payload));
    }

    private byte[] Sign(byte[] payload) => HMACSHA256.HashData(Convert.FromBase64String(_cfg.TokenSecret!), payload);

    private static byte[] Hash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
