using System.Collections.Immutable;

namespace FantasyMontior.Core;

public sealed class SnapshotCollector
{
    private MonitoringSnapshot _previous = MonitoringSnapshot.Empty;
    private readonly HashSet<string> _collidingIds = new(StringComparer.Ordinal);

    public MonitoringSnapshot Collect(IReadOnlyList<IMonitorHardware> roots, DateTimeOffset now)
    {
        var hardware = ImmutableArray.CreateBuilder<HardwareInfo>();
        var sensors = ImmutableArray.CreateBuilder<SensorReading>();
        var errors = ImmutableArray.CreateBuilder<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var successfulUpdates = 0;

        void Visit(IMonitorHardware node, string rootId)
        {
            if (!visited.Add(node.Id)) return;
            hardware.Add(new(node.Id, node.Name, node.Kind, rootId));
            try
            {
                node.Update();
                // Materialize before modifying the snapshot so a failed enumeration cannot publish a partial node.
                var raw = node.Sensors;
                foreach (var collision in raw.GroupBy(s => s.Id).Where(g => g.Count() > 1))
                {
                    _collidingIds.Add(collision.Key);
                    errors.Add($"Library identifier collision: {collision.Key}. Readings are kept separate by sensor type and source label.");
                }
                var readings = raw.Select(s => new SensorReading(s.Id, node.Id, s.Name, s.Kind,
                    s.Unit, s.Value is { } v && double.IsFinite(v) ? v : null,
                    s.Value is { } value && double.IsFinite(value) ? now : null,
                    Identity: _collidingIds.Contains(s.Id) ? $"{s.Id}#{Uri.EscapeDataString(s.Kind + ":" + s.Name)}" : null)).ToArray();
                if (readings.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count() != readings.Length)
                    throw new InvalidOperationException("Duplicate sensor identifiers and labels cannot be disambiguated reliably.");
                sensors.AddRange(readings);
                successfulUpdates++;
            }
            catch (Exception ex)
            {
                var error = $"{node.Name} ({node.Id}): {ex.GetType().Name}: {ex.Message}";
                errors.Add(error);
                sensors.AddRange(_previous.Sensors.Where(s => s.HardwareId == node.Id).Select(s => s with { Error = error }));
            }

            try
            {
                foreach (var child in node.Children) Visit(child, rootId);
            }
            catch (Exception ex)
            {
                errors.Add($"{node.Name}: child discovery failed: {ex.Message}");
            }
        }

        foreach (var root in roots) Visit(root, root.Id);
        if (hardware.Count == 0) errors.Add("No hardware detected. Check permissions and prerequisites, then retry discovery.");
        _previous = new(now, successfulUpdates > 0 ? now : _previous.LastSuccessfulRefresh,
            errors.Count == 0 ? "Monitoring" : successfulUpdates > 0 ? "Partial readings" : "Collection failed",
            hardware.ToImmutable(), sensors.ToImmutable(), errors.ToImmutable());
        return _previous;
    }
}
