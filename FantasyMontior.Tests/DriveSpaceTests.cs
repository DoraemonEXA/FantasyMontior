using FantasyMontior.Core;
using Xunit;

namespace FantasyMontior.Tests;

public sealed class DriveSpaceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private const long GiB = 1024L * 1024 * 1024;
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
    private sealed class Reader : IDriveSpaceReader
    {
        public DriveDescriptor[] Drives = [new(@"C:\", DriveType.Fixed, true)];
        public DriveCapacity Capacity = new(500 * GiB, 160 * GiB);
        public Exception? Error;
        public Exception? DiscoveryError;
        public int Discoveries;
        public string? ReadRoot;
        public IReadOnlyList<DriveDescriptor> GetDrives()
        {
            Discoveries++;
            if (DiscoveryError is { } error) throw error;
            return Drives;
        }
        public DriveCapacity Read(string rootPath)
        {
            ReadRoot = rootPath;
            if (Error is { } error) throw error;
            return Capacity;
        }
    }

    [Fact]
    public void PrefersCEvenWhenNotReadyAndNeverSwitchesDuringPolling()
    {
        var reader = new Reader { Drives = [new(@"D:\", DriveType.Fixed, true), new(@"c:\", DriveType.Fixed, false)] };
        var disk = DriveSpaceHardware.Discover(reader);
        Assert.Equal("C:", disk.Name);
        reader.Error = new IOException("Drive unavailable");
        Assert.Throws<IOException>(disk.Update);
        reader.Drives = [new(@"D:\", DriveType.Fixed, true)];
        reader.Error = null;
        disk.Update();
        Assert.Equal(@"C:\", reader.ReadRoot);
        Assert.Equal(1, reader.Discoveries);
        Assert.Equal("D:", DriveSpaceHardware.Discover(reader).Name);
    }

    [Fact]
    public void FallbackIsFirstReadyFixedDriveRegardlessOfEnumerationOrder()
    {
        var reader = new Reader { Drives =
        [
            new(@"Z:\", DriveType.Network, true), new(@"A:\", DriveType.Removable, true),
            new(@"B:\", DriveType.CDRom, true), new(@"D:\", DriveType.Fixed, false),
            new(@"F:\", DriveType.Fixed, true), new(@"E:\", DriveType.Fixed, true)
        ] };
        var first = DriveSpaceHardware.Discover(reader);
        reader.Drives = [.. reader.Drives.Reverse()];
        var second = DriveSpaceHardware.Discover(reader);
        Assert.Equal("E:", first.Name);
        Assert.Equal(first.Id, second.Id);
        first.Update();
        Assert.Equal(@"E:\", reader.ReadRoot);
    }

    [Theory]
    [InlineData(100, 0)]
    [InlineData(75, 25)]
    [InlineData(0, 100)]
    public void PublishesOccupiedPercentageAndBinaryCapacity(long freeGiB, double percent)
    {
        var disk = DriveSpaceHardware.Discover(new Reader { Capacity = new(100 * GiB, freeGiB * GiB) });
        var snapshot = new SnapshotCollector().Collect([disk], Now);
        Assert.Equal("Monitoring", snapshot.Status);
        var usage = WidgetSources.Resolve(snapshot, disk.Id, disk.Kind, "Load", null)!;
        Assert.Equal(percent, usage.Value);
        Assert.Equal("%", usage.Unit);
        Assert.Equal(ReadingState.Live, usage.StateAt(Now));
        Assert.Equal(100 - freeGiB, snapshot.Sensors.Single(s => s.Id == disk.Id + DriveSpaceHardware.UsedSuffix).Value);
        Assert.Equal(100, snapshot.Sensors.Single(s => s.Id == disk.Id + DriveSpaceHardware.TotalSuffix).Value);
        Assert.All(snapshot.Sensors.Where(s => s.Kind == "Data"), s => Assert.Equal("GiB", s.Unit));
        Assert.Single(snapshot.Hardware.Where(WidgetSources.IsWidgetDevice));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(100, -1)]
    [InlineData(100, 101)]
    public void InvalidCapacityIsUnavailableInsteadOfZero(long total, long free)
    {
        var disk = DriveSpaceHardware.Discover(new Reader { Capacity = new(total, free) });
        var snapshot = new SnapshotCollector().Collect([disk], Now);
        Assert.Empty(snapshot.Sensors);
        Assert.Null(snapshot.LastSuccessfulRefresh);
        Assert.Contains("invalid capacity", Assert.Single(snapshot.Errors));
    }

    [Fact]
    public void MissingOrFailedDiscoveryDoesNotStopOtherHardware()
    {
        var reader = new Reader { Drives = [] };
        foreach (var error in new Exception?[] { null, new IOException("Discovery failed"), new UnauthorizedAccessException("Denied") })
        {
            reader.DiscoveryError = error;
            var disk = DriveSpaceHardware.Discover(reader);
            var cpu = new FakeHardware("cpu") { Sensors = [new("cpu/load", "CPU Total", "Load", "%", 20)] };
            var snapshot = new SnapshotCollector().Collect([disk, cpu], Now);
            Assert.Equal("Partial readings", snapshot.Status);
            Assert.Single(snapshot.Errors);
            Assert.Equal("cpu/load", Assert.Single(snapshot.Sensors).Id);
            Assert.Null(WidgetSources.Resolve(snapshot, disk.Id, disk.Kind, "Load", null));
        }
    }

    [Fact]
    public async Task ReadFailureKeepsStaleValuesWithoutRecordingThemAndRecoveryResumesHistory()
    {
        var reader = new Reader();
        var disk = DriveSpaceHardware.Discover(reader);
        var collector = new SnapshotCollector();
        await using var history = new SensorHistoryService(timeProvider: new Clock(Now.AddSeconds(2)));
        var first = collector.Collect([disk], Now);
        var id = SensorSeriesId.From(first.Sensors.Single(s => s.Kind == "Load"));
        history.Record(first, TimeSpan.FromSeconds(1));
        reader.Error = new UnauthorizedAccessException("Denied");
        var stale = collector.Collect([disk], Now.AddSeconds(1));
        history.Record(stale, TimeSpan.FromSeconds(1));
        Assert.All(stale.Sensors, s =>
        {
            Assert.Equal(Now, s.LastSuccess);
            Assert.Equal(ReadingState.Stale, s.StateAt(stale.CapturedAt));
        });
        Assert.Equal(1, history.GetSeries(id)!.Minutes.Sum(m => m.Count));
        Assert.Equal(68, history.GetMaximum(id));
        reader.Error = null;
        reader.Capacity = new(500 * GiB, 100 * GiB);
        var live = collector.Collect([disk], Now.AddSeconds(2));
        history.Record(live, TimeSpan.FromSeconds(1));
        Assert.Empty(live.Errors);
        Assert.All(live.Sensors, s => Assert.Equal(ReadingState.Live, s.StateAt(live.CapturedAt)));
        Assert.Equal(2, history.GetSeries(id)!.Minutes.Sum(m => m.Count));
        Assert.Equal(80, history.GetMaximum(id));
    }

    [Fact]
    public async Task RediscoveryKeepsDifferentDriveHistoriesSeparateFromPhysicalStorage()
    {
        var reader = new Reader();
        var first = new SnapshotCollector().Collect([DriveSpaceHardware.Discover(reader)], Now);
        reader.Drives = [new(@"D:\", DriveType.Fixed, true)];
        reader.Capacity = new(500 * GiB, 400 * GiB);
        var second = new SnapshotCollector().Collect([DriveSpaceHardware.Discover(reader)], Now.AddSeconds(1));
        await using var history = new SensorHistoryService(timeProvider: new Clock(Now.AddSeconds(1)));
        history.Record(first, TimeSpan.FromSeconds(1));
        history.Record(second, TimeSpan.FromSeconds(1));
        var firstId = SensorSeriesId.From(first.Sensors.Single(s => s.Kind == "Load"));
        var secondId = SensorSeriesId.From(second.Sensors.Single(s => s.Kind == "Load"));
        Assert.NotEqual(firstId, secondId);
        Assert.Equal(68, history.GetMaximum(firstId));
        Assert.Equal(20, history.GetMaximum(secondId));
        Assert.False(WidgetSources.IsWidgetDevice(new("/hdd/0", "Physical disk", "Storage", "/hdd/0")));
        Assert.Null(WidgetSources.Resolve(second, firstId.HardwareId, DriveSpaceHardware.HardwareKind, "Load", null));
    }
}
