using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;

namespace FantasyMontior.Core;

public sealed record RuntimeInfo(string OperatingSystem, string Architecture, string Runtime,
    string LibraryVersion, string Privileges, string PawnIo)
{
    public static RuntimeInfo Detect()
    {
        string privileges;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            privileges = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
                ? "Administrator" : "Standard user";
        }
        catch (Exception ex) { privileges = $"Unknown: {ex.Message}"; }
        string pawnIo;
        try { pawnIo = LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled ? $"Installed ({LibreHardwareMonitor.PawnIo.PawnIo.Version})" : "Not detected"; }
        catch (Exception ex) { pawnIo = $"Unknown: {ex.Message}"; }
        return new(RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription, typeof(Computer).Assembly.GetName().Version?.ToString() ?? "Unknown",
            privileges, pawnIo);
    }
}

public static class Diagnostics
{
    public static string ToJson(MonitoringSnapshot snapshot, RuntimeInfo runtime, DateTimeOffset now, TimeSpan? staleAfter = null) =>
        JsonSerializer.Serialize(new
        {
            ExportedAt = now, Environment = runtime, Snapshot = snapshot,
            ReadingStates = snapshot.Sensors.Select(s => new { s.Key, s.Id, State = s.StateAt(now, staleAfter).ToString() }),
            Note = "Live means the library returned a finite value. It does not establish accuracy. Without working PawnIO access, low-level CPU/motherboard readings can be missing or misleading, including zero temperatures. Workload comparison is required."
        }, new JsonSerializerOptions { WriteIndented = true });
}
