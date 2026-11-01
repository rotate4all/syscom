using Syscom.Server.Auth;

namespace Syscom.Server.Api;

/// <summary>Who is calling: a signed-in dashboard (password login), an API key, or nobody.</summary>
public enum Caller { None, ReadKey, FullKey, Session }

/// <summary>
/// Sorts out credentials on every request. A dashboard sends its session token (Authorization: Bearer …); scripts
/// send an API key, as "Authorization: Bearer syscom_…", "X-API-Key: syscom_…", or (GET requests and WebSockets only,
/// where headers are awkward) "?key=syscom_…". Read keys may read everything a dashboard shows; full keys may also
/// change the dashboard and the device list. Creating and revoking keys, and renewing a session, take a session.
/// </summary>
public sealed class ApiAccess(AuthService auth, ApiKeyStore keys)
{
    public const string KeyHeader = "X-API-Key";

    public Caller Identify(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        var bearer = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        var key = ctx.Request.Headers[KeyHeader].ToString();
        if (key.Length == 0 && bearer?.StartsWith(ApiKeyStore.KeyPrefix, StringComparison.Ordinal) == true) key = bearer;
        if (key.Length == 0 && (HttpMethods.IsGet(ctx.Request.Method) || ctx.WebSockets.IsWebSocketRequest)) key = ctx.Request.Query["key"].ToString();
        if (key.Length > 0) return keys.Validate(key) switch { KeyAccess.Full => Caller.FullKey, KeyAccess.Read => Caller.ReadKey, _ => Caller.None };
        if (bearer is null && ctx.WebSockets.IsWebSocketRequest) bearer = ctx.Request.Query["token"].ToString();
        return auth.ValidateToken(bearer) ? Caller.Session : Caller.None;
    }

    public bool CanRead(HttpContext ctx) => Identify(ctx) != Caller.None;

    public bool CanWrite(HttpContext ctx) => Identify(ctx) is Caller.FullKey or Caller.Session;

    public bool IsSession(HttpContext ctx) => Identify(ctx) == Caller.Session;
}
