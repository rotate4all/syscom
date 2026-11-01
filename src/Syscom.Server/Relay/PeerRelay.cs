using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Net.WebSockets;
using Syscom.Server.Dashboards;
using Syscom.Server.Discovery;

namespace Syscom.Server.Relay;

/// <summary>
/// Lets a dashboard reach the other SYSCOM PCs through the PC that served it: <c>/relay/{target}/{path}</c> forwards
/// to that PC's plain HTTP port on the LAN, WebSockets included.
///
/// Why: a dashboard opened over HTTPS (needed to install it as a phone app) may only talk HTTPS, and every PC has
/// its own certificate authority. Without the relay a phone has to trust each PC's certificate before it can add
/// it; with it, trusting the first PC is enough.
///
/// Safety: only SYSCOM's own sign-in, data and dashboard endpoints are forwarded (never /api/admin); the target is a
/// server id this PC knows (LAN discovery or the device list) or a private-network IPv4 address that answers as a
/// SYSCOM server; credentials pass through untouched (the target PC checks them), and nothing is cached. The relay
/// gives no access the target PC wouldn't give directly: every target still asks for its own password.
/// </summary>
public static class PeerRelay
{
    private static readonly HashSet<string> Paths = new(StringComparer.Ordinal)
    {
        "ws", "api/info", "api/health", "api/login", "api/token", "api/sensors", "api/history", "api/peers",
        "api/dashboard", "api/dashboard/background", "api/devices",
    };

    private static readonly string[] RequestHeaders = { "Authorization", "If-None-Match" };
    private static readonly string[] ResponseHeaders = { "ETag", "Retry-After", "Cache-Control" };

    /// <summary>Addresses given as "a.b.c.d-port" that answered as SYSCOM servers, and until when that holds.</summary>
    private static readonly ConcurrentDictionary<string, DateTime> Verified = new();

    public static void MapPeerRelay(this WebApplication app, LanDiscovery discovery, DeviceListStore devices)
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(4),
        })
        { Timeout = TimeSpan.FromSeconds(60) };

        app.Map("/relay/{target}/{**path}", async (HttpContext ctx, string target, string? path) =>
        {
            path ??= "";
            if (!Paths.Contains(path)) return Results.NotFound();
            if (Resolve(target, discovery, devices, out var known) is not { } upstream)
                return Results.NotFound(new { message = "Unknown SYSCOM PC." });
            if (!known && !await IsSyscom(http, upstream, ctx.RequestAborted))
                return Results.NotFound(new { message = "No SYSCOM server answers at that address." });

            if (path == "ws")
            {
                if (!ctx.WebSockets.IsWebSocketRequest) return Results.BadRequest();
                await RelayWebSocket(ctx, new UriBuilder(upstream) { Scheme = "ws", Path = "/ws", Query = ctx.Request.QueryString.Value }.Uri, app.Lifetime.ApplicationStopping);
                return Results.Empty;
            }
            return await RelayHttp(ctx, http, new Uri(upstream, "/" + path + ctx.Request.QueryString.Value));
        });
    }

    /// <summary>
    /// The target PC's HTTP address: a known server id (<paramref name="known"/>), or "a.b.c.d-port" on a private
    /// network, which is only used once it answers as a SYSCOM server.
    /// </summary>
    private static Uri? Resolve(string target, LanDiscovery discovery, DeviceListStore devices, out bool known)
    {
        known = true;
        if (discovery.Peers.FirstOrDefault(p => p.Id == target) is { } peer) return new Uri(peer.Url);
        if (devices.UrlOf(target) is { } url && Uri.TryCreate(url, UriKind.Absolute, out var listed) && listed.Scheme == "http")
            return new Uri(listed.GetLeftPart(UriPartial.Authority));

        known = false;
        var dash = target.LastIndexOf('-');
        if (dash <= 0 || !int.TryParse(target[(dash + 1)..], out var port) || port is <= 0 or > 65535) return null;
        if (!IPAddress.TryParse(target[..dash], out var ip) || !IsPrivate(ip)) return null;
        return new Uri($"http://{ip}:{port}");
    }

    /// <summary>Whether a SYSCOM server answers at that address (its /api/info names its id and this port).</summary>
    private static async Task<bool> IsSyscom(HttpClient http, Uri upstream, CancellationToken ct)
    {
        var key = upstream.Authority;
        if (Verified.TryGetValue(key, out var until) && until > DateTime.UtcNow) return true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            using var resp = await http.GetAsync(new Uri(upstream, "/api/info"), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentLength > 64 * 1024) return false;
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            var root = doc.RootElement;
            var ok = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString()!.Length is >= 8 and <= 64
                && root.TryGetProperty("port", out var port) && port.ValueKind == JsonValueKind.Number && port.TryGetInt32(out var p) && p == upstream.Port;
            if (ok)
            {
                if (Verified.Count > 256) Verified.Clear();
                Verified[key] = DateTime.UtcNow.AddMinutes(10);
            }
            return ok;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return false;
        }
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    private static async Task<IResult> RelayHttp(HttpContext ctx, HttpClient http, Uri url)
    {
        using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), url);
        foreach (var h in RequestHeaders)
            if (ctx.Request.Headers.TryGetValue(h, out var v)) req.Headers.TryAddWithoutValidation(h, (IEnumerable<string?>)v);
        if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.TransferEncoding.Count > 0)
        {
            req.Content = new StreamContent(ctx.Request.Body);
            if (ctx.Request.ContentType is { } type) req.Content.Headers.TryAddWithoutValidation("Content-Type", type);
        }

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Results.Json(new { message = "That PC doesn't answer." }, statusCode: StatusCodes.Status502BadGateway);
        }

        using (resp)
        {
            ctx.Response.StatusCode = (int)resp.StatusCode;
            foreach (var h in ResponseHeaders)
                if (resp.Headers.TryGetValues(h, out var values)) ctx.Response.Headers[h] = values.ToArray();
            if (resp.Content.Headers.ContentType is { } ct) ctx.Response.ContentType = ct.ToString();
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            await resp.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        }
        return Results.Empty;
    }

    private static async Task RelayWebSocket(HttpContext ctx, Uri url, CancellationToken stopping)
    {
        using var upstream = new ClientWebSocket();
        upstream.Options.Proxy = null;
        upstream.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await upstream.ConnectAsync(url, timeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpRequestException)
        {
            ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        using var client = await ctx.WebSockets.AcceptWebSocketAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        var stopped = new TaskCompletionSource();
        using var onStop = stopping.Register(() => stopped.TrySetResult());   // this PC restarting must not wait for it
        var up = Pump(client, upstream, cts.Token);
        var down = Pump(upstream, client, cts.Token);
        await Task.WhenAny(up, down, stopped.Task);
        cts.Cancel();
        try { await Task.WhenAll(up, down); } catch (Exception) { /* one side went away */ }
        foreach (var ws in new WebSocket[] { client, upstream })
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await ws.CloseOutputAsync(stopping.IsCancellationRequested ? WebSocketCloseStatus.EndpointUnavailable : WebSocketCloseStatus.NormalClosure, null, timeout.Token); }
                catch (Exception) { }
            }
        }
    }

    /// <summary>Copies messages one way; a close (with its code, e.g. 4401 "sign in again") is passed on.</summary>
    private static async Task Pump(WebSocket from, WebSocket to, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var r = await from.ReceiveAsync(buffer.AsMemory(), ct);
            if (r.MessageType == WebSocketMessageType.Close)
            {
                if (to.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await to.CloseOutputAsync(from.CloseStatus ?? WebSocketCloseStatus.NormalClosure, from.CloseStatusDescription, CancellationToken.None);
                return;
            }
            await to.SendAsync(buffer.AsMemory(0, r.Count), r.MessageType, r.EndOfMessage, ct);
        }
    }
}
