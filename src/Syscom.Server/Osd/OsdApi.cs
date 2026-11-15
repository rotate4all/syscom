using Microsoft.AspNetCore.Routing;
using Syscom.Server.Api;
using Syscom.Server.Config;
using Syscom.Server.Dashboards;
using Syscom.Server.Sensors;
using Syscom.Shared;

namespace Syscom.Server.Osd;

/// <summary>
/// The on-screen display's endpoints: for the dashboard and scripts (/api/osd…, docs/API.md) and for the tray app,
/// which draws it (/api/admin/osd…, loopback + admin key).
/// </summary>
public static class OsdApi
{
    private const int MaxFonts = 2000;
    private const int MaxScreens = 16;

    public static void MapOsdApi(this RouteGroupBuilder api, RouteGroupBuilder admin, ApiAccess access, OsdService osd,
        SensorHub hub, DashboardStore dashboards, ServerConfig cfg)
    {
        // ---- dashboard and scripts
        // Whether it shows, what the tray app reports, the settings, and each reading with what it shows now
        // (?readings=false leaves the readings out, and doesn't wake the sensors).
        api.MapGet("/osd", async (HttpContext ctx, bool? readings) =>
        {
            if (!access.CanRead(ctx)) return Results.Unauthorized();
            var (items, _) = dashboards.Osd();
            var tray = osd.Tray;
            var withReadings = readings != false;
            var snap = withReadings ? await hub.FreshAsync(ctx.RequestAborted) : hub.Latest;
            var elements = withReadings ? OsdReadings.Elements(items, new Readings.Context(hub.Catalog, snap)).Select(e => e.Api).ToList() : null;
            return Results.Ok(new
            {
                pc = cfg.Name,
                shown = osd.Shown,
                hotkey = osd.Look().Hotkey,
                tray = new
                {
                    running = osd.TrayRunning,
                    hotkeyRegistered = tray?.HotkeyRegistered ?? false,
                    hotkeyError = tray?.HotkeyError,
                    screens = tray?.Screens ?? [],
                },
                look = osd.Look(),
                t = withReadings ? snap.Timestamp : (long?)null,
                elements,
            });
        });

        // The fonts installed on this PC (the tray app reports them), for the OSD's font list.
        api.MapGet("/osd/fonts", (HttpContext ctx) =>
            access.CanRead(ctx) ? Results.Ok(new { fonts = osd.Tray?.Fonts ?? [] }) : Results.Unauthorized());

        IResult Set(HttpContext ctx, bool? shown) =>
            access.CanWrite(ctx) ? Results.Ok(new { shown = osd.SetShown(shown) }) : Results.Unauthorized();
        api.MapPost("/osd/show", (HttpContext ctx) => Set(ctx, true));
        api.MapPost("/osd/hide", (HttpContext ctx) => Set(ctx, false));
        api.MapPost("/osd/toggle", (HttpContext ctx) => Set(ctx, null));

        // ---- the tray app
        // Hidden, it asks every 1.5 s without readings (the settings carry the shortcut); shown, at the OSD's rate.
        admin.MapGet("/osd", (bool? values) =>
        {
            var drawing = values == true && osd.Shown;
            osd.TrayAsked(drawing);
            var look = osd.Look();
            if (!drawing) return Results.Ok(new OsdFrame(osd.Rev, osd.Shown, look, 0, null));
            var snap = hub.Latest;
            var elements = OsdReadings.Elements(dashboards.Osd().Items, new Readings.Context(hub.Catalog, snap));
            return Results.Ok(new OsdFrame(osd.Rev, true, look, snap.Timestamp, OsdReadings.Groups(elements)));
        });

        admin.MapPost("/osd/shown", (OsdShownRequest body) => Results.Ok(new { shown = osd.SetShown(body.Shown) }));

        admin.MapPost("/osd/tray", (OsdTrayReport body) =>
        {
            static string Clean(string? s, int max) => new string((s ?? "").Where(c => !char.IsControl(c)).Take(max).ToArray()).Trim();
            var report = new OsdTrayReport(
                Clean(body.Hotkey, 64),
                body.HotkeyRegistered,
                body.HotkeyError is null ? null : Clean(body.HotkeyError, 300),
                (body.Screens ?? []).Take(MaxScreens)
                    .Select(s => new OsdScreen(Clean(s.Id, 64), Clean(s.Name, 80), Math.Clamp(s.Width, 0, 100_000),
                        Math.Clamp(s.Height, 0, 100_000), double.IsFinite(s.Scale) ? Math.Clamp(s.Scale, 0.5, 8) : 1, s.Primary))
                    .ToList(),
                (body.Fonts ?? []).Select(f => Clean(f, 64)).Where(f => f.Length > 0).Distinct().Take(MaxFonts).ToList());
            osd.Report(report);
            return Results.Ok(new { });
        });
    }
}
