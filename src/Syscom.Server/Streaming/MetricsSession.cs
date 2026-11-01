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
///   Several rates on one connection (a card that refreshes faster or slower than the rest of the dashboard):
///   client → {"type":"subscribe","streams":[{"id":1,"interval":500,"sensors":[...]},{"id":2,"interval":5000}]}
///   server → {"type":"subscribed","streams":[{"id":1,"interval":500,"sensors":[...]},...]}
///   server → {"type":"data","s":1,"t":unixMs,"v":[...]}                 (values of stream 1, in its order)
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

    /// <summary>What the client asked for: one or more sets of sensors, each at its own interval.</summary>
    /// <param name="Id">Stream id the client chose; null for the single-rate form (its frames carry no "s").</param>
    /// <param name="Sensors">null = every sensor.</param>
    private sealed record Stream(int? Id, int Interval, string[]? Sensors);

    private const int MaxStreams = 16;
    private const int MaxSensors = 1000;
    private const int MetaCheckMs = 500;   // how often catalog / layout / device list changes are looked for

    private volatile Stream[] _streams = Array.Empty<Stream>();
    private volatile bool _resubscribed;
    private volatile bool _hasSubscribed;
    private readonly SemaphoreSlim _changed = new(0);
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

    /// <summary>
    /// Serves the client until it goes away (<paramref name="ct"/>) or SYSCOM stops (<paramref name="stopping"/>).
    /// A stopping server says goodbye right away (close code 1001, the page reconnects): an open dashboard must not
    /// hold up a restart, or the Windows service runs into its 30 s shutdown limit and crashes.
    /// </summary>
    public async Task RunAsync(CancellationToken ct, CancellationToken stopping)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var lease = _hub.Subscribe(1000);
        var stopped = new TaskCompletionSource();
        using var onStop = stopping.Register(() => stopped.TrySetResult());

        try { await SendCatalogIfChanged(cts.Token); } catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { return; }
        var receive = ReceiveLoop(lease, cts.Token);
        var send = SendLoop(lease, cts.Token);

        await Task.WhenAny(receive, send, stopped.Task);
        if (stopping.IsCancellationRequested) await CloseForShutdown();
        cts.Cancel();
        try { await Task.WhenAll(receive, send); } catch (OperationCanceledException) { } catch (WebSocketException) { }

        if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token); }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        }
    }

    private async Task CloseForShutdown()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await _sendLock.WaitAsync(timeout.Token);
            try
            {
                if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await _ws.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "SYSCOM is restarting", timeout.Token);
            }
            finally { _sendLock.Release(); }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
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

            var streams = new List<Stream>();
            if (root.TryGetProperty("streams", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in list.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object || streams.Count == MaxStreams) continue;
                    var id = e.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out var n) ? n : streams.Count;
                    if (streams.Any(x => x.Id == id)) continue;
                    streams.Add(new Stream(id, IntervalOf(e, lease.Interval), SensorsOf(e)));
                }
            }
            else
            {
                streams.Add(new Stream(null, IntervalOf(root, lease.Interval), SensorsOf(root)));
            }
            _streams = streams.ToArray();
            // The hardware is read as often as the fastest stream needs.
            if (streams.Count > 0) lease.SetInterval(streams.Min(x => x.Interval));
            _resubscribed = true;
            _hasSubscribed = true;
            _changed.Release();
        }
        catch (JsonException)
        {
            // ignore malformed messages
        }
    }

    private int IntervalOf(JsonElement e, int fallback) =>
        _hub.ClampInterval(e.TryGetProperty("interval", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var ms) ? ms : fallback);

    private static string[]? SensorsOf(JsonElement e) =>
        e.TryGetProperty("sensors", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Distinct().Take(MaxSensors).ToArray()
            : null;

    /// <summary>
    /// Sends each stream's readings on its own schedule, and announces catalog, layout and device-list changes.
    /// Wakes early when the client changes its subscription.
    /// </summary>
    private async Task SendLoop(SensorHub.Lease lease, CancellationToken ct)
    {
        var due = new Dictionary<int, long>();   // stream → when it sends next (Environment.TickCount64)
        Stream[] streams = Array.Empty<Stream>();
        string[][] ids = Array.Empty<string[]>();
        long nextMeta = 0;
        while (!ct.IsCancellationRequested)
        {
            var now = Environment.TickCount64;
            if (now >= nextMeta)
            {
                await SendCatalogIfChanged(ct);
                await SendLayoutIfChanged(ct);
                await SendDevicesIfChanged(ct);
                nextMeta = now + MetaCheckMs;
            }

            if (_hasSubscribed)
            {
                // Clear the flag before reading the subscription so a concurrent change is announced next round.
                if (_resubscribed)
                {
                    _resubscribed = false;
                    streams = _streams;
                    var all = _hub.Catalog.Select(c => c.Id).ToArray();
                    ids = streams.Select(x => x.Sensors ?? all).ToArray();
                    await Announce(streams, ids, ct);
                    due.Clear();
                }

                var snap = _hub.Latest;
                for (var i = 0; i < streams.Length; i++)
                {
                    var key = streams[i].Id ?? -1;
                    if (due.TryGetValue(key, out var at) && now < at) continue;
                    await SendData(streams[i].Id, ids[i], snap, ct);
                    var next = due.ContainsKey(key) ? at + streams[i].Interval : now + streams[i].Interval;
                    due[key] = next <= now ? now + streams[i].Interval : next;   // no catching up after a stall
                }
            }

            var wake = nextMeta;
            foreach (var at in due.Values) wake = Math.Min(wake, at);
            var wait = (int)Math.Clamp(wake - Environment.TickCount64, 1, MetaCheckMs);
            if (await _changed.WaitAsync(wait, ct))
                while (_changed.CurrentCount > 0 && _changed.Wait(0)) { }
        }
    }

    private Task Announce(Stream[] streams, string[][] ids, CancellationToken ct) => SendJson(w =>
    {
        w.WriteString("type", "subscribed");
        if (streams is [{ Id: null } single])
        {
            w.WriteNumber("interval", single.Interval);
            WriteIds(w, "sensors", ids[0]);
            return;
        }
        w.WriteStartArray("streams");
        for (var i = 0; i < streams.Length; i++)
        {
            w.WriteStartObject();
            w.WriteNumber("id", streams[i].Id ?? i);
            w.WriteNumber("interval", streams[i].Interval);
            WriteIds(w, "sensors", ids[i]);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }, ct);

    private static void WriteIds(Utf8JsonWriter w, string name, string[] ids)
    {
        w.WriteStartArray(name);
        foreach (var id in ids) w.WriteStringValue(id);
        w.WriteEndArray();
    }

    private Task SendData(int? stream, string[] ids, Snapshot snap, CancellationToken ct) => SendJson(w =>
    {
        w.WriteString("type", "data");
        if (stream is { } s) w.WriteNumber("s", s);
        w.WriteNumber("t", snap.Timestamp);
        w.WriteStartArray("v");
        foreach (var id in ids)
        {
            if (snap.Values.TryGetValue(id, out var v)) w.WriteNumberValue(Math.Round(v, 2));
            else w.WriteNullValue();
        }
        w.WriteEndArray();
    }, ct);

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
