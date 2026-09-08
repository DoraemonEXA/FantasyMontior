using FantasyMontior.Core;
using Xunit;

namespace FantasyMontior.Tests;

public sealed class WidgetSourcesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static MonitoringSnapshot Snapshot => new(Now, Now, "Monitoring",
        [new("cpu", "CPU", "Cpu", "cpu"), new("child", "CCD", "Cpu", "cpu"),
         new("gpu1", "GPU", "GpuNvidia", "gpu1"), new("gpu2", "GPU", "GpuNvidia", "gpu2"),
         new("/ram", "Total Memory", "Memory", "/ram"), new("/vram", "Virtual Memory", "Memory", "/vram")],
        [new("total", "cpu", "CPU Total", "Load", "%", 0, Now),
         new("core", "cpu", "CPU Core #1", "Load", "%", 100, Now),
         new("temp", "child", "Core (Tctl/Tdie)", "Temperature", "°C", 50, Now),
         new("g1", "gpu1", "GPU Core", "Load", "%", 10, Now),
         new("g2", "gpu2", "GPU Core", "Load", "%", 90, Now),
         new("m", "/ram", "Memory", "Load", "%", 40, Now),
         new("v", "/vram", "Memory", "Load", "%", 80, Now)], []);

    [Fact]
    public void RolesStayWithinDeviceAndTraverseChildren()
    {
        Assert.Equal(0d, WidgetSources.Resolve(Snapshot, "cpu", "Cpu", "Load", null)!.Value);
        Assert.Equal(50d, WidgetSources.Resolve(Snapshot, "cpu", "Cpu", "Temperature", null)!.Value);
        Assert.Equal(90d, WidgetSources.Resolve(Snapshot, "gpu2", "GpuNvidia", "Load", null)!.Value);
        Assert.Equal(40d, WidgetSources.Resolve(Snapshot, "/ram", "Memory", "Load", null)!.Value);
        Assert.Equal(4, Snapshot.Hardware.Count(WidgetSources.IsWidgetDevice));
        Assert.Equal(WidgetSources.Resolve(Snapshot, "cpu", "Cpu", "Load", null),
            WidgetSources.Resolve(Snapshot with { Sensors = [.. Snapshot.Sensors.Reverse()] }, "cpu", "Cpu", "Load", null));
    }

    [Fact]
    public void UnknownAmbiguousAndMissingSelectionsNeverBecomeAnArbitraryReading()
    {
        Assert.Null(WidgetSources.Resolve(Snapshot, "cpu", "Cpu", "Load", "missing"));
        Assert.Null(WidgetSources.Resolve(Snapshot, "cpu", "Cpu", "Load", "g1"));
        var ambiguous = Snapshot with { Sensors = [.. Snapshot.Sensors, new("duplicate", "cpu", "CPU Total", "Load", "%", 10, Now)] };
        Assert.Null(WidgetSources.Resolve(ambiguous, "cpu", "Cpu", "Load", null));
        Assert.Equal(100d, WidgetSources.Resolve(ambiguous, "cpu", "Cpu", "Load", "core")!.Value);
        var unknown = Snapshot with { Sensors = [new("core", "cpu", "CPU Core #1", "Load", "%", 100, Now)] };
        Assert.Null(WidgetSources.Resolve(unknown, "cpu", "Cpu", "Load", null));
    }

    [Fact]
    public void CollisionKeysRemainDistinctAndSelectionDoesNotFallbackWhenDisconnected()
    {
        var snapshot = Snapshot with { Sensors = [
            new("same", "gpu1", "GPU Bus", "Load", "%", 10, Now, Identity: "same#bus"),
            new("same", "gpu1", "GPU Memory", "Load", "%", 70, Now, Identity: "same#memory")] };
        Assert.Equal(2, WidgetSources.Candidates(snapshot, "gpu1", "Load").Length);
        Assert.Equal(70d, WidgetSources.Resolve(snapshot, "gpu1", "GpuNvidia", "Load", "same#memory")!.Value);
        Assert.Null(WidgetSources.Resolve(snapshot, "gpu1", "GpuNvidia", "Load", "same"));
        Assert.Null(WidgetSources.Resolve(snapshot with { Hardware = [] }, "gpu1", "GpuNvidia", "Load", "same#memory"));
    }
}
