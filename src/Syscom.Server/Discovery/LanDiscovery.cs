using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Syscom.Server.Config;

namespace Syscom.Server.Discovery;

public sealed record Peer(string Id, string Name, string Address, int Port, int? HttpsPort, DateTime LastSeen)
{
    public string Url => $"http://{Address}:{Port}";
    public string? HttpsUrl => HttpsPort is { } p ? $"https://{Address}:{p}" : null;
}

/// <summary>
/// Announces this server on the LAN with a small UDP broadcast every few seconds and listens for other
/// SYSCOM servers. Browsers can't do UDP, so the web client asks any server it is connected to for
/// <c>/api/peers</c> to find the rest.
/// </summary>
public sealed class LanDiscovery : BackgroundService
{
    private const string App = "syscom";
    private static readonly TimeSpan AnnounceEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PeerTtl = TimeSpan.FromSeconds(20);

    private readonly ServerConfig _cfg;
    private readonly ILogger<LanDiscovery> _log;
    private readonly ConcurrentDictionary<string, Peer> _peers = new();

    public LanDiscovery(ServerConfig cfg, ILogger<LanDiscovery> log)
    {
        _cfg = cfg;
        _log = log;
    }

    public IReadOnlyList<Peer> Peers
    {
        get
        {
            var cutoff = DateTime.UtcNow - PeerTtl;
            foreach (var p in _peers.Values.Where(p => p.LastSeen < cutoff)) _peers.TryRemove(p.Id, out _);
            return _peers.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_cfg.DiscoveryEnabled) return;

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.EnableBroadcast = true;
        try
        {
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, _cfg.DiscoveryPort));
        }
        catch (SocketException ex)
        {
            _log.LogWarning("LAN discovery disabled: cannot bind UDP {Port} ({Error})", _cfg.DiscoveryPort, ex.SocketErrorCode);
            return;
        }

        var receive = ReceiveLoop(udp, ct);
        var message = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            app = App, v = 1, id = _cfg.Id, name = _cfg.Name, port = _cfg.Port,
            httpsPort = _cfg.HttpsEnabled ? _cfg.HttpsPort : (int?)null,
        }));

        using var timer = new PeriodicTimer(AnnounceEvery);
        do
        {
            foreach (var target in BroadcastAddresses())
            {
                try { await udp.SendAsync(message, new IPEndPoint(target, _cfg.DiscoveryPort), ct); }
                catch (SocketException) { /* interface down; try next round */ }
            }
        } while (await SafeTick(timer, ct));

        await receive;
    }

    private async Task ReceiveLoop(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }

            try
            {
                using var doc = JsonDocument.Parse(result.Buffer);
                var root = doc.RootElement;
                if (root.GetProperty("app").GetString() != App) continue;
                var id = root.GetProperty("id").GetString();
                if (string.IsNullOrEmpty(id) || id == _cfg.Id) continue;
                var name = root.GetProperty("name").GetString() ?? id;
                var port = root.GetProperty("port").GetInt32();
                int? httpsPort = root.TryGetProperty("httpsPort", out var hp) && hp.ValueKind == JsonValueKind.Number ? hp.GetInt32() : null;
                var isNew = !_peers.ContainsKey(id);
                _peers[id] = new Peer(id, name, result.RemoteEndPoint.Address.ToString(), port, httpsPort, DateTime.UtcNow);
                if (isNew) _log.LogInformation("Discovered SYSCOM server {Name} at {Address}:{Port}", name, result.RemoteEndPoint.Address, port);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                // Not ours / malformed; ignore.
            }
        }
    }

    private static async Task<bool> SafeTick(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>Directed broadcast address of every active IPv4 interface, plus the limited broadcast.</summary>
    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        var set = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                var bcast = new byte[4];
                for (var i = 0; i < 4; i++) bcast[i] = (byte)(ip[i] | ~mask[i]);
                set.Add(new IPAddress(bcast));
            }
        }
        return set;
    }

    /// <summary>LAN IPv4 addresses of this machine, for printing connect URLs.</summary>
    public static IEnumerable<string> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString())
            .Distinct();
}
