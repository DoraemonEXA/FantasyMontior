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
    {
        var candidates = Candidates(snapshot, deviceId, sensorKind);
        if (selectedKey is not null) return candidates.SingleOrDefault(s => s.Key == selectedKey);
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
