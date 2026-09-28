using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FantasyMontior.Core;
using FantasyMontior.ViewModels;
using Xunit;

namespace FantasyMontior.UiChecks;

internal static class TrendsChecks
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Store : ISensorHistoryStore
    {
        public HistoryArchive? Saved;
        public Task<HistoryArchive?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<HistoryArchive?>(null);
        public Task SaveAsync(HistoryArchive archive, CancellationToken cancellationToken = default) { Saved = archive; return Task.CompletedTask; }
    }
    internal static async Task VerifyAsync(string output)
    {
        var settingsPath = Path.Combine(output, "trends-fixture-settings.json");
        File.WriteAllText(settingsPath, "{\"Language\":\"en\",\"SampleSeconds\":10}");
        var settings = new SettingsViewModel(settingsPath);
        Localization.Apply("en");
        var now = DateTimeOffset.UtcNow;
        var clock = new Clock { Now = now - TimeSpan.FromHours(24) };
        await using var history = new SensorHistoryService(timeProvider: clock);
        MonitoringSnapshot snapshot = MonitoringSnapshot.Empty;
        for (var second = 0; second <= 86400; second += 10)
        {
            clock.Now = now - TimeSpan.FromHours(24) + TimeSpan.FromSeconds(second);
            snapshot = Fixture(clock.Now, second);
            if (second is >= 28000 and <= 32000) snapshot = snapshot with { Sensors = [] };
            history.Record(snapshot, TimeSpan.FromSeconds(10));
        }
        var runtime = new RuntimeInfo("TEST OS", "X64", "TEST", "0.9.6", "Standard user", "Installed (TEST)");
        await using var session = new MonitoringSession(settings, new MonitoringService(() => new FakeBackend()), runtime, history);
        session.Diagnostics.Apply(snapshot, now);
        session.Widget.Apply(snapshot, now);
        session.Diagnostics.Notice = "TEST DATA · 24-HOUR LAYOUT FIXTURE · Not real measurements";
        var model = session.Diagnostics.Trends!;
        var window = new MainWindow(session);
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        var host = new Window { Content = root, Width = 1080, Height = 900, Left = -10000, Top = -10000,
            ShowActivated = false, ShowInTaskbar = false, Background = window.Background };
        root.DataContext = session.Diagnostics;
        TextElementFont(root, window);
        host.Show();
        host.UpdateLayout();
        model.Refresh(now, true);
        var tabs = Descendants<TabControl>(root).Single();
        Assert.Equal("TrendsTab", ((TabItem)tabs.SelectedItem).Name);
        Assert.True(model.IsActive);
        Assert.Equal(3, model.Devices.Count);
        Assert.Equal(6, model.Sensors.Count);
        Assert.NotEqual("—", session.Widget.Rows[0].UsageMaximum);
        Render(root, 1080, 900, 1, Path.Combine(output, "trends-fixture-en.png"));
        var chart = Descendants<SensorTrendChart>(root).First(c => c.Data?.Series?.Sensor.Id == session.Widget.Rows[0].UsageHistoryId);
        Assert.Equal((0d, 100d), chart.AxisRange);
        var plot = chart.PlotBounds;
        var sampleTime = now.AddHours(-1);
        var hover = chart.HitTestMinute(new(plot.Left + plot.Width * 23 / 24, plot.Top + plot.Height / 2));
        Assert.NotNull(hover);
        Assert.Equal(sampleTime.Hour, hover.Value.Minute.Hour);
        Assert.Null(chart.HitTestMinute(new(plot.Left + plot.Width * 30000 / 86400, plot.Top + plot.Height / 2)));
        var before = chart.DrawingBuildCount;
        model.Refresh(now.AddMilliseconds(300));
        root.UpdateLayout();
        Assert.Equal(before, chart.DrawingBuildCount);

        // Source changes select the exact recorded series, including an alternative GPU engine.
        var gpu = session.Widget.Rows.Single(r => r.Id == "gpu");
        gpu.UsageKey = "gpu/encode";
        session.Widget.Apply(snapshot, now);
        model.Refresh(now, true);
        Assert.Equal("gpu/encode", model.Devices.Single(d => d.Row == gpu).Usage.Id!.SensorKey);
        model.Select(model.Devices.Single(d => d.Row == gpu).Usage);
        Assert.Equal("gpu/encode", model.SelectedSensor!.Id.SensorKey);
        model.Filter = "encode";
        Assert.Single(model.SensorView.Cast<HistorySensorChoice>());
        model.Filter = "no matching sensor";
        Assert.Empty(model.SensorView.Cast<HistorySensorChoice>());
        Assert.Equal("gpu/encode", model.SelectedSensor.Id.SensorKey);
        model.Filter = "";
        gpu.UsageKey = "";
        session.Widget.Apply(snapshot, now);
        model.Refresh(now, true);
        model.Select(model.Devices.First().Temperature);
        root.UpdateLayout();
        var trends = Descendants<TrendsView>(root).Single();
        var scroller = Descendants<ScrollViewer>(trends).First();
        scroller.ScrollToEnd();
        Render(root, 1080, 900, 1, Path.Combine(output, "trends-detail-fixture-en.png"));
        var detail = Descendants<SensorTrendChart>(root).Single(c => !c.IsCompact);
        Assert.True(detail.AxisRange.Maximum > history.GetSeries(session.Widget.Rows[0].TemperatureHistoryId)!.Minutes.Max(m => m.Maximum));

        foreach (var language in new[] { "en", "zh-CN" })
        foreach (var scale in new[] { 1d, 1.5, 2d })
        {
            settings.Language = language;
            session.Diagnostics.Apply(snapshot, now);
            session.Widget.Apply(snapshot, now);
            model.Refresh(now, true);
            scroller.ScrollToTop();
            Render(root, 760, 640, scale, Path.Combine(output, $"trends-fixture-{language}-minimum-{scale * 100:0}.png"));
            scroller.ScrollToEnd();
            Render(root, 760, 640, scale, Path.Combine(output, $"trends-detail-fixture-{language}-{scale * 100:0}.png"));
        }
        settings.Language = "en";
        session.Widget.Apply(snapshot with { Sensors = [], Hardware = [] }, now.AddMinutes(1));
        model.Refresh(now.AddMinutes(1), true);
        Assert.Equal(6, model.Sensors.Count);
        Assert.NotNull(model.Detail.Data.Series);
        Assert.NotEqual("—", session.Widget.Rows[0].UsageMaximum);
        scroller.ScrollToTop();
        Render(root, 1080, 900, 1, Path.Combine(output, "trends-disconnected-fixture.png"));

        var multiGpu = snapshot with
        {
            Hardware = [.. snapshot.Hardware, new("gpu2", "TEST SECOND GPU", "GpuAmd", "gpu2")],
            Sensors = [.. snapshot.Sensors, new("gpu2/load", "gpu2", "GPU Core", "Load", "%", 11, now)]
        };
        history.Record(multiGpu, TimeSpan.FromSeconds(10));
        session.Widget.Apply(multiGpu, now);
        model.Refresh(now, true);
        Assert.Equal(4, model.Devices.Count);
        Assert.Equal("11%", model.Devices.Single(d => d.Row.Id == "gpu2").Row.UsageMaximum);
        Assert.NotEqual(model.Devices.Single(d => d.Row.Id == "gpu").Usage.Id, model.Devices.Single(d => d.Row.Id == "gpu2").Usage.Id);
        Render(root, 1080, 900, 1, Path.Combine(output, "trends-multi-gpu-fixture.png"));
        host.WindowState = WindowState.Minimized;
        Assert.False(model.IsActive);
        host.WindowState = WindowState.Normal;
        Assert.True(model.IsActive);

        // An inactive tab does no history projection or drawing work, even if the session refreshes.
        var dataBeforeHidden = model.Detail.Data;
        window.OpenSettings();
        root.UpdateLayout();
        Assert.Equal("SettingsTab", ((TabItem)tabs.SelectedItem).Name);
        Assert.False(model.IsActive);
        var buildsBeforeHidden = detail.DrawingBuildCount;
        model.Refresh(now.AddMinutes(2));
        Assert.Same(dataBeforeHidden, model.Detail.Data);
        Assert.Equal(buildsBeforeHidden, detail.DrawingBuildCount);

        await using var emptyHistory = new SensorHistoryService();
        var emptyWidget = new WidgetViewModel(settings, emptyHistory);
        emptyWidget.Apply(MonitoringSnapshot.Empty, now);
        var emptyModel = new TrendsViewModel(emptyHistory, emptyWidget);
        var emptyView = new TrendsView { DataContext = emptyModel };
        var emptyPanel = new Grid { Background = Brushes.White };
        emptyPanel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        emptyPanel.RowDefinitions.Add(new());
        emptyPanel.Children.Add(new TextBlock { Text = "TEST DATA · EMPTY-HISTORY FIXTURE", Margin = new Thickness(12) });
        Grid.SetRow(emptyView, 1);
        emptyPanel.Children.Add(emptyView);
        host.Content = emptyPanel;
        host.UpdateLayout();
        Assert.Empty(emptyModel.Sensors);
        Assert.Equal("—", emptyModel.DetailMaximum);
        Assert.All(Descendants<SensorTrendChart>(emptyView), c => Assert.Null(c.Data?.Series));
        Render(emptyPanel, 1080, 780, 1, Path.Combine(output, "trends-empty-fixture.png"));
        host.Close();
        window.Close();
        Assert.False(model.IsActive);

        await VerifySessionAsync(output);
        File.Delete(settingsPath);
        Console.WriteLine("TRENDS_CHECKS: PASS — 24-hour fixtures, units, gaps, selection, localization, DPI, hidden rendering, recording and flush lifetime.");
    }

    private static async Task VerifySessionAsync(string output)
    {
        var store = new Store();
        var history = new SensorHistoryService(store);
        var hardware = new TestHardware();
        var backend = new FakeBackend(hardware);
        var monitor = new MonitoringService(() => backend);
        var settings = new SettingsViewModel(Path.Combine(output, "trends-session-settings.json")) { SampleSeconds = 1 };
        await using var session = new MonitoringSession(settings, monitor,
            new("TEST OS", "X64", "TEST", "0.9.6", "Standard user", "Not detected"), history);
        session.Start();
        await WaitFor(() => history.GetMaximum(new("test-cpu", "test-load", "Load", "%")) is not null);
        var firstTime = monitor.Latest.CapturedAt;
        var diagnostics = new MainWindow(session) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        diagnostics.Show();
        diagnostics.Close();
        await WaitFor(() => monitor.Latest.CapturedAt > firstTime);
        var series = history.GetSeries(new("test-cpu", "test-load", "Load", "%"))!;
        Assert.True(series.Minutes.Sum(m => m.Count) >= 2);
        Assert.Equal(1, backend.Opens);
        Assert.False(session.Diagnostics.Trends!.IsActive);
        await session.DisposeAsync();
        Assert.Equal(1, backend.Closes);
        Assert.NotNull(store.Saved);
        Assert.True(store.Saved.Series.Single().Minutes.Sum(m => m.Count) >= 2);
    }

    private sealed class TestHardware : IMonitorHardware
    {
        public string Id => "test-cpu";
        public string Name => "TEST CPU";
        public string Kind => "Cpu";
        public IReadOnlyList<IMonitorHardware> Children => [];
        public IReadOnlyList<RawSensor> Sensors => [new("test-load", "CPU Total", "Load", "%", 30)];
        public void Update() { }
    }
    private sealed class FakeBackend(params IMonitorHardware[] hardware) : IHardwareBackend
    {
        public int Opens, Closes;
        public IReadOnlyList<IMonitorHardware> Hardware => hardware;
        public void Open() => Opens++;
        public void Dispose() => Closes++;
    }
    private static async Task WaitFor(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(predicate());
    }
    private static MonitoringSnapshot Fixture(DateTimeOffset time, int second)
    {
        var wave = 45 + 24 * Math.Sin(second / 3000d) + 12 * Math.Cos(second / 710d);
        return new(time, time, "Monitoring",
            [new("cpu", "TEST CPU · Example processor", "Cpu", "cpu"), new("gpu", "TEST GPU · Example graphics", "GpuNvidia", "gpu"), new("/ram", "TEST RAM", "Memory", "/ram")],
            [new("cpu/load", "cpu", "CPU Total", "Load", "%", wave, time),
             new("cpu/temp", "cpu", "CPU Package", "Temperature", "°C", 43 + wave * .4, time),
             new("gpu/load", "gpu", "GPU Core", "Load", "%", Math.Clamp(wave * 1.2 + 5 * Math.Sin(second / 70d), 0, 100), time),
             new("gpu/temp", "gpu", "GPU Core", "Temperature", "°C", 38 + wave * .5, time),
             new("gpu/encode", "gpu", "GPU Video Encode", "Load", "%", 12, time),
             new("ram/load", "/ram", "Memory", "Load", "%", 35 + 10 * Math.Sin(second / 10000d), time)], []);
    }
    private static void TextElementFont(FrameworkElement root, Window window)
    {
        System.Windows.Documents.TextElement.SetForeground(root, window.Foreground);
        System.Windows.Documents.TextElement.SetFontFamily(root, window.FontFamily);
        System.Windows.Documents.TextElement.SetFontSize(root, window.FontSize);
        ((Grid)root).Background = window.Background;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Render(FrameworkElement root, double width, double height, double scale, string path)
    {
        if (Window.GetWindow(root) is { } host)
        {
            host.Width = width;
            host.Height = height;
            host.UpdateLayout();
        }
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
