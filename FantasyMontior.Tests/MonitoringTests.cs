using System.Text.Json;
using System.IO;
using FantasyMontior.Core;
using LibreHardwareMonitor.Hardware;
using Xunit;

namespace FantasyMontior.Tests;

public sealed class FakeHardware(string id, string kind = "Cpu") : IMonitorHardware
{
    public string Id { get; } = id;
    public string Name { get; set; } = "Same display name";
    public string Kind { get; } = kind;
    public IReadOnlyList<IMonitorHardware> Children { get; set; } = [];
    public IReadOnlyList<RawSensor> Sensors { get; set; } = [];
    public Action? Updating { get; set; }
    public int Updates;
    public void Update() { Interlocked.Increment(ref Updates); Updating?.Invoke(); }
}

public sealed class FakeBackend(params IMonitorHardware[] nodes) : IHardwareBackend
{
    public Action? Opening { get; set; }
    public Action? Closing { get; set; }
    public int Opens;
    public int Closes;
    public IReadOnlyList<IMonitorHardware> Hardware => nodes;
    public void Open() { Interlocked.Increment(ref Opens); Opening?.Invoke(); }
    public void Dispose() { Interlocked.Increment(ref Closes); Closing?.Invoke(); }
}

public class MonitoringTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static RawSensor Reading(string id, double? value, string kind = "Load") => new(id, "Source label", kind, SensorUnits.For(kind), value);

    [Fact]
    public void TraversesNestedHardwareAndKeepsIdenticallyNamedGpusSeparate()
    {
        var child = new FakeHardware("/gpu/0/child", "GpuNvidia") { Sensors = [Reading("/gpu/0/child/load/0", 10)] };
        var first = new FakeHardware("/gpu/0", "GpuNvidia") { Children = [child] };
        var second = new FakeHardware("/gpu/1", "GpuNvidia") { Sensors = [Reading("/gpu/1/load/0", 20)] };
        var snapshot = new SnapshotCollector().Collect([first, second], Now);
        Assert.Equal(3, snapshot.Hardware.Length);
        Assert.Equal(2, snapshot.Sensors.Length);
        Assert.Equal("/gpu/0", snapshot.Hardware.Single(h => h.Id == child.Id).RootId);
        Assert.Equal(1, child.Updates);
        Assert.Equal(2, snapshot.Sensors.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void PreservesZeroAndRejectsMissingOrNonFiniteValues()
    {
        var node = new FakeHardware("/cpu") { Sensors = [Reading("zero", 0), Reading("missing", null), Reading("nan", double.NaN), Reading("infinity", double.PositiveInfinity)] };
        var snapshot = new SnapshotCollector().Collect([node], Now);
        Assert.Equal(0, snapshot.Sensors[0].Value);
        Assert.Equal(ReadingState.Live, snapshot.Sensors[0].StateAt(Now));
        Assert.All(snapshot.Sensors.Skip(1), s => { Assert.Null(s.Value); Assert.Equal(ReadingState.Unavailable, s.StateAt(Now)); });
    }

    [Fact]
    public void FailedNodeRetainsStaleValueWhileSiblingAndChildContinueAndRecoveryClearsError()
    {
        var child = new FakeHardware("/cpu/child") { Sensors = [Reading("child", 2)] };
        var node = new FakeHardware("/cpu") { Sensors = [Reading("cpu", 12)], Children = [child] };
        var sibling = new FakeHardware("/gpu", "GpuNvidia") { Sensors = [Reading("gpu", 45)] };
        var collector = new SnapshotCollector();
        collector.Collect([node, sibling], Now);
        node.Updating = () => throw new IOException("Disconnected");
        var failed = collector.Collect([node, sibling], Now.AddSeconds(1));
        var stale = failed.Sensors.Single(s => s.Id == "cpu");
        Assert.Equal(12, stale.Value);
        Assert.Equal(Now, stale.LastSuccess);
        Assert.Equal(ReadingState.Stale, stale.StateAt(Now.AddSeconds(1)));
        Assert.Single(failed.Errors);
        Assert.Equal("Partial readings", failed.Status);
        Assert.Equal(2, child.Updates);
        Assert.Equal(ReadingState.Live, failed.Sensors.Single(s => s.Id == "gpu").StateAt(Now.AddSeconds(1)));
        node.Updating = null;
        var recovered = collector.Collect([node, sibling], Now.AddSeconds(2));
        Assert.Empty(recovered.Errors);
        Assert.All(recovered.Sensors, s => Assert.Equal(ReadingState.Live, s.StateAt(Now.AddSeconds(2))));
    }

    [Fact]
    public void AgeMarksReadingsStaleAtThreeSecondsWithoutAnotherSnapshot()
    {
        var reading = new SensorReading("id", "cpu", "CPU Total", "Load", "%", 0, Now);
        Assert.Equal(ReadingState.Live, reading.StateAt(Now.AddMilliseconds(2999)));
        Assert.Equal(ReadingState.Stale, reading.StateAt(Now.AddSeconds(3)));
        Assert.Equal(ReadingState.Unavailable, (reading with { Value = null }).StateAt(Now.AddSeconds(5)));
    }

    [Fact]
    public void ReorderingSensorsUsesIdentityAndRemovedSensorsDisappear()
    {
        var node = new FakeHardware("cpu") { Sensors = [Reading("a", 1), Reading("b", 2)] };
        var collector = new SnapshotCollector();
        var before = collector.Collect([node], Now);
        node.Sensors = [Reading("b", 3), Reading("a", 4)];
        var after = collector.Collect([node], Now.AddSeconds(1));
        Assert.Equal(4, after.Sensors.Single(s => s.Id == "a").Value);
        Assert.Equal(1, before.Sensors.Single(s => s.Id == "a").Value);
        node.Sensors = [Reading("b", 5)];
        Assert.Single(collector.Collect([node], Now.AddSeconds(2)).Sensors);
    }

    [Fact]
    public void UnitsCoverEveryPinnedLibrarySensorType()
    {
        Assert.All(Enum.GetNames<SensorType>(), kind => Assert.NotEqual("Unknown", SensorUnits.For(kind)));
        Assert.Equal("°C", SensorUnits.For("Temperature"));
        Assert.Equal("%", SensorUnits.For("Load"));
        Assert.Equal("B/s", SensorUnits.For("Throughput"));
        Assert.Equal("mWh", SensorUnits.For("Energy"));
        Assert.Equal("ns", SensorUnits.For("Timing"));
    }

    [Fact]
    public void DiagnosticsIncludeEnvironmentErrorsIdentifiersAndExportTimeStaleness()
    {
        var node = new FakeHardware("cpu") { Sensors = [Reading("sensor", 0)] };
        var snapshot = new SnapshotCollector().Collect([node], Now) with { Errors = ["Example failure"] };
        var runtime = new RuntimeInfo("Windows test", "X64", ".NET test", "0.9.6", "Standard user", "Not detected");
        using var json = JsonDocument.Parse(Diagnostics.ToJson(snapshot, runtime, Now.AddSeconds(4)));
        Assert.Equal("Standard user", json.RootElement.GetProperty("Environment").GetProperty("Privileges").GetString());
        Assert.Equal("sensor", json.RootElement.GetProperty("Snapshot").GetProperty("Sensors")[0].GetProperty("Id").GetString());
        Assert.Equal("Stale", json.RootElement.GetProperty("ReadingStates")[0].GetProperty("State").GetString());
        Assert.Equal("Example failure", json.RootElement.GetProperty("Snapshot").GetProperty("Errors")[0].GetString());
    }

    [Fact]
    public async Task RetryWaitsForActiveUpdateAndClosesBeforeReopening()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var order = new List<string>();
        var active = 0;
        var peak = 0;
        var node = new FakeHardware("cpu") { Sensors = [Reading("load", 1)], Updating = () =>
        {
            peak = Math.Max(peak, Interlocked.Increment(ref active));
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test release missing");
            Interlocked.Decrement(ref active);
        }};
        var first = new FakeBackend(node) { Closing = () => order.Add("close-first") };
        var second = new FakeBackend(new FakeHardware("gpu", "GpuAmd")) { Opening = () => order.Add("open-second") };
        var creates = 0;
        await using var service = new MonitoringService(() => Interlocked.Increment(ref creates) == 1 ? first : second, TimeSpan.FromMilliseconds(10));
        service.Start();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            service.RetryDiscovery();
            service.RetryDiscovery();
            Assert.Equal(1, creates);
        }
        finally { release.Set(); }
        await WaitUntil(() => Volatile.Read(ref second.Opens) == 1);
        await service.DisposeAsync();
        Assert.Equal(1, peak);
        Assert.Equal(new[] { "close-first", "open-second" }, order);
        Assert.Equal(1, first.Closes);
        Assert.Equal(1, second.Closes);
    }

    [Fact]
    public async Task ShutdownWaitsForHardwareAndNeverClosesDuringUpdate()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var node = new FakeHardware("cpu") { Updating = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); } };
        var backend = new FakeBackend(node);
        var service = new MonitoringService(() => backend);
        service.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var stop = service.DisposeAsync().AsTask();
        try { Assert.False(stop.IsCompleted); Assert.Equal(0, backend.Closes); }
        finally { release.Set(); }
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        await service.DisposeAsync();
        Assert.Equal(1, backend.Closes);
        Assert.Equal("Stopped", service.Latest.Status);
    }

    [Fact]
    public async Task SlowDiscoveryRunsOffCallerAndShutdownWaitsWithoutCollecting()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var node = new FakeHardware("cpu");
        var backend = new FakeBackend(node) { Opening = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test release missing");
        }};
        await using var service = new MonitoringService(() => backend);
        service.Start();
        Task stop;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal("Initializing", service.Latest.Status);
            Assert.Empty(service.Latest.Sensors);
            stop = service.DisposeAsync().AsTask();
            Assert.False(stop.IsCompleted);
            Assert.Equal(0, backend.Closes);
        }
        finally { release.Set(); }
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, node.Updates);
        Assert.Equal(1, backend.Closes);
        Assert.Equal("Stopped", service.Latest.Status);
    }

    [Fact]
    public async Task FailedOpenIsCleanedUpAndManualRetryRecovers()
    {
        var first = new FakeBackend() { Opening = () => throw new UnauthorizedAccessException("No access") };
        var second = new FakeBackend(new FakeHardware("gpu", "GpuNvidia") { Sensors = [Reading("gpu/load", 0)] });
        var creates = 0;
        await using var service = new MonitoringService(() => ++creates == 1 ? first : second, TimeSpan.FromMilliseconds(10));
        service.Start();
        await WaitUntil(() => service.Latest.Status == "Collection failed");
        Assert.Contains("No access", service.Latest.Errors[0]);
        service.RetryDiscovery();
        await WaitUntil(() => service.Latest.Status == "Monitoring");
        Assert.Equal(1, first.Closes);
        Assert.Single(service.Latest.Sensors);
        Assert.Empty(service.Latest.Errors);
    }

    [Fact]
    public void LibraryIdentifierCollisionKeepsBothReadingsStableAcrossReorder()
    {
        var bus = new RawSensor("gpu/load/3", "GPU Bus", "Load", "%", 0);
        var memory = bus with { Name = "GPU Memory", Value = 25 };
        var node = new FakeHardware("gpu", "GpuNvidia") { Sensors = [bus, memory] };
        var collector = new SnapshotCollector();
        var first = collector.Collect([node], Now);
        Assert.Equal(2, first.Sensors.Select(s => s.Key).Distinct().Count());
        Assert.All(first.Sensors, s => Assert.Equal("gpu/load/3", s.Id));
        Assert.Contains("collision", Assert.Single(first.Errors));
        node.Sensors = [memory with { Value = 30 }, bus];
        var second = collector.Collect([node], Now.AddSeconds(1));
        Assert.Equal(first.Sensors[0].Key, second.Sensors[1].Key);
        Assert.Equal(first.Sensors[1].Key, second.Sensors[0].Key);
        node.Sensors = [memory];
        Assert.Equal(first.Sensors[1].Key, Assert.Single(collector.Collect([node], Now.AddSeconds(2)).Sensors).Key);
    }

    [Fact]
    public async Task FailedCleanupPreventsOpeningAnotherComputerUntilRetrySucceeds()
    {
        var fail = true;
        var first = new FakeBackend(new FakeHardware("cpu"))
        {
            Closing = () => { if (fail) throw new IOException("Busy driver"); }
        };
        var second = new FakeBackend(new FakeHardware("gpu", "GpuNvidia"));
        var creates = 0;
        await using var service = new MonitoringService(() => Interlocked.Increment(ref creates) == 1 ? first : second, TimeSpan.FromMilliseconds(10));
        service.Start();
        await WaitUntil(() => service.Latest.Status == "Monitoring");
        service.RetryDiscovery();
        await WaitUntil(() => service.Latest.Errors.Any(e => e.Contains("Busy driver")));
        Assert.Equal(1, creates);
        fail = false;
        service.RetryDiscovery();
        await WaitUntil(() => Volatile.Read(ref second.Opens) == 1);
        Assert.Equal(2, creates);
    }

    [Fact]
    public void EmptyHardwareDoesNotClaimSuccessfulMonitoring()
    {
        var snapshot = new SnapshotCollector().Collect([], Now);
        Assert.Equal("Collection failed", snapshot.Status);
        Assert.Null(snapshot.LastSuccessfulRefresh);
        Assert.NotEmpty(snapshot.Errors);
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task ChangingIntervalWakesExistingWorkerWithoutReopeningHardware()
    {
        var updates = 0;
        var backend = new FakeBackend(new FakeHardware("cpu") { Updating = () => Interlocked.Increment(ref updates) });
        await using var service = new MonitoringService(() => backend, TimeSpan.FromSeconds(10));
        service.Start();
        await WaitUntil(() => Volatile.Read(ref updates) == 1);
        service.SetInterval(TimeSpan.FromSeconds(1));
        await WaitUntil(() => Volatile.Read(ref updates) >= 2);
        Assert.Equal(1, backend.Opens);
        Assert.Equal(TimeSpan.FromSeconds(1), service.Interval);
        Assert.Throws<ArgumentOutOfRangeException>(() => service.SetInterval(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.SetInterval(TimeSpan.FromSeconds(11)));
    }

    [Fact]
    public void LongerSamplingIntervalPreservesMissingAndErrorStates()
    {
        var reading = new SensorReading("load", "cpu", "Load", "Load", "%", 12, Now);
        var threshold = TimeSpan.FromSeconds(15);
        Assert.Equal(ReadingState.Live, reading.StateAt(Now.AddSeconds(6), threshold));
        Assert.Equal(ReadingState.Stale, reading.StateAt(Now.AddSeconds(15), threshold));
        Assert.Equal(ReadingState.Unavailable, (reading with { Value = null }).StateAt(Now, threshold));
        Assert.Equal(ReadingState.Stale, (reading with { Error = "failed" }).StateAt(Now, threshold));
    }
}
