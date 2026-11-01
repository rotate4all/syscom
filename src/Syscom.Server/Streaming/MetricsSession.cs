using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using Syscom.Server.Dashboards;
using Syscom.Server.Sensors;

namespace Syscom.Server.Streaming;

/// <summary>
/// One WebSocket client. Protocol (JSON text frames):
///   server → {"type":"catalog","version":n,"sensors":[{id,name,hardware,hardwareType,type,unit}...]}
///   client → {"type":"subscribe","interval":1000,"sensors":["id",...]}   (omit sensors = all)
///   server → {"type":"subscribed","interval":1000,"sensors":["id",...]}
///   server → {"type":"data","t":unixMs,"v":[number|null,...]}            (same order as "subscribed")
///   server → {"type":"layout","rev":n}       the PC's dashboard changed (sent on connect too); GET /api/dashboard
///   server → {"type":"devices","rev":n}      the PC's device list changed (sent on connect too); GET /api/devices
/// </summary>
public sealed class MetricsSession
{
    public const int CloseUnauthorized = 4401;

    private readonly WebSocket _ws;
    private readonly SensorHub _hub;
    private readonly DashboardStore _dashboards;
    private readonly DeviceListStore _devices;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private volatile string[]? _subscription; // null = everything
    private volatile bool _resubscribed;
    private volatile bool _hasSubscribed;
    private int _sentCatalogVersion = -1;
    private int _sentLayoutRev = -1;
    private int _sentDevicesRev = -1;

    public MetricsSession(WebSocket ws, SensorHub hub, DashboardStore dashboards, DeviceListStore devices)
    {
        _ws = ws;
        _hub = hub;
        _dashboards = dashboards;
        _devices = devices;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var lease = _hub.Subscribe(1000);

        await SendCatalogIfChanged(cts.Token);
        var receive = ReceiveLoop(lease, cts.Token);
        var send = SendLoop(lease, cts.Token);

        await Task.WhenAny(receive, send);
        cts.Cancel();
        try { await Task.WhenAll(receive, send); } catch (OperationCanceledException) { } catch (WebSocketException) { }

        if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch (WebSocketException) { }
        }
    }

    private async Task ReceiveLoop(SensorHub.Lease lease, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
        {
            var count = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                if (count == buffer.Length) return; // oversized message: drop the client
                result = await _ws.ReceiveAsync(buffer.AsMemory(count), ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                count += result.Count;
            } while (!result.EndOfMessage);

            HandleMessage(buffer.AsSpan(0, count), lease);
        }
    }

    private void HandleMessage(ReadOnlySpan<byte> json, SensorHub.Lease lease)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "subscribe") return;

            if (root.TryGetProperty("interval", out var interval) && interval.TryGetInt32(out var ms))
                lease.SetInterval(ms);

            if (root.TryGetProperty("sensors", out var sensors) && sensors.ValueKind == JsonValueKind.Array)
            {
                _subscription = sensors.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Distinct()
                    .Take(1000)
                    .ToArray();
            }
            else
            {
                _subscription = null;
            }
            _resubscribed = true;
            _hasSubscribed = true;
        }
        catch (JsonException)
        {
            // ignore malformed messages
        }
    }

    private async Task SendLoop(SensorHub.Lease lease, CancellationToken ct)
    {
        var interval = lease.Interval;
        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(interval));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await SendCatalogIfChanged(ct);
                await SendLayoutIfChanged(ct);
                await SendDevicesIfChanged(ct);
                if (!_hasSubscribed) continue;

                // Clear the flag before reading the subscription so a concurrent change is announced next tick.
                var announce = _resubscribed;
                _resubscribed = false;
                var ids = _subscription ?? _hub.Catalog.Select(s => s.Id).ToArray();
                if (announce)
                {
                    await SendJson(w =>
                    {
                        w.WriteString("type", "subscribed");
                        w.WriteNumber("interval", lease.Interval);
                        w.WriteStartArray("sensors");
                        foreach (var id in ids) w.WriteStringValue(id);
                        w.WriteEndArray();
                    }, ct);
                }

                var snap = _hub.Latest;
                await SendJson(w =>
                {
                    w.WriteString("type", "data");
                    w.WriteNumber("t", snap.Timestamp);
                    w.WriteStartArray("v");
                    foreach (var id in ids)
                    {
                        if (snap.Values.TryGetValue(id, out var v)) w.WriteNumberValue(Math.Round(v, 2));
                        else w.WriteNullValue();
                    }
                    w.WriteEndArray();
                }, ct);

                if (lease.Interval != interval)
                {
                    interval = lease.Interval;
                    timer.Dispose();
                    timer = new PeriodicTimer(TimeSpan.FromMilliseconds(interval));
                }
            }
        }
        finally
        {
            timer.Dispose();
        }
    }

    private async Task SendLayoutIfChanged(CancellationToken ct)
    {
        var rev = _dashboards.Rev;
        if (rev == _sentLayoutRev) return;
        _sentLayoutRev = rev;
        await SendJson(w => { w.WriteString("type", "layout"); w.WriteNumber("rev", rev); }, ct);
    }

    private async Task SendDevicesIfChanged(CancellationToken ct)
    {
        var rev = _devices.Rev;
        if (rev == _sentDevicesRev) return;
        _sentDevicesRev = rev;
        await SendJson(w => { w.WriteString("type", "devices"); w.WriteNumber("rev", rev); }, ct);
    }

    private async Task SendCatalogIfChanged(CancellationToken ct)
    {
        var version = _hub.CatalogVersion;
        if (version == _sentCatalogVersion) return;
        _sentCatalogVersion = version;
        _resubscribed = true; // index order may have changed for "all sensors" subscriptions
        var catalog = _hub.Catalog;
        await SendJson(w =>
        {
            w.WriteString("type", "catalog");
            w.WriteNumber("version", version);
            w.WriteStartArray("sensors");
            foreach (var s in catalog)
            {
                w.WriteStartObject();
                w.WriteString("id", s.Id);
                w.WriteString("name", s.Name);
                w.WriteString("hardware", s.Hardware);
                w.WriteString("hardwareType", s.HardwareType);
                w.WriteString("type", s.Type);
                w.WriteString("unit", s.Unit);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }, ct);
    }

    private async Task SendJson(Action<Utf8JsonWriter> body, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }

        await _sendLock.WaitAsync(ct);
        try
        {
            await _ws.SendAsync(buffer.WrittenMemory, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
