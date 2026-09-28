using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FantasyMontior.Core;

public sealed record SensorSeriesId(string HardwareId, string SensorKey, string Kind, string Unit)
{
    public static SensorSeriesId From(SensorReading reading) => new(reading.HardwareId, reading.Key, reading.Kind, reading.Unit);
}

public sealed record HistorySensor(SensorSeriesId Id, string Name, string HardwareName, string RootHardwareId);
// Separate segments within a minute prevent a failed collection from being drawn as continuous coverage.
public readonly record struct HistoryMinute(DateTimeOffset Minute, long Segment, DateTimeOffset First,
    DateTimeOffset Last, int Count, double Sum, double Minimum, double Maximum)
{
    [JsonIgnore] public double Average => Sum / Count;
}
public readonly record struct HistoryPeak(DateTimeOffset Time, double Value);
public sealed record HistorySeriesArchive(HistorySensor Sensor, DateTimeOffset LastSeen,
    HistoryMinute[] Minutes, HistoryPeak[] Peaks);
public sealed record HistoryArchive(int Version, HistorySeriesArchive[] Series);
public sealed record HistorySeries(HistorySensor Sensor, HistoryMinute[] Minutes, double? Maximum);
public sealed record HistoryRecordingStatus(bool Loading, string? LoadError, string? SaveError);

public interface ISensorHistoryStore
{
    Task<HistoryArchive?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(HistoryArchive archive, CancellationToken cancellationToken = default);
}

public sealed class FileSensorHistoryStore(string directory) : ISensorHistoryStore
{
    private readonly string _path = Path.Combine(directory, "history-v1.json.gz");

    public async Task<HistoryArchive?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return null;
        await using var file = File.OpenRead(_path);
        await using var compressed = new GZipStream(file, CompressionMode.Decompress);
        // Bound uncompressed input too, so damaged local files cannot exhaust memory during startup.
        using var buffer = new MemoryStream();
        var block = new byte[65536];
        int count;
        while ((count = await compressed.ReadAsync(block, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > 256L * 1024 * 1024) throw new InvalidDataException("History file is too large.");
            buffer.Write(block, 0, count);
        }
        buffer.Position = 0;
        return await JsonSerializer.DeserializeAsync<HistoryArchive>(buffer, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("History file is empty.");
    }

    public async Task SaveAsync(HistoryArchive archive, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            await using (var compressed = new GZipStream(file, CompressionLevel.Fastest))
                await JsonSerializer.SerializeAsync(compressed, archive, cancellationToken: cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>Application-owned history. Recording never performs disk I/O or invokes presentation code.</summary>
public sealed class SensorHistoryService : IAsyncDisposable
{
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private readonly object _gate = new();
    private readonly Dictionary<SensorSeriesId, Series> _series = [];
    private readonly ISensorHistoryStore? _store;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private Task? _start, _writer, _dispose;
    private bool _loading;
    private string? _loadError, _saveError;

    private sealed class Series(HistorySensor sensor)
    {
        public HistorySensor Sensor = sensor;
        public DateTimeOffset LastSeen, LastAccepted;
        public TimeSpan Interval = TimeSpan.FromSeconds(1);
        public readonly Queue<HistoryMinute> Minutes = new();
        public HistoryMinute? Current;
        public readonly LinkedList<HistoryPeak> Peaks = new();
        public bool Broken = true;
        public long Segment;

        public void Prune(DateTimeOffset now)
        {
            var cutoff = now - Retention;
            while (Minutes.TryPeek(out var minute) && minute.Last <= cutoff) Minutes.Dequeue();
            if (Current?.Last <= cutoff) Current = null;
            while (Peaks.First is { } peak && peak.Value.Time <= cutoff) Peaks.RemoveFirst();
        }
        public HistoryMinute[] CopyMinutes() => Current is { } current ? [.. Minutes, current] : [.. Minutes];
    }

    public SensorHistoryService(ISensorHistoryStore? store = null, TimeProvider? timeProvider = null)
    {
        _store = store;
        _time = timeProvider ?? TimeProvider.System;
    }

    public HistoryRecordingStatus Status { get { lock (_gate) return new(_loading, _loadError, _saveError); } }
    public Task StartAsync()
    {
        lock (_gate) return _start ??= Task.Run(LoadAndStartAsync);
    }

    private async Task LoadAndStartAsync()
    {
        lock (_gate) _loading = true;
        if (_store is not null)
        {
            try
            {
                var archive = await _store.LoadAsync().ConfigureAwait(false);
                if (archive is not null)
                {
                    var restored = Restore(archive, _time.GetUtcNow());
                    lock (_gate)
                        foreach (var (id, series) in restored)
                            _series.TryAdd(id, series);
                }
            }
            catch (Exception ex) { lock (_gate) _loadError = ex.Message; }
        }
        lock (_gate) _loading = false;
        if (_store is not null) _writer = WritePeriodicallyAsync();
    }

    private static Dictionary<SensorSeriesId, Series> Restore(HistoryArchive archive, DateTimeOffset now)
    {
        if (archive.Version != 1 || archive.Series is null || archive.Series.Length > 4096)
            throw new InvalidDataException("Unsupported or invalid history data.");
        var result = new Dictionary<SensorSeriesId, Series>();
        foreach (var saved in archive.Series)
        {
            if (saved?.Sensor?.Id is not { } id || string.IsNullOrWhiteSpace(id.HardwareId) ||
                string.IsNullOrWhiteSpace(id.SensorKey) || id.Kind is null || id.Unit is null ||
                saved.Sensor.Name is null || saved.Sensor.HardwareName is null || saved.Sensor.RootHardwareId is null ||
                saved.Minutes is null || saved.Peaks is null || saved.LastSeen > now)
                throw new InvalidDataException("Invalid sensor history descriptor.");
            var series = new Series(saved.Sensor) { LastSeen = saved.LastSeen };
            DateTimeOffset previous = DateTimeOffset.MinValue;
            long segment = 0;
            foreach (var minute in saved.Minutes)
            {
                if (minute.Count <= 0 || !double.IsFinite(minute.Sum) || !double.IsFinite(minute.Minimum) ||
                    !double.IsFinite(minute.Maximum) || minute.Minimum > minute.Maximum ||
                    minute.Average < minute.Minimum - Math.Abs(minute.Minimum) * 1e-12 - 1e-9 ||
                    minute.Average > minute.Maximum + Math.Abs(minute.Maximum) * 1e-12 + 1e-9 ||
                    minute.First > minute.Last || minute.Last > now || minute.First <= previous ||
                    minute.Minute != FloorMinute(minute.First) || minute.Minute != FloorMinute(minute.Last) ||
                    minute.Segment < segment)
                    throw new InvalidDataException("Invalid history minute.");
                series.Minutes.Enqueue(minute);
                previous = minute.Last;
                segment = minute.Segment;
            }
            series.LastAccepted = previous;
            series.Segment = segment;
            previous = DateTimeOffset.MinValue;
            var peakValue = double.PositiveInfinity;
            foreach (var peak in saved.Peaks)
            {
                if (!double.IsFinite(peak.Value) || peak.Value >= peakValue || peak.Time <= previous || peak.Time > now)
                    throw new InvalidDataException("Invalid history maximum.");
                series.Peaks.AddLast(peak);
                previous = peak.Time;
                peakValue = peak.Value;
            }
            series.Prune(now);
            if (saved.LastSeen > now - Retention && !result.TryAdd(id, series))
                throw new InvalidDataException("Duplicate history identity.");
        }
        return result;
    }

    public void Record(MonitoringSnapshot snapshot, TimeSpan interval)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            Prune(now);
            var hardware = snapshot.Hardware.ToDictionary(h => h.Id, StringComparer.Ordinal);
            var present = new HashSet<SensorSeriesId>();
            foreach (var reading in snapshot.Sensors)
            {
                var id = SensorSeriesId.From(reading);
                present.Add(id);
                hardware.TryGetValue(reading.HardwareId, out var device);
                var descriptor = new HistorySensor(id, reading.Name, device?.Name ?? reading.HardwareId, device?.RootId ?? reading.HardwareId);
                if (!_series.TryGetValue(id, out var series)) _series[id] = series = new(descriptor);
                series.Sensor = descriptor;
                series.LastSeen = now;
                if (reading.Error is not null || reading.Value is not { } value || !double.IsFinite(value) ||
                    reading.LastSuccess is not { } observed || observed != snapshot.CapturedAt || observed > now ||
                    observed <= now - Retention)
                {
                    series.Broken = true;
                    continue;
                }
                // Repeated publications and UI reads must never count a sensor sample twice.
                if (observed <= series.LastAccepted)
                {
                    if (observed < series.LastAccepted) series.Broken = true;
                    continue;
                }
                if (series.Broken || observed - series.LastAccepted >= TimeSpan.FromTicks(Math.Max(interval.Ticks, series.Interval.Ticks) * 3))
                    series.Segment++;
                var minute = FloorMinute(observed);
                if (series.Current is { } current && (current.Minute != minute || current.Segment != series.Segment))
                {
                    series.Minutes.Enqueue(current);
                    series.Current = null;
                }
                series.Current = series.Current is { } old
                    ? old with { Last = observed, Count = old.Count + 1, Sum = old.Sum + value,
                        Minimum = Math.Min(old.Minimum, value), Maximum = Math.Max(old.Maximum, value) }
                    : new(minute, series.Segment, observed, observed, 1, value, value, value);
                while (series.Peaks.Last is { } peak && peak.Value.Value <= value) series.Peaks.RemoveLast();
                series.Peaks.AddLast(new HistoryPeak(observed, value));
                series.LastAccepted = observed;
                series.Interval = interval;
                series.Broken = false;
            }
            foreach (var (id, series) in _series)
                if (!present.Contains(id)) series.Broken = true;
        }
    }

    public HistorySensor[] Sensors
    {
        get { lock (_gate) { Prune(_time.GetUtcNow()); return [.. _series.Values.Select(s => s.Sensor)]; } }
    }

    public double? GetMaximum(SensorSeriesId? id)
    {
        lock (_gate)
        {
            if (id is null || !_series.TryGetValue(id, out var series)) return null;
            series.Prune(_time.GetUtcNow());
            return series.Peaks.First?.Value.Value;
        }
    }

    public HistorySeries? GetSeries(SensorSeriesId? id)
    {
        lock (_gate)
        {
            if (id is null || !_series.TryGetValue(id, out var series)) return null;
            series.Prune(_time.GetUtcNow());
            return new(series.Sensor, series.CopyMinutes(), series.Peaks.First?.Value.Value);
        }
    }

    private static DateTimeOffset FloorMinute(DateTimeOffset time) => new(time.UtcTicks - time.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero);
    private void Prune(DateTimeOffset now)
    {
        foreach (var series in _series.Values) series.Prune(now);
        foreach (var id in _series.Where(p => p.Value.LastSeen <= now - Retention).Select(p => p.Key).ToArray()) _series.Remove(id);
    }

    private async Task WritePeriodicallyAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), _time);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false)) await CheckpointAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task CheckpointAsync()
    {
        if (_store is null) return;
        try
        {
            HistoryArchive archive;
            lock (_gate)
            {
                Prune(_time.GetUtcNow());
                archive = new(1, [.. _series.Values.Select(s => new HistorySeriesArchive(s.Sensor, s.LastSeen, s.CopyMinutes(), [.. s.Peaks]))]);
            }
            await _store.SaveAsync(archive).ConfigureAwait(false);
            lock (_gate) _saveError = null;
        }
        catch (Exception ex) { lock (_gate) _saveError = ex.Message; }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_dispose ??= Task.Run(StopAsync));
    }
    private async Task StopAsync()
    {
        if (_start is not null) await _start.ConfigureAwait(false);
        _stop.Cancel();
        if (_writer is not null) await _writer.ConfigureAwait(false);
        if (_start is not null) await CheckpointAsync().ConfigureAwait(false);
    }
}
