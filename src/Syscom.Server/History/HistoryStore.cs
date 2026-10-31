using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Syscom.Server.Sensors;

namespace Syscom.Server.History;

/// <summary>
/// Keeps 30 days of history for every sensor, small enough to forget about (about 75 MB for 600 sensors).
///
/// Readings are averaged into buckets: 1-minute buckets kept for 3 days, 10-minute buckets kept for 31 days. Each
/// closed bucket is appended to a file for its tier and day as one fixed-size record, so writing is one sequential
/// append per minute (kind to SSDs) and reading one sensor is a seek per record.
///
///   history/sensors.txt           sensor id per line; the line number is the sensor's column (append-only)
///   history/m1-20261031-0.bin     header "SYSH", version, bucket seconds, columns; then records:
///                                 int64 bucket start (unix seconds) + columns × (avg, min, max) as float32
///
/// A day's file is continued after a restart; a new segment (-1, -2, …) starts when sensors appear beyond its columns.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    public const int MaxDays = 30;
    private const int MaxSensors = 5000;
    private const int MaxPoints = 1500;
    private const int HeaderSize = 16;
    private static readonly byte[] Magic = "SYSH"u8.ToArray();

    private readonly string _dir;
    private readonly ILogger _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _columns = new(StringComparer.Ordinal);
    private readonly Tier[] _tiers;
    private bool _warnedFull;
    private bool _disposed;

    public HistoryStore(string dataDir, ILogger log)
    {
        _dir = Path.Combine(dataDir, "history");
        _log = log;
        Directory.CreateDirectory(_dir);
        _tiers = new[] { new Tier("m1", 60, keepDays: 3), new Tier("m10", 600, keepDays: 31) };

        var index = Path.Combine(_dir, "sensors.txt");
        if (File.Exists(index))
            foreach (var line in File.ReadAllLines(index))
                if (line.Length > 0) _columns.TryAdd(line, _columns.Count);
        Prune(DateTime.UtcNow);
    }

    /// <summary>Adds one poll of all sensors (called from the sensor hub's loop).</summary>
    public void Add(Snapshot snap)
    {
        if (snap.Values.Count == 0) return;
        var now = snap.Timestamp / 1000;
        lock (_lock)
        {
            if (_disposed) return;
            foreach (var tier in _tiers)
            {
                var bucket = now - now % tier.Seconds;
                if (tier.Bucket != bucket)
                {
                    if (tier.Bucket != 0) Flush(tier);
                    tier.Bucket = bucket;
                }
            }

            foreach (var (id, value) in snap.Values)
            {
                if (float.IsNaN(value) || float.IsInfinity(value)) continue;
                var col = Column(id);
                if (col < 0) continue;
                foreach (var tier in _tiers) tier.Accumulate(col, value);
            }
        }
    }

    /// <summary>
    /// One sensor's history over the last <paramref name="days"/> days: evenly spaced points (at most 1500) with the
    /// average, lowest and highest reading of each. Gaps (SYSCOM not running) are NaN.
    /// </summary>
    public HistorySeries Query(string id, int days)
    {
        days = Math.Clamp(days, 1, MaxDays);
        var tier = days <= _tiers[0].KeepDays ? _tiers[0] : _tiers[1];
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var end = now - now % tier.Seconds;                 // start of the current (open) bucket
        var count = days * 86400 / tier.Seconds;
        var start = end - (count - 1) * (long)tier.Seconds;
        var avg = Filled(count); var min = Filled(count); var max = Filled(count);

        int col;
        lock (_lock)
        {
            if (!_columns.TryGetValue(id, out col)) return HistorySeries.Empty(tier.Seconds, start, count);
            tier.Stream?.Flush();
            // The open bucket, so the chart reaches right up to now.
            if (tier.Bucket == end && tier.Current(col) is { } open) { avg[^1] = open.Avg; min[^1] = open.Min; max[^1] = open.Max; }
        }

        var record = new byte[12];
        var stamp = new byte[8];
        for (var day = DateTimeOffset.FromUnixTimeSeconds(start).UtcDateTime.Date; day <= DateTime.UtcNow.Date; day = day.AddDays(1))
        {
            foreach (var path in Segments(tier, day))
            {
                try
                {
                    using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var header = new byte[HeaderSize];
                    if (RandomAccess.Read(handle, header, 0) < HeaderSize || !header.AsSpan(0, 4).SequenceEqual(Magic)) continue;
                    var columns = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
                    if (col >= columns) continue;
                    var size = 8 + columns * 12L;
                    var records = (RandomAccess.GetLength(handle) - HeaderSize) / size;
                    for (long r = 0; r < records; r++)
                    {
                        var offset = HeaderSize + r * size;
                        RandomAccess.Read(handle, stamp, offset);
                        var i = (BinaryPrimitives.ReadInt64LittleEndian(stamp) - start) / tier.Seconds;
                        if (i < 0 || i >= count) continue;
                        RandomAccess.Read(handle, record, offset + 8 + col * 12L);
                        var a = BinaryPrimitives.ReadSingleLittleEndian(record);
                        if (float.IsNaN(a)) continue;
                        avg[i] = a;
                        min[i] = BinaryPrimitives.ReadSingleLittleEndian(record.AsSpan(4));
                        max[i] = BinaryPrimitives.ReadSingleLittleEndian(record.AsSpan(8));
                    }
                }
                catch (IOException ex)
                {
                    _log.LogDebug(ex, "Skipping history file {File}", path);
                }
            }
        }

        // Merge neighbouring buckets so a long range stays a reasonable size for a phone.
        var factor = (count + MaxPoints - 1) / MaxPoints;
        if (factor > 1)
        {
            var merged = (count + factor - 1) / factor;
            var offset = merged * factor - count;          // align groups to end at "now"
            var (ma, mlo, mhi) = (Filled(merged), Filled(merged), Filled(merged));
            for (var g = 0; g < merged; g++)
            {
                double sum = 0; var n = 0; var lo = float.NaN; var hi = float.NaN;
                for (var k = g * factor - offset; k < (g + 1) * factor - offset; k++)
                {
                    if (k < 0 || float.IsNaN(avg[k])) continue;
                    sum += avg[k]; n++;
                    lo = float.IsNaN(lo) ? min[k] : Math.Min(lo, min[k]);
                    hi = float.IsNaN(hi) ? max[k] : Math.Max(hi, max[k]);
                }
                if (n > 0) { ma[g] = (float)(sum / n); mlo[g] = lo; mhi[g] = hi; }
            }
            return new HistorySeries(tier.Seconds * factor, end - (merged * (long)factor - 1) * tier.Seconds, ma, mlo, mhi);
        }
        return new HistorySeries(tier.Seconds, start, avg, min, max);
    }

    /// <summary>Writes the open buckets (on shutdown), so a restart loses nothing.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var tier in _tiers)
            {
                if (tier.Bucket != 0) Flush(tier);
                tier.Stream?.Dispose();
                tier.Stream = null;
            }
        }
    }

    // ---- writing

    private int Column(string id)
    {
        if (_columns.TryGetValue(id, out var col)) return col;
        if (_columns.Count >= MaxSensors || id.Contains('\n'))
        {
            if (!_warnedFull) { _warnedFull = true; _log.LogWarning("History is full ({Max} sensors); new sensors are not recorded", MaxSensors); }
            return -1;
        }
        col = _columns.Count;
        _columns[id] = col;
        File.AppendAllText(Path.Combine(_dir, "sensors.txt"), id + "\n", Encoding.UTF8);
        return col;
    }

    private void Flush(Tier tier)
    {
        try
        {
            var day = DateTimeOffset.FromUnixTimeSeconds(tier.Bucket).UtcDateTime.Date;
            if (tier.Stream is null || tier.Day != day || tier.Columns < _columns.Count) Open(tier, day);
            var record = new byte[8 + tier.Columns * 12];
            BinaryPrimitives.WriteInt64LittleEndian(record, tier.Bucket);
            for (var c = 0; c < tier.Columns; c++)
            {
                var (a, lo, hi) = tier.Current(c) ?? (float.NaN, float.NaN, float.NaN);
                var o = 8 + c * 12;
                BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(o), a);
                BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(o + 4), lo);
                BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(o + 8), hi);
            }
            tier.Stream!.Write(record);
            tier.Stream.Flush();
        }
        catch (IOException ex)
        {
            _log.LogWarning(ex, "Could not write history");
            tier.Stream?.Dispose();
            tier.Stream = null;
        }
        finally
        {
            tier.Reset();
        }
    }

    /// <summary>Continues today's newest segment when it has room for every sensor, else starts a new one.</summary>
    private void Open(Tier tier, DateTime day)
    {
        tier.Stream?.Dispose();
        tier.Stream = null;
        if (tier.Day != day) Prune(day);
        tier.Day = day;

        var segments = Segments(tier, day).ToList();
        if (segments.Count > 0)
        {
            var last = segments[^1];
            var columns = ReadColumns(last);
            if (columns >= _columns.Count)
            {
                var stream = new FileStream(last, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
                var size = 8 + columns * 12L;
                stream.SetLength(HeaderSize + (stream.Length - HeaderSize) / size * size);   // drop a torn record
                stream.Seek(0, SeekOrigin.End);
                (tier.Stream, tier.Columns) = (stream, columns);
                return;
            }
        }

        var cols = Math.Min(MaxSensors, _columns.Count + 64);   // room for a few new sensors before the next segment
        var path = Path.Combine(_dir, $"{tier.Name}-{day:yyyyMMdd}-{segments.Count}.bin");
        var fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), tier.Seconds);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), cols);
        fs.Write(header);
        (tier.Stream, tier.Columns) = (fs, cols);
    }

    private static int ReadColumns(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var header = new byte[HeaderSize];
            return fs.Read(header) == HeaderSize && header.AsSpan(0, 4).SequenceEqual(Magic)
                ? BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12)) : -1;
        }
        catch (IOException) { return -1; }
    }

    /// <summary>A tier's files for one day, in segment order.</summary>
    private IEnumerable<string> Segments(Tier tier, DateTime day) =>
        Directory.EnumerateFiles(_dir, $"{tier.Name}-{day:yyyyMMdd}-*.bin")
            .Select(p => (p, n: int.TryParse(Path.GetFileNameWithoutExtension(p).Split('-')[^1], out var n) ? n : -1))
            .Where(x => x.n >= 0).OrderBy(x => x.n).Select(x => x.p);

    private void Prune(DateTime today)
    {
        foreach (var tier in _tiers)
        {
            foreach (var path in Directory.EnumerateFiles(_dir, $"{tier.Name}-*.bin"))
            {
                var parts = Path.GetFileNameWithoutExtension(path).Split('-');
                if (parts.Length == 3 && DateTime.TryParseExact(parts[1], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    && d < today.AddDays(-tier.KeepDays))
                {
                    try { File.Delete(path); } catch (IOException) { }
                }
            }
        }
    }

    private static float[] Filled(int n) { var a = new float[n]; Array.Fill(a, float.NaN); return a; }

    private sealed class Tier(string name, int seconds, int keepDays)
    {
        public string Name { get; } = name;
        public int Seconds { get; } = seconds;
        public int KeepDays { get; } = keepDays;
        public long Bucket;
        public DateTime Day;
        public FileStream? Stream;
        public int Columns;

        private double[] _sum = new double[64];
        private int[] _n = new int[64];
        private float[] _min = new float[64];
        private float[] _max = new float[64];

        public void Accumulate(int col, float v)
        {
            if (col >= _n.Length)
            {
                var size = Math.Max(col + 1, _n.Length * 2);
                Array.Resize(ref _sum, size); Array.Resize(ref _n, size); Array.Resize(ref _min, size); Array.Resize(ref _max, size);
            }
            if (_n[col]++ == 0) { _sum[col] = v; _min[col] = v; _max[col] = v; return; }
            _sum[col] += v;
            if (v < _min[col]) _min[col] = v;
            if (v > _max[col]) _max[col] = v;
        }

        public (float Avg, float Min, float Max)? Current(int col) =>
            col < _n.Length && _n[col] > 0 ? ((float)(_sum[col] / _n[col]), _min[col], _max[col]) : null;

        public void Reset() => Array.Clear(_n);
    }
}

/// <summary>Evenly spaced history points: point i covers [Start + i·Step, Start + (i+1)·Step).</summary>
public sealed record HistorySeries(int Step, long Start, float[] Avg, float[] Min, float[] Max)
{
    public static HistorySeries Empty(int step, long start, int count)
    {
        var a = new float[count];
        Array.Fill(a, float.NaN);
        return new HistorySeries(step, start, a, a, a);
    }
}
