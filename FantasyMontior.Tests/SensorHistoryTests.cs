using FantasyMontior.Core;
using Xunit;

namespace FantasyMontior.Tests;

public sealed class SensorHistoryTests
{
    private static readonly DateTimeOffset Origin = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
    private static readonly SensorSeriesId Cpu = new("cpu", "load", "Load", "%");
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = Origin;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Store : ISensorHistoryStore
    {
        public HistoryArchive? Archive;
        public bool FailRead, FailWrite;
        public int Writes;
        public Task<HistoryArchive?> LoadAsync(CancellationToken cancellationToken = default) => FailRead
            ? throw new IOException("Test read failure") : Task.FromResult(Archive);
        public Task SaveAsync(HistoryArchive archive, CancellationToken cancellationToken = default)
        {
            if (FailWrite) throw new IOException("Test write failure");
            Archive = archive;
            Writes++;
            return Task.CompletedTask;
        }
    }
    private static MonitoringSnapshot Snapshot(DateTimeOffset time, double? value, string? error = null) => new(time, time, "Monitoring",
        [new("cpu", "TEST CPU", "Cpu", "cpu")], [new("load", "cpu", "CPU Total", "Load", "%", value, time, error)], []);
    private static void Record(SensorHistoryService history, Clock clock, double seconds, double? value, string? error = null)
    {
        clock.Now = Origin.AddSeconds(seconds);
        history.Record(Snapshot(clock.Now, value, error), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void AggregatesMinutesWithoutLosingSpikesOrDuplicatingPublications()
    {
        var clock = new Clock();
        var history = new SensorHistoryService(timeProvider: clock);
        Record(history, clock, 0, 20);
        Record(history, clock, 1, 95);
        Record(history, clock, 2, 35);
        history.Record(Snapshot(clock.Now, 35), TimeSpan.FromSeconds(1));
        var minute = Assert.Single(history.GetSeries(Cpu)!.Minutes);
        Assert.Equal(3, minute.Count);
        Assert.Equal(150, minute.Sum);
        Assert.Equal(50, minute.Average);
        Assert.Equal(20, minute.Minimum);
        Assert.Equal(95, minute.Maximum);
        Assert.Equal(95, history.GetMaximum(Cpu));
    }

    [Fact]
    public void MaximumExpiresAtExactSampleTimeRatherThanMinuteBoundary()
    {
        var clock = new Clock();
        var history = new SensorHistoryService(timeProvider: clock);
        Record(history, clock, 10, 95);
        Record(history, clock, 11, 60);
        clock.Now = Origin.AddHours(24).AddSeconds(9.999);
        Assert.Equal(95, history.GetMaximum(Cpu));
        clock.Now = Origin.AddHours(24).AddSeconds(10);
        Assert.Equal(60, history.GetMaximum(Cpu));
        clock.Now = Origin.AddHours(24).AddSeconds(11);
        Assert.Null(history.GetMaximum(Cpu));
        Assert.Empty(history.GetSeries(Cpu)!.Minutes);
    }

    [Fact]
    public void RejectsUnavailableNonfiniteAndFailedSamplesButPreservesZero()
    {
        var clock = new Clock();
        var history = new SensorHistoryService(timeProvider: clock);
        Record(history, clock, 0, 0);
        Record(history, clock, 1, null);
        Record(history, clock, 2, double.NaN);
        Record(history, clock, 3, double.PositiveInfinity);
        Record(history, clock, 4, 90, "Test failure");
        clock.Now = Origin.AddSeconds(5);
        history.Record(Snapshot(clock.Now, 90) with { Sensors = [new("load", "cpu", "CPU Total", "Load", "%", 90, Origin)] }, TimeSpan.FromSeconds(1));
        Assert.Equal(0, history.GetMaximum(Cpu));
        Assert.Equal(1, Assert.Single(history.GetSeries(Cpu)!.Minutes).Count);
    }

    [Fact]
    public void PreservesWithinMinuteFailureDisconnectSleepAndRediscoveryGaps()
    {
        var clock = new Clock();
        var history = new SensorHistoryService(timeProvider: clock);
        Record(history, clock, 0, 10);
        Record(history, clock, 1, null);
        Record(history, clock, 2, 20);
        clock.Now = Origin.AddSeconds(3);
        history.Record(Snapshot(clock.Now, 0) with { Sensors = [] }, TimeSpan.FromSeconds(1));
        Record(history, clock, 4, 30);
        Record(history, clock, 20, 40);
        Record(history, clock, 21, 40, "Rediscovering hardware");
        Record(history, clock, 22, 50);
        var minutes = history.GetSeries(Cpu)!.Minutes;
        Assert.Equal(5, minutes.Length);
        Assert.Equal(5, minutes.Select(m => m.Segment).Distinct().Count());
        Assert.Single(minutes.Select(m => m.Minute).Distinct());
    }

    [Fact]
    public void ChangingSamplingIntervalDoesNotInventASleepGap()
    {
        var clock = new Clock();
        var history = new SensorHistoryService(timeProvider: clock);
        Record(history, clock, 0, 10);
        clock.Now = Origin.AddSeconds(10);
        history.Record(Snapshot(clock.Now, 20), TimeSpan.FromSeconds(10));
        Assert.Equal(2, Assert.Single(history.GetSeries(Cpu)!.Minutes).Count);
    }

    [Fact]
    public void KeysSeparateDevicesCollisionsTypesAndUnits()
    {
        var clock = new Clock();
        var history = new SensorHistoryService(timeProvider: clock);
        var snapshot = Snapshot(clock.Now, 10);
        var load = snapshot.Sensors[0];
        history.Record(snapshot with { Sensors = [load, load with { HardwareId = "gpu" },
            load with { Identity = "load#Load:Compute", Value = 80 }, load with { Kind = "Temperature", Unit = "°C", Value = 65 },
            load with { Unit = "rpm", Value = 300 }] }, TimeSpan.FromSeconds(1));
        Assert.Equal(5, history.Sensors.Length);
        Assert.Equal(10, history.GetMaximum(Cpu));
        Assert.Equal(80, history.GetMaximum(Cpu with { SensorKey = "load#Load:Compute" }));
        Assert.Equal(65, history.GetMaximum(Cpu with { Kind = "Temperature", Unit = "°C" }));
    }

    [Fact]
    public async Task RestartRestoresMinuteRangesAndExactPeaksAndCreatesAGap()
    {
        var clock = new Clock();
        var store = new Store();
        var history = new SensorHistoryService(store, clock);
        await history.StartAsync();
        Record(history, clock, 10, 90);
        Record(history, clock, 11, 60);
        await history.DisposeAsync();
        Assert.Equal(1, store.Writes);
        await using var restored = new SensorHistoryService(store, clock);
        await restored.StartAsync();
        Assert.Equal(90, restored.GetMaximum(Cpu));
        Record(restored, clock, 12, 30);
        Assert.Equal(2, restored.GetSeries(Cpu)!.Minutes.Select(m => m.Segment).Distinct().Count());
        clock.Now = Origin.AddHours(24).AddSeconds(10);
        Assert.Equal(60, restored.GetMaximum(Cpu));
    }

    [Fact]
    public async Task PersistenceErrorsDoNotStopInMemoryRecording()
    {
        var store = new Store { FailRead = true, FailWrite = true };
        var clock = new Clock();
        var history = new SensorHistoryService(store, clock);
        await history.StartAsync();
        Record(history, clock, 0, 50);
        Assert.Contains("read failure", history.Status.LoadError);
        Assert.Equal(50, history.GetMaximum(Cpu));
        await history.DisposeAsync();
        Assert.Contains("write failure", history.Status.SaveError);
        Assert.Equal(50, history.GetMaximum(Cpu));
    }

    [Fact]
    public async Task InvalidArchiveIsRejectedWithoutPartiallyRestoringIt()
    {
        var clock = new Clock();
        var descriptor = new HistorySensor(Cpu, "TEST", "TEST", "cpu");
        var good = new HistorySeriesArchive(descriptor, Origin, [new(Origin, 1, Origin, Origin, 1, 10, 10, 10)], [new(Origin, 10)]);
        var store = new Store { Archive = new(1, [good, good with { Sensor = descriptor with { Id = Cpu with { Unit = "rpm" } },
            Minutes = [new(Origin, 1, Origin, Origin, 0, double.NaN, 10, 10)] }]) };
        await using var history = new SensorHistoryService(store, clock);
        await history.StartAsync();
        Assert.NotNull(history.Status.LoadError);
        Assert.Empty(history.Sensors);
        Record(history, clock, 1, 40);
        Assert.Equal(40, history.GetMaximum(Cpu));
    }

    [Fact]
    public async Task FileStoreRoundTripsAndReportsCorruptCompressedData()
    {
        var directory = Path.Combine(Path.GetTempPath(), "FantasyMontior-history-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new FileSensorHistoryStore(directory);
            var clock = new Clock();
            var history = new SensorHistoryService(store, clock);
            await history.StartAsync();
            Record(history, clock, 0, 23);
            await history.DisposeAsync();
            Assert.False(File.Exists(Path.Combine(directory, "history-v1.json.gz.tmp")));
            var saved = await store.LoadAsync();
            Assert.Equal(23, Assert.Single(saved!.Series).Peaks[0].Value);
            await File.WriteAllTextAsync(Path.Combine(directory, "history-v1.json.gz"), "TEST corrupt file");
            var corrupt = new SensorHistoryService(store, clock);
            await corrupt.StartAsync();
            Assert.NotNull(corrupt.Status.LoadError);
            await corrupt.DisposeAsync();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void TwentyFiveHoursOfSamplesHaveBoundedMinuteRetention()
    {
        var clock = new Clock();
        var history = new SensorHistoryService(timeProvider: clock);
        for (var second = 0; second <= 25 * 3600; second++) Record(history, clock, second, second % 101);
        var series = history.GetSeries(Cpu)!;
        Assert.InRange(series.Minutes.Length, 1440, 1441);
        Assert.All(series.Minutes, m => Assert.InRange(m.Count, 1, 60));
        Assert.Equal(100, series.Maximum);
        clock.Now += TimeSpan.FromHours(25);
        Assert.Empty(history.Sensors);
    }
}
