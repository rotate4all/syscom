using Syscom.Server.Admin;
using Syscom.Server.Auth;
using Syscom.Server.Components;
using Syscom.Server.Config;
using Syscom.Server.Dashboards;
using Syscom.Server.Discovery;
using Syscom.Server.History;
using Syscom.Server.Relay;
using Syscom.Server.Sensors;
using Syscom.Server.Streaming;
using Syscom.Server.Tls;
using Syscom.Server.Web;
using Syscom.Shared;

var options = CliOptions.Parse(args);
if (options.ShowHelp)
{
    Console.WriteLine("""
        SYSCOM server

          --config <path>        Config file (default: %ProgramData%\SYSCOM\config.json)
          --port <n>             HTTP port (default 47810)
          --https-port <n>       HTTPS port (default 47812)
          --name <name>          Name shown in the web client (default: computer name)
          --set-password <pw>    Set the login password and exit (signs out all clients)
          --mock                 Serve synthetic sensor data (for client development)

        Installed by install.ps1, SYSCOM runs as the Windows service "SYSCOM" and is managed from the tray app.
        """);
    return 0;
}

// Lock the data folder down (and repair it) before reading anything in it.
if (DataDirAccess.Secure() is { } accessWarning) Console.Error.WriteLine($"  Warning: {accessWarning}");

ServerConfig cfg;
try
{
    cfg = ServerConfig.Load(options.ConfigPath ?? ServerConfig.DefaultPath());
}
catch (UnauthorizedAccessException)
{
    // The data folder (password hash, certificate keys) is restricted to SYSTEM and Administrators.
    Console.Error.WriteLine($"SYSCOM can't read its settings in {SyscomPaths.DataDir}: that folder is for administrators only.");
    Console.Error.WriteLine("Run this from a terminal opened with \"Run as administrator\" (it's also needed to read all sensors).");
    Console.Error.WriteLine($"Already elevated? Reset the folder's permissions once:  icacls \"{SyscomPaths.DataDir}\" /reset /t /c /q");
    return 1;
}
if (options.Port is { } port) cfg.Port = port;
if (options.HttpsPort is { } httpsPort) cfg.HttpsPort = httpsPort;
if (options.Name is { } name) cfg.Name = name;

if (options.SetPassword is { } newPassword)
{
    AuthService.SetPassword(cfg, newPassword);
    cfg.Save();
    File.Delete(SyscomPaths.InitialPasswordFile);
    Console.WriteLine($"Password updated in {cfg.FilePath}. Existing sessions have been signed out.");
    return 0;
}

string? generatedPassword = null;
if (string.IsNullOrEmpty(cfg.PasswordHash))
{
    generatedPassword = AuthService.GeneratePassword();
    AuthService.SetPassword(cfg, generatedPassword);
    // Shown by the tray app until the user picks their own (the folder is SYSTEM/Administrators only).
    Directory.CreateDirectory(SyscomPaths.DataDir);
    File.WriteAllText(SyscomPaths.InitialPasswordFile, generatedPassword);
}
cfg.Save();

var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
// HTTP for everyone; HTTPS (own certificate, see Tls/TlsManager.cs) so phones can install the dashboard as an app.
var tls = cfg.HttpsEnabled ? TryCreateTls() : null;
builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(cfg.Port);
    if (tls is not null)
        k.ListenAnyIP(cfg.HttpsPort, listen => listen.UseHttps(https => https.ServerCertificateSelector = (_, _) => tls.Current));
});
if (tls is not null)
{
    builder.Services.AddSingleton(tls);
    builder.Services.AddHostedService(sp => sp.GetRequiredService<TlsManager>());
}
#if WINDOWS
builder.Services.AddWindowsService(o => o.ServiceName = SyscomPaths.ServiceName);
#endif
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton<AuthService>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// Sensor sources: LibreHardwareMonitor on Windows, drive space everywhere, mock on request / elsewhere.
builder.Services.AddSingleton<ISensorSource, DriveSpaceSource>();
var useMock = options.Mock;
#if LHM
if (!useMock)
{
    builder.Services.AddSingleton<ISensorSource>(sp =>
    {
        try
        {
            return new LhmSensorSource(sp.GetRequiredService<ILoggerFactory>().CreateLogger("Sensors"));
        }
        catch (Exception ex)
        {
            // Keep serving (drive space, health, management) even if the library is broken; the tray app shows why.
            return new FailedSensorSource("LibreHardwareMonitor", ex.Message);
        }
    });
}
#else
useMock = true;
#endif
#if WINDOWS
// FPS and frame time for any GPU, from Windows' graphics present events.
if (!useMock)
    builder.Services.AddSingleton<ISensorSource>(sp =>
        new PresentMonitorSource(sp.GetRequiredService<ILoggerFactory>().CreateLogger("FrameRate")));
#endif
if (useMock) builder.Services.AddSingleton<ISensorSource, MockSensorSource>();

builder.Services.AddSingleton<SensorHub>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SensorHub>());

// History and the dashboard layout live next to config.json (so a --config test instance keeps its own).
var dataDir = Path.GetDirectoryName(Path.GetFullPath(cfg.FilePath))!;
builder.Services.AddSingleton(sp => new HistoryStore(dataDir, sp.GetRequiredService<ILoggerFactory>().CreateLogger("History")));
builder.Services.AddSingleton(sp => new DashboardStore(dataDir, sp.GetRequiredService<ILogger<DashboardStore>>()));
builder.Services.AddSingleton(sp => new DeviceListStore(dataDir, sp.GetRequiredService<ILogger<DeviceListStore>>()));
builder.Services.AddSingleton(new BackgroundStore(dataDir));
builder.Services.AddSingleton<LanDiscovery>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LanDiscovery>());

// Management: component health, update checks and updates (used by the tray app).
builder.Services.AddSingleton<AdminAccess>();
builder.Services.AddSingleton<ReleaseFeed>();
builder.Services.AddSingleton<Component, SyscomAppComponent>();
builder.Services.AddSingleton<Component, WebClientComponent>();
builder.Services.AddSingleton<Component, LhmComponent>();
builder.Services.AddSingleton<Component, PawnIoComponent>();
builder.Services.AddSingleton<Component, DotnetRuntimeComponent>();
builder.Services.AddSingleton<ComponentManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ComponentManager>());

var app = builder.Build();
app.UseCors();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

var auth = app.Services.GetRequiredService<AuthService>();
var hub = app.Services.GetRequiredService<SensorHub>();
var discovery = app.Services.GetRequiredService<LanDiscovery>();
var components = app.Services.GetRequiredService<ComponentManager>();
var admin = app.Services.GetRequiredService<AdminAccess>();
var history = app.Services.GetRequiredService<HistoryStore>();
var dashboards = app.Services.GetRequiredService<DashboardStore>();
var deviceList = app.Services.GetRequiredService<DeviceListStore>();
var backgrounds = app.Services.GetRequiredService<BackgroundStore>();
if (cfg.HistoryEnabled) hub.Polled += history.Add;
var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

bool Authorized(HttpContext ctx)
{
    var header = ctx.Request.Headers.Authorization.ToString();
    var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : null;
    return auth.ValidateToken(token);
}

var api = app.MapGroup("/api");

api.MapGet("/info", () => Results.Ok(new
{
    id = cfg.Id,
    name = cfg.Name,
    version,
    os = Environment.OSVersion.VersionString,
    minIntervalMs = cfg.MinIntervalMs,
    port = cfg.Port,
    httpsPort = tls is not null ? cfg.HttpsPort : (int?)null,
    caFingerprint = tls?.CaFingerprint,
    caName = tls?.CaName,
    sources = hub.SourceNames,
}));

api.MapGet("/health", () => Results.Ok(new { status = "ok", version, sensors = hub.Catalog.Count }));

api.MapPost("/login", (HttpContext ctx, LoginRequest body) =>
{
    var token = auth.Login(ctx.Connection.RemoteIpAddress, body.Password, out var retryAfter);
    if (token is not null) return Results.Ok(new { token, expiresInDays = cfg.TokenLifetimeDays });
    if (retryAfter > TimeSpan.Zero)
    {
        ctx.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }
    return Results.Unauthorized();
});

// A signed-in browser swaps its token for a fresh one now and then, so a session lasts TokenLifetimeDays after it
// was last used rather than after the password was typed.
api.MapPost("/token", (HttpContext ctx) =>
    Authorized(ctx) ? Results.Ok(new { token = auth.Renew(), expiresInDays = cfg.TokenLifetimeDays }) : Results.Unauthorized());

api.MapGet("/sensors", (HttpContext ctx) =>
    Authorized(ctx) ? Results.Ok(hub.Catalog) : Results.Unauthorized());

// One sensor's history: ?sensor=<id>&days=1..30 → evenly spaced points (avg, min, max); null = no data.
api.MapGet("/history", (HttpContext ctx, string? sensor, int? days) =>
{
    if (!Authorized(ctx)) return Results.Unauthorized();
    if (string.IsNullOrEmpty(sensor)) return Results.BadRequest(new { message = "sensor is required" });
    var series = history.Query(sensor, days ?? 1);
    static IEnumerable<double?> Clean(float[] a) => a.Select(v => float.IsNaN(v) ? (double?)null : Math.Round(v, 3));
    return Results.Ok(new
    {
        sensor,
        enabled = cfg.HistoryEnabled,
        step = series.Step,
        start = series.Start,
        avg = Clean(series.Avg),
        min = Clean(series.Min),
        max = Clean(series.Max),
    });
});

// This PC's dashboard layout, shared by every browser that signs in.
api.MapGet("/dashboard", (HttpContext ctx) => Authorized(ctx) ? Results.Ok(dashboards.Get()) : Results.Unauthorized());
api.MapPut("/dashboard", async (HttpContext ctx) =>
{
    if (!Authorized(ctx)) return Results.Unauthorized();
    if (ctx.Request.ContentLength > DashboardStore.MaxBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    System.Text.Json.JsonDocument doc;
    try { doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted); }
    catch (System.Text.Json.JsonException) { return Results.BadRequest(new { message = "Not JSON." }); }
    using (doc)
    {
        var (rev, error) = dashboards.Save(doc.RootElement);
        return rev is null ? Results.BadRequest(new { message = error }) : Results.Ok(new { rev });
    }
});

// The dashboard's background picture (see BackgroundStore); its id is in the dashboard's "look".
api.MapGet("/dashboard/background", (HttpContext ctx) =>
{
    if (!Authorized(ctx)) return Results.Unauthorized();
    if (backgrounds.Get() is not { } bg) return Results.NotFound();
    var etag = $"\"{bg.Id}\"";
    if (ctx.Request.Headers.IfNoneMatch == etag) return Results.StatusCode(StatusCodes.Status304NotModified);
    ctx.Response.Headers.ETag = etag;
    ctx.Response.Headers.CacheControl = "private, no-cache";
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    return Results.File(bg.File, bg.Type);
});
api.MapPut("/dashboard/background", async (HttpContext ctx) =>
{
    if (!Authorized(ctx)) return Results.Unauthorized();
    if (!BackgroundStore.Accepts(ctx.Request.ContentType)) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
    if (ctx.Request.ContentLength > BackgroundStore.MaxBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    using var ms = new MemoryStream();
    var buffer = new byte[81920];
    int read;
    while ((read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
    {
        if (ms.Length + read > BackgroundStore.MaxBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        ms.Write(buffer, 0, read);
    }
    try { return Results.Ok(new { id = backgrounds.Save(ms.ToArray(), ctx.Request.ContentType!) }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
});
api.MapDelete("/dashboard/background", (HttpContext ctx) =>
{
    if (!Authorized(ctx)) return Results.Unauthorized();
    backgrounds.Delete();
    return Results.Ok(new { });
});

// The PCs a dashboard opened from this PC shows (see DeviceListStore).
api.MapGet("/devices", (HttpContext ctx) => Authorized(ctx) ? Results.Ok(deviceList.Get()) : Results.Unauthorized());
api.MapPut("/devices", async (HttpContext ctx) =>
{
    if (!Authorized(ctx)) return Results.Unauthorized();
    if (ctx.Request.ContentLength > 64 * 1024) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    System.Text.Json.JsonDocument doc;
    try { doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted); }
    catch (System.Text.Json.JsonException) { return Results.BadRequest(new { message = "Not JSON." }); }
    using (doc)
    {
        var (rev, error) = deviceList.Save(doc.RootElement);
        return rev is null ? Results.BadRequest(new { message = error }) : Results.Ok(new { rev });
    }
});

api.MapGet("/peers", (HttpContext ctx) =>
    Authorized(ctx)
        ? Results.Ok(discovery.Peers.Select(p => new { p.Id, p.Name, p.Url, p.HttpsUrl }))
        : Results.Unauthorized());

app.Map("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    // Browsers can't set headers on WebSockets, so the token comes in the query string. Rejected
    // clients get a close code instead of an HTTP error so the page can tell "log in again" from "offline".
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    if (!auth.ValidateToken(ctx.Request.Query["token"]))
    {
        await ws.CloseAsync((System.Net.WebSockets.WebSocketCloseStatus)MetricsSession.CloseUnauthorized, "unauthorized", ctx.RequestAborted);
        return;
    }
    await new MetricsSession(ws, hub, dashboards, deviceList).RunAsync(ctx.RequestAborted);
});

// ---- certificate for phones (public: it's a CA certificate, no secrets)
app.MapGet("/tls/syscom-ca.crt", () => tls is null
    ? Results.NotFound()
    : Results.File(tls.CaCertificateDer, "application/x-x509-ca-cert", $"SYSCOM-{cfg.Name}.crt"));
app.MapGet("/tls/syscom.mobileconfig", () => tls is null
    ? Results.NotFound()
    : Results.Text(tls.MobileConfig(), "application/x-apple-aspen-config", System.Text.Encoding.UTF8));

// ---- management API for the tray app: loopback only + admin key
var mgmt = api.MapGroup("/admin").AddEndpointFilter(async (ctx, next) =>
    admin.IsAuthorized(ctx.HttpContext) ? await next(ctx) : Results.StatusCode(StatusCodes.Status403Forbidden));

mgmt.MapGet("/status", () => Results.Ok(new ServerStatus(
    cfg.Name, version, cfg.Port,
    LanDiscovery.LocalAddresses().Select(ip => $"http://{ip}:{cfg.Port}").ToList(),
    hub.Clients, hub.Catalog.Count, WindowsServiceHelpers.IsService(), cfg.UpdateFeedUrl, components.LastUpdate,
    tls is not null ? cfg.HttpsPort : null, tls?.CaFingerprint)));

mgmt.MapGet("/components", () => Results.Ok(components.Status()));

mgmt.MapPost("/components/check", async (CancellationToken ct) =>
{
    await components.CheckAllAsync(ct);
    return Results.Ok(components.Status());
});

mgmt.MapPost("/components/update", async (UpdateRequest body, CancellationToken ct) =>
    Results.Ok(new { message = await components.UpdateAsync(body.Ids ?? Array.Empty<string>(), ct) }));

mgmt.MapGet("/settings", () => Results.Ok(new ServerSettings(cfg.AutoUpdate, cfg.UpdateCheckHours)));

mgmt.MapPost("/settings", (ServerSettings body) =>
{
    if (body.AutoUpdate is not ("syscom" or "all" or "off"))
        return Results.BadRequest(new { message = "AutoUpdate must be syscom, all or off." });
    cfg.AutoUpdate = body.AutoUpdate;
    if (body.UpdateCheckHours is > 0) cfg.UpdateCheckHours = Math.Clamp(body.UpdateCheckHours, 1, 168);
    cfg.Save();
    return Results.Ok(new ServerSettings(cfg.AutoUpdate, cfg.UpdateCheckHours));
});

mgmt.MapPost("/password", (PasswordRequest body) =>
{
    if (string.IsNullOrWhiteSpace(body.Password) || body.Password.Length < 6)
        return Results.BadRequest(new { message = "Use at least 6 characters." });
    AuthService.SetPassword(cfg, body.Password);
    cfg.Save();
    File.Delete(SyscomPaths.InitialPasswordFile);
    return Results.Ok(new { message = "Password changed. All browsers have been signed out." });
});

mgmt.MapPost("/restart", () =>
{
    components.Restart();
    return Results.Ok(new { message = "Restarting." });
});

// Lets an HTTPS dashboard reach the other PCs through this one, so a phone only has to trust this PC's certificate.
app.MapPeerRelay(discovery, deviceList);

app.MapWebClient();

app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine();
    Console.WriteLine($"  SYSCOM {version}  ·  {cfg.Name}");
    Console.WriteLine($"  Sensors: {string.Join(", ", hub.SourceNames)} ({hub.Catalog.Count})");
    var addresses = LanDiscovery.LocalAddressesRanked().ToList();
    var lan = addresses.FirstOrDefault().Address ?? "<this PC>";
    Console.WriteLine($"  Open:    http://{lan}:{cfg.Port}   (on this PC: http://localhost:{cfg.Port})");
    if (addresses.Count > 1)
        Console.WriteLine($"  Also on: {string.Join(", ", addresses.Skip(1).Select(a => a.Address))}" +
                          (addresses.Skip(1).Any(a => !a.HasGateway) ? " (virtual adapters, usually not reachable from a phone)" : ""));
    if (tls is not null)
    {
        Console.WriteLine($"  HTTPS:   port {cfg.HttpsPort} · phone setup: http://{lan}:{cfg.Port}/trust (or tray app → Set up phone)");
        Console.WriteLine($"  CA SHA-256: {tls.CaFingerprint}");
    }
    if (generatedPassword is not null)
    {
        Console.WriteLine($"  Password (generated, change with --set-password): {generatedPassword}");
    }
    if (!useMock && PawnIoCheck.Warning(cfg.MinPawnIoVersion) is { } pawnIoWarning)
    {
        Console.WriteLine($"  Warning: {pawnIoWarning}");
    }
    else if (PawnIoCheck.InstalledVersion() is { } pawnIo)
    {
        Console.WriteLine($"  PawnIO:  {pawnIo}");
    }
    if (OperatingSystem.IsWindows() && !Elevation.IsAdministrator())
    {
        Console.WriteLine("  Warning: not running as administrator; CPU temperatures and motherboard sensors will be missing.");
    }
    Console.WriteLine();
});

app.Run();
return 0;

TlsManager? TryCreateTls()
{
    try
    {
        return new TlsManager(LoggerFactory.Create(b => b.AddConsole()).CreateLogger<TlsManager>());
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Warning: HTTPS disabled, certificate setup failed: {ex.Message}");
        return null;
    }
}

internal sealed record LoginRequest(string? Password);
internal sealed record PasswordRequest(string? Password);
internal sealed record UpdateRequest(string[]? Ids);

internal static class Elevation
{
    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}

internal sealed class CliOptions
{
    public string? ConfigPath { get; private set; }
    public int? Port { get; private set; }
    public int? HttpsPort { get; private set; }
    public string? Name { get; private set; }
    public string? SetPassword { get; private set; }
    public bool Mock { get; private set; }
    public bool ShowHelp { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--config": o.ConfigPath = Next(); break;
                case "--port": o.Port = int.Parse(Next()); break;
                case "--https-port": o.HttpsPort = int.Parse(Next()); break;
                case "--name": o.Name = Next(); break;
                case "--set-password": o.SetPassword = Next(); break;
                case "--mock": o.Mock = true; break;
                case "-h" or "--help" or "/?": o.ShowHelp = true; break;
            }
        }
        return o;
    }
}
