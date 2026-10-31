using System.Collections.Concurrent;
using System.Diagnostics;
using Syscom.Server.Config;

namespace Syscom.Server.Sensors;

public sealed record Snapshot(long Timestamp, IReadOnlyDictionary<string, float> Values);

/// <summary>
/// Polls all sensor sources on one shared loop. The loop runs at the fastest interval any connected client asked
/// for. With nobody connected it keeps sampling slowly for the history (HistorySampleMs), or goes idle (no hardware
/// access at all) when history is turned off.
/// </summary>
public sealed class SensorHub : BackgroundService
{
    private readonly IReadOnlyList<ISensorSource> _sources;
    private readonly ServerConfig _cfg;
    private readonly ILogger<SensorHub> _log;
    private readonly ConcurrentDictionary<Guid, int> _subscribers = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly ConcurrentDictionary<string, string> _errors = new();
    private readonly DerivedSensors _derived = new();

    private volatile Snapshot _latest = new(0, new Dictionary<string, float>());
    private volatile IReadOnlyList<SensorInfo> _catalog = Array.Empty<SensorInfo>();
    private int _catalogVersion;

    public SensorHub(IEnumerable<ISensorSource> sources, ServerConfig cfg, ILogger<SensorHub> log)
    {
        _sources = sources.ToList();
        _cfg = cfg;
        _log = log;
        Poll(); // populate the catalog before the first client arrives
    }

    public Snapshot Latest => _latest;
    public IReadOnlyList<SensorInfo> Catalog => _catalog;
    public int CatalogVersion => Volatile.Read(ref _catalogVersion);
    public IEnumerable<string> SourceNames => _sources.Select(s => s.Name);

    public int Clients => _subscribers.Count;

    /// <summary>Raised on the polling thread after every poll.</summary>
    public event Action<Snapshot>? Polled;

    /// <summary>Last error of a sensor source, or null when it is healthy.</summary>
    public string? SourceError(string sourceName) => _errors.TryGetValue(sourceName, out var e) ? e : null;

    public int ClampInterval(int ms) => Math.Clamp(ms, _cfg.MinIntervalMs, 60_000);

    /// <summary>Registers a client's polling interval; dispose the lease when the client leaves.</summary>
    public Lease Subscribe(int intervalMs)
    {
        var lease = new Lease(this, Guid.NewGuid());
        lease.SetInterval(intervalMs);
        _wake.Release();
        return lease;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_subscribers.IsEmpty && !_cfg.HistoryEnabled)
            {
                try { await _wake.WaitAsync(ct); } catch (OperationCanceledException) { break; }
                Drain();
                continue;
            }

            var interval = _subscribers.IsEmpty
                ? Math.Max(_cfg.MinIntervalMs, _cfg.HistorySampleMs)
                : _subscribers.Values.Min();
            var sw = Stopwatch.StartNew();
            Poll();
            var wait = interval - (int)sw.ElapsedMilliseconds;
            if (wait > 0)
            {
                // Wake early if a faster subscriber shows up.
                try { await _wake.WaitAsync(wait, ct); } catch (OperationCanceledException) { break; }
                Drain();
            }
        }
    }

    private void Drain()
    {
        while (_wake.CurrentCount > 0 && _wake.Wait(0)) { }
    }

    private void Poll()
    {
        var values = new Dictionary<string, float>(_latest.Values.Count + 16);
        var catalogChanged = false;
        foreach (var source in _sources)
        {
            try
            {
                catalogChanged |= source.Update(values);
                _errors.TryRemove(source.Name, out _);
            }
            catch (Exception ex)
            {
                if (_errors.TryAdd(source.Name, ex.Message))
                    _log.LogWarning(ex, "Sensor source {Source} failed to update", source.Name);
            }
        }

        if (catalogChanged || _catalog.Count == 0)
        {
            var catalog = _sources.SelectMany(s => s.Catalog).ToList();
            _derived.Rebuild(catalog);
            catalog.AddRange(_derived.Catalog);
            _catalog = catalog;
            Interlocked.Increment(ref _catalogVersion);
        }
        _derived.Update(values);
        _latest = new Snapshot(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), values);
        try { Polled?.Invoke(_latest); }
        catch (Exception ex) { _log.LogWarning(ex, "History could not take a reading"); }
    }

    public override void Dispose()
    {
        foreach (var s in _sources) s.Dispose();
        base.Dispose();
    }

    public sealed class Lease : IDisposable
    {
        private readonly SensorHub _hub;
        private readonly Guid _id;
        internal Lease(SensorHub hub, Guid id) { _hub = hub; _id = id; }

        public int Interval { get; private set; }

        public void SetInterval(int ms)
        {
            Interval = _hub.ClampInterval(ms);
            _hub._subscribers[_id] = Interval;
            _hub._wake.Release();
        }

        public void Dispose() => _hub._subscribers.TryRemove(_id, out _);
    }
}
