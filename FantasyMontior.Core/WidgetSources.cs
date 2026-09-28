namespace FantasyMontior.Core;

// Presentation roles are resolved against a specific device, never against collection order.
public static class WidgetSources
{
    public static bool IsWidgetDevice(HardwareInfo hardware) =>
        hardware.IsCpuOrGpu && hardware.Id == hardware.RootId ||
        hardware.Kind == "Memory" && hardware.Id == "/ram";

    public static SensorReading[] Candidates(MonitoringSnapshot snapshot, string deviceId, string kind)
    {
        var devices = snapshot.Hardware.Where(h => h.Id == deviceId || h.RootId == deviceId)
            .Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
        return snapshot.Sensors.Where(s => devices.Contains(s.HardwareId) && s.Kind == kind &&
            s.Unit == (kind == "Load" ? "%" : "°C")).OrderBy(s => s.Key, StringComparer.Ordinal).ToArray();
    }

    public static SensorReading? Resolve(MonitoringSnapshot snapshot, string deviceId, string hardwareKind,
        string sensorKind, string? selectedKey)
        => ResolveCandidates(Candidates(snapshot, deviceId, sensorKind), hardwareKind, sensorKind, selectedKey);

    public static SensorReading? ResolveCandidates(IReadOnlyList<SensorReading> candidates, string hardwareKind,
        string sensorKind, string? selectedKey)
    {
        if (selectedKey is not null)
        {
            var selected = candidates.Where(s => s.Key == selectedKey).Take(2).ToArray();
            return selected.Length == 1 ? selected[0] : null;
        }
        // Source labels corroborate the semantic role within an identified device and sensor type.
        // Unknown and ambiguous roles require an explicit selection rather than a core average.
        string[] roles = (hardwareKind, sensorKind) switch
        {
            ("Cpu", "Load") => ["CPU Total"],
            ("Cpu", "Temperature") => ["CPU Package", "Core (Tctl/Tdie)", "Core (Tdie)", "CPU (Tctl/Tdie)"],
            ("Memory", "Load") => ["Memory"],
            (_, "Load") when hardwareKind.StartsWith("Gpu", StringComparison.Ordinal) => ["GPU Core"],
            (_, "Temperature") when hardwareKind.StartsWith("Gpu", StringComparison.Ordinal) => ["GPU Core"],
            _ => []
        };
        foreach (var role in roles)
        {
            var matches = candidates.Where(s => s.Name == role).ToArray();
            if (matches.Length > 0) return matches.Length == 1 ? matches[0] : null;
        }
        return null;
    }
}
