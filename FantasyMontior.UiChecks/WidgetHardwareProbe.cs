using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FantasyMontior.Core;
using FantasyMontior.ViewModels;

namespace FantasyMontior.UiChecks;

internal static class WidgetHardwareProbe
{
    internal static async Task RunAsync(int seconds, string output)
    {
        Directory.CreateDirectory(output);
        var settings = new SettingsViewModel(Path.Combine(output, "settings.json"));
        Localization.Apply(settings.Language);
        var backend = new CountedBackend();
        await using var monitor = new MonitoringService(() => backend);
        var historyStore = new FileSensorHistoryStore(Path.Combine(output, "history-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")));
        await using var session = new MonitoringSession(settings, monitor,
            history: new SensorHistoryService(historyStore));
        var observedPeaks = new Dictionary<SensorSeriesId, double>();
        var peakGate = new object();
        void Observe(MonitoringSnapshot snapshot, TimeSpan interval)
        {
            lock (peakGate)
                foreach (var sensor in snapshot.Sensors)
                    if (sensor.Error is null && sensor.LastSuccess == snapshot.CapturedAt && sensor.Value is { } value && double.IsFinite(value))
                    {
                        var id = SensorSeriesId.From(sensor);
                        observedPeaks[id] = observedPeaks.TryGetValue(id, out var peak) ? Math.Max(value, peak) : value;
                    }
        }
        monitor.SnapshotPublished += Observe;
        await using var shell = new WidgetApplication(session);
        shell.Start();
        if (!shell.GetWidget().IsDesktopAttached)
            throw new InvalidOperationException("Cannot measure a desktop widget that is not attached: " + session.Diagnostics.DesktopStatus);
        var watch = Stopwatch.StartNew();
        using var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        var maxWorkingSet = 0L;
        var maxPrivate = 0L;
        var updates = new HashSet<DateTimeOffset>();
        var samples = new List<object>();
        var errors = new HashSet<string>();
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            await Task.Delay(1000);
            if (!shell.GetWidget().IsDesktopAttached)
                throw new InvalidOperationException("Desktop attachment was lost during the widget probe: " + session.Diagnostics.DesktopStatus);
            var snapshot = monitor.Latest;
            if (snapshot.Sensors.Length > 0) updates.Add(snapshot.CapturedAt);
            foreach (var error in snapshot.Errors) errors.Add(error);
            process.Refresh();
            maxWorkingSet = Math.Max(maxWorkingSet, process.WorkingSet64);
            maxPrivate = Math.Max(maxPrivate, process.PrivateMemorySize64);
            if (samples.Count == 0 || watch.Elapsed.TotalSeconds >= samples.Count * 15)
            {
                var sample = new { Seconds = Math.Round(watch.Elapsed.TotalSeconds, 1), snapshot.Status,
                    Sensors = snapshot.Sensors.Length, WorkingSetBytes = process.WorkingSet64,
                    PrivateBytes = process.PrivateMemorySize64, CpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds };
                samples.Add(sample);
                Console.WriteLine(JsonSerializer.Serialize(sample));
            }
            await File.WriteAllTextAsync(Path.Combine(output, "snapshot.json"), Diagnostics.ToJson(snapshot, session.Diagnostics.Runtime, DateTimeOffset.UtcNow));
        }
        var duration = watch.Elapsed.TotalSeconds;
        var cpu = (process.TotalProcessorTime - cpuStart).TotalSeconds;
        // Exclude image export and diagnostic-window construction from the widget-only measurement.
        WidgetChecks.Render(session.Widget, output, "widget-hardware.png", 1, fixture: false);
        shell.OpenDiagnostics(false);
        await Task.Delay(250);
        var diagnostics = Application.Current.Windows.OfType<MainWindow>().Single();
        diagnostics.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(diagnostics.ActualWidth), (int)Math.Ceiling(diagnostics.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(diagnostics);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(output, "trends-hardware.png"))) encoder.Save(file);
        if (backend.Opens != 1) throw new InvalidOperationException("Diagnostics opened another hardware backend.");
        var close = Stopwatch.StartNew();
        await shell.DisposeAsync();
        close.Stop();
        monitor.SnapshotPublished -= Observe;
        foreach (var (id, peak) in observedPeaks)
            if (session.History.GetMaximum(id) != peak) throw new InvalidOperationException("Recorded peak did not match observed readings.");
        await using var restoredHistory = new SensorHistoryService(historyStore);
        await restoredHistory.StartAsync();
        if (restoredHistory.Status.LoadError is not null) throw new InvalidOperationException(restoredHistory.Status.LoadError);
        foreach (var (id, peak) in observedPeaks)
            if (restoredHistory.GetMaximum(id) != peak) throw new InvalidOperationException("Restored peak did not match observed readings.");
        await File.WriteAllTextAsync(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new
        {
            DurationSeconds = duration, Updates = updates.Count, CpuSeconds = cpu,
            AveragePercentOfOneCore = cpu / duration * 100, MaxWorkingSetBytes = maxWorkingSet,
            MaxPrivateBytes = maxPrivate, ShutdownMilliseconds = close.Elapsed.TotalMilliseconds,
            BackendOpens = backend.Opens, BackendCloses = backend.Closes,
            HistorySensorsVerified = observedPeaks.Count, HistoryRestored = true,
            Runtime = session.Diagnostics.Runtime, Samples = samples, Errors = errors,
            Note = "Full widget and tray in standalone WPF runner at existing privileges. Excludes image export and diagnostics from resource measurement. Not a manual interaction or sensor-accuracy acceptance."
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (updates.Count == 0 || backend.Closes != 1) throw new InvalidOperationException("No readings or incomplete hardware cleanup.");
    }
    private sealed class CountedBackend : IHardwareBackend
    {
        private readonly LibreHardwareBackend _backend = new();
        public int Opens, Closes;
        public IReadOnlyList<IMonitorHardware> Hardware => _backend.Hardware;
        public void Open() { Interlocked.Increment(ref Opens); _backend.Open(); }
        public void Dispose() { _backend.Dispose(); Interlocked.Increment(ref Closes); }
    }
}
