using System.Collections.Immutable;
using System.Globalization;

namespace FantasyMontior.Core;

public sealed record HardwareInfo(string Id, string Name, string Kind, string RootId)
{
    public bool IsCpuOrGpu => Kind is "Cpu" or "GpuNvidia" or "GpuAmd" or "GpuIntel";
}

public enum ReadingState { Live, Unavailable, Stale }

public sealed record SensorReading(string Id, string HardwareId, string Name, string Kind,
    string Unit, double? Value, DateTimeOffset? LastSuccess, string? Error = null, string? Identity = null)
{
    public string Key => Identity ?? Id;
    public ReadingState StateAt(DateTimeOffset now, TimeSpan? staleAfter = null) => Value is null ? ReadingState.Unavailable
        : Error is not null || LastSuccess is null || now - LastSuccess >= (staleAfter ?? TimeSpan.FromSeconds(3))
            ? ReadingState.Stale : ReadingState.Live;

    public string FormattedValue => Value?.ToString("0.##", CultureInfo.CurrentCulture) ?? "Unavailable";
}

public sealed record MonitoringSnapshot(DateTimeOffset CapturedAt, DateTimeOffset? LastSuccessfulRefresh,
    string Status, ImmutableArray<HardwareInfo> Hardware, ImmutableArray<SensorReading> Sensors,
    ImmutableArray<string> Errors)
{
    public static MonitoringSnapshot Empty { get; } = new(DateTimeOffset.UtcNow, null,
        "Initializing", [], [], []);
}

public sealed record RawSensor(string Id, string Name, string Kind, string Unit, double? Value);

// A narrow boundary for hardware-independent traversal and lifecycle tests.
public interface IMonitorHardware
{
    string Id { get; }
    string Name { get; }
    string Kind { get; }
    IReadOnlyList<IMonitorHardware> Children { get; }
    IReadOnlyList<RawSensor> Sensors { get; }
    void Update();
}

public interface IHardwareBackend : IDisposable
{
    void Open();
    IReadOnlyList<IMonitorHardware> Hardware { get; }
}
