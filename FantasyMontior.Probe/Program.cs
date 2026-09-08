using System.Diagnostics;
using System.Text.Json;
using FantasyMontior.Core;

if (args.Length != 2 || !int.TryParse(args[0], out var seconds) || seconds is < 1 or > 3600)
{
    Console.Error.WriteLine("Usage: FantasyMontior.Probe <seconds: 1..3600> <output-directory>");
    return 2;
}

Directory.CreateDirectory(args[1]);
var runtime = RuntimeInfo.Detect();
Console.WriteLine(JsonSerializer.Serialize(runtime));
await using var monitor = new MonitoringService();
monitor.Start();
var clock = Stopwatch.StartNew();
var process = Process.GetCurrentProcess();
var cpuStart = process.TotalProcessorTime;
var samples = new List<object>();
var ranges = new Dictionary<string, (double Min, double Max, int Count)>();
var errors = new HashSet<string>();
var captured = DateTimeOffset.MinValue;
var updates = 0;
var maxWorkingSet = 0L;
var maxPrivateBytes = 0L;
var nextReport = 0;
while (clock.Elapsed.TotalSeconds < seconds)
{
    await Task.Delay(1000);
    var snapshot = monitor.Latest;
    if (snapshot.CapturedAt != captured)
    {
        captured = snapshot.CapturedAt;
        updates++;
        foreach (var sensor in snapshot.Sensors.Where(s => s.StateAt(DateTimeOffset.UtcNow) == ReadingState.Live))
        {
            var value = sensor.Value!.Value;
            ranges[sensor.Key] = ranges.TryGetValue(sensor.Key, out var old)
                ? (Math.Min(old.Min, value), Math.Max(old.Max, value), old.Count + 1) : (value, value, 1);
        }
    }
    foreach (var error in snapshot.Errors) errors.Add(error);
    process.Refresh();
    maxWorkingSet = Math.Max(maxWorkingSet, process.WorkingSet64);
    maxPrivateBytes = Math.Max(maxPrivateBytes, process.PrivateMemorySize64);
    if (clock.Elapsed.TotalSeconds >= nextReport)
    {
        var sample = new { Seconds = Math.Round(clock.Elapsed.TotalSeconds), snapshot.Status,
            Sensors = snapshot.Sensors.Length, WorkingSet = process.WorkingSet64, PrivateBytes = process.PrivateMemorySize64,
            CpuSeconds = process.TotalProcessorTime.TotalSeconds - cpuStart.TotalSeconds };
        samples.Add(sample);
        Console.WriteLine(JsonSerializer.Serialize(sample));
        nextReport += 30;
    }
    // Keep current evidence available even if a native driver call later blocks shutdown.
    await File.WriteAllTextAsync(Path.Combine(args[1], "snapshot.json"), Diagnostics.ToJson(snapshot, runtime, DateTimeOffset.UtcNow));
}
var final = monitor.Latest;
var close = Stopwatch.StartNew();
await monitor.DisposeAsync();
close.Stop();
await File.WriteAllTextAsync(Path.Combine(args[1], "summary.json"), JsonSerializer.Serialize(new
{
    DurationSeconds = clock.Elapsed.TotalSeconds, Updates = updates, MaxWorkingSetBytes = maxWorkingSet,
    MaxPrivateBytes = maxPrivateBytes, ShutdownMilliseconds = close.Elapsed.TotalMilliseconds,
    Runtime = runtime, Samples = samples, Errors = errors,
    SensorRanges = ranges.Select(pair => new { Id = pair.Key, pair.Value.Min, pair.Value.Max, Samples = pair.Value.Count }),
    RequiredHardware = final.Hardware.Where(h => h.IsCpuOrGpu).Select(h => new
    {
        h.Id, h.Name, h.Kind,
        Sensors = final.Sensors.Where(s => s.HardwareId == h.Id && (s.Kind is "Load" or "Temperature"))
    })
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Probe finished. {updates} snapshots observed; shutdown {close.Elapsed.TotalMilliseconds:0} ms.");
return final.Sensors.Any(s => s.StateAt(DateTimeOffset.UtcNow) == ReadingState.Live) ? 0 : 1;
