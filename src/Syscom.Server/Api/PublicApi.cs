using Microsoft.AspNetCore.Routing;
using Syscom.Server.Config;
using Syscom.Server.Dashboards;
using Syscom.Server.Sensors;

namespace Syscom.Server.Api;

/// <summary>
/// The API for scripts and other apps (docs/API.md; the dashboard shows the same reference with this PC's own
/// sensors and cards as examples). Everything here is read-only, so a read key is enough. The dashboard's own
/// endpoints (/api/dashboard, /api/devices, /api/history, /ws) take API keys too; see Program.cs.
/// </summary>
public static class PublicApi
{
    public static void MapPublicApi(this RouteGroupBuilder api, ApiAccess access, ApiKeyStore keys, SensorHub hub,
        DashboardStore dashboards, ServerConfig cfg)
    {
        // ---- keys: listed, created and revoked from a signed-in dashboard only (never with a key)
        api.MapGet("/keys", (HttpContext ctx) => access.IsSession(ctx) ? Results.Ok(keys.List()) : Results.Unauthorized());
        api.MapPost("/keys", (HttpContext ctx, NewKey body) =>
        {
            if (!access.IsSession(ctx)) return Results.Unauthorized();
            if (ApiKeyStore.ParseAccess(body.Access ?? "read") is not { } level)
                return Results.BadRequest(new { message = "access must be \"read\" or \"full\"." });
            var (key, secret, error) = keys.Create(body.Name, level);
            return key is null ? Results.BadRequest(new { message = error }) : Results.Ok(new { key, secret });
        });
        api.MapDelete("/keys/{id}", (HttpContext ctx, string id) =>
        {
            if (!access.IsSession(ctx)) return Results.Unauthorized();
            return keys.Revoke(id) ? Results.Ok(new { }) : Results.NotFound(new { message = "No such key." });
        });

        // ---- sensors
        // Every sensor with its current value; ?category=gpu&kind=temperature&hardware=…&type=…&q=…&ids=a,b narrow it.
        api.MapGet("/sensors", async (HttpContext ctx) =>
        {
            if (!access.CanRead(ctx)) return Results.Unauthorized();
            var (filter, error) = Readings.Filter.From(ctx.Request);
            if (filter is null) return Results.BadRequest(new { message = error });
            var snap = await hub.FreshAsync(ctx.RequestAborted);
            return Results.Ok(filter.Apply(hub.Catalog).Select(s => Readings.Sensor(s, snap)));
        });

        // One sensor: /api/sensors/amdcpu/0/temperature/2 (its id, without the leading slash, or URL-encoded).
        api.MapGet("/sensors/{**id}", async (HttpContext ctx, string id) =>
        {
            if (!access.CanRead(ctx)) return Results.Unauthorized();
            id = Uri.UnescapeDataString(id);
            if (!id.StartsWith('/')) id = "/" + id;
            var sensor = hub.Catalog.FirstOrDefault(s => s.Id == id);
            return sensor is null ? Results.NotFound(new { message = $"No sensor {id} on this PC." }) : Results.Ok(Readings.Sensor(sensor, await hub.FreshAsync(ctx.RequestAborted)));
        });

        // Which categories and kinds of sensors this PC has, with counts (for ?category= and ?kind=).
        api.MapGet("/categories", (HttpContext ctx) =>
            access.CanRead(ctx) ? Results.Ok(Readings.CategoryList(hub.Catalog)) : Results.Unauthorized());

        // Just values, as compact as it gets: { t, values: { id: value } }. Same filters as /api/sensors.
        api.MapGet("/values", async (HttpContext ctx) =>
        {
            if (!access.CanRead(ctx)) return Results.Unauthorized();
            var (filter, error) = Readings.Filter.From(ctx.Request);
            if (filter is null) return Results.BadRequest(new { message = error });
            var snap = await hub.FreshAsync(ctx.RequestAborted);
            var ids = filter.Ids is { } only && filter.Category is null && filter.Kind is null && filter.Hardware is null && filter.Type is null && filter.Search is null
                ? only.AsEnumerable()   // asked by id: unknown ids answer null rather than vanish
                : filter.Apply(hub.Catalog).Select(s => s.Id);
            var values = new Dictionary<string, double?>();
            foreach (var id in ids) values[id] = Readings.ValueOf(snap, id);
            return Results.Ok(new { t = snap.Timestamp, values });
        });

        // ---- the dashboard's cards, with what they show right now. ?layout=desktop (default), phone, or a saved layout.
        api.MapGet("/dashboard/readings", async (HttpContext ctx, string? layout) =>
        {
            if (!access.CanRead(ctx)) return Results.Unauthorized();
            if (Resolve(dashboards, layout) is not { } items) return LayoutNotFound(layout);
            var rc = await Context(hub, ctx.RequestAborted);
            var groups = Readings.Groups(items.Items);
            return Results.Ok(new
            {
                pc = cfg.Name,
                layout = items.Name,
                t = rc.Snap.Timestamp,
                widgets = groups[0].Cards.Select(c => Readings.Widget(c, null, rc)).ToList(),
                sections = groups.Skip(1).Select(g => Readings.Section(g.Section, g.Cards, rc)).ToList(),
            });
        });

        api.MapGet("/widgets/{id}", async (HttpContext ctx, string id, string? layout) =>
        {
            if (!access.CanRead(ctx)) return Results.Unauthorized();
            if (Resolve(dashboards, layout) is not { } items) return LayoutNotFound(layout);
            var rc = await Context(hub, ctx.RequestAborted);
            foreach (var (section, cards) in Readings.Groups(items.Items))
            {
                var card = cards.FirstOrDefault(c => Readings.Str(c, "id") == id);
                if (card.ValueKind != System.Text.Json.JsonValueKind.Undefined)
                    return Results.Ok(new { t = rc.Snap.Timestamp, widget = Readings.Widget(card, section is { } s ? Readings.Str(s, "id") : null, rc) });
            }
            return Results.NotFound(new { message = $"No card {id} in the {items.Name} layout." });
        });

        api.MapGet("/sections/{id}", async (HttpContext ctx, string id, string? layout) =>
        {
            if (!access.CanRead(ctx)) return Results.Unauthorized();
            if (Resolve(dashboards, layout) is not { } items) return LayoutNotFound(layout);
            var rc = await Context(hub, ctx.RequestAborted);
            var group = Readings.Groups(items.Items).Skip(1).FirstOrDefault(g => Readings.Str(g.Section!.Value, "id") == id);
            return group.Section is null
                ? Results.NotFound(new { message = $"No section {id} in the {items.Name} layout." })
                : Results.Ok(new { t = rc.Snap.Timestamp, section = Readings.Section(group.Section, group.Cards, rc) });
        });
    }

    public sealed record NewKey(string? Name, string? Access);

    private static async Task<Readings.Context> Context(SensorHub hub, CancellationToken ct) => new(hub.Catalog, await hub.FreshAsync(ct));

    private static (System.Text.Json.JsonElement? Items, string Name)? Resolve(DashboardStore dashboards, string? layout)
    {
        var (desktop, phone, saved) = dashboards.Layouts();
        var (items, name, error) = Readings.Layout(desktop, phone, saved, layout);
        return error is null ? (items, name) : null;
    }

    private static IResult LayoutNotFound(string? layout) =>
        Results.NotFound(new { message = $"No layout \"{layout}\". Use desktop, phone, or the id or name of a saved layout." });
}
