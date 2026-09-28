using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FantasyMontior.Core;
using FantasyMontior.ViewModels;
using Xunit;

namespace FantasyMontior.UiChecks;

internal static class WidgetChecks
{
    internal static async Task VerifyAsync(string output)
    {
        var path = Path.Combine(output, "widget-settings-test.json");
        File.WriteAllText(path, "{\"Language\":\"en\",\"SampleSeconds\":5,\"AlwaysOnTop\":true}");
        var settings = new SettingsViewModel(path);
        Assert.Equal(16, settings.FontSize);
        Assert.Equal(0, settings.Transparency);
        settings.Transparency = double.NaN;
        settings.Transparency = .9;
        settings.Transparency = -.1;
        Assert.Equal(0, settings.Transparency);
        Assert.True(settings.IsLocked);
        settings.FontSize = double.NaN;
        Assert.Equal(16, settings.FontSize);
        var now = DateTimeOffset.UtcNow;
        var snapshot = new MonitoringSnapshot(now, now, "Monitoring",
            [new("cpu", "TEST CPU", "Cpu", "cpu"), new("gpu", "TEST GPU", "GpuNvidia", "gpu"),
             new("/ram", "TEST RAM", "Memory", "/ram"), new("/vram", "TEST VIRTUAL MEMORY", "Memory", "/vram")],
            [new("cpu/load", "cpu", "CPU Total", "Load", "%", 27, now),
             new("cpu/temp", "cpu", "CPU Package", "Temperature", "°C", 54, now),
             new("gpu/load", "gpu", "GPU Core", "Load", "%", 68, now),
             new("gpu/temp", "gpu", "GPU Core", "Temperature", "°C", 62, now),
             new("ram/load", "/ram", "Memory", "Load", "%", 43, now),
             new("vram/load", "/vram", "Memory", "Load", "%", 99, now)], []);
        var history = new SensorHistoryService();
        var peakTime = now.AddMinutes(-1);
        history.Record(snapshot with { CapturedAt = peakTime, Sensors = [.. snapshot.Sensors.Select(s => s with { LastSuccess = peakTime, Value = s.Value + 20 })] }, TimeSpan.FromSeconds(1));
        history.Record(snapshot, TimeSpan.FromSeconds(1));
        var vm = new WidgetViewModel(settings, history);
        vm.Apply(snapshot, now);
        Assert.Equal(3, vm.Rows.Count);
        await VerifyAnimationAsync(vm, snapshot, now, output);
        Assert.Equal(43, vm.Rows.Last().Fill);
        var cpu = vm.Rows.First();
        Assert.Equal("47%", cpu.UsageMaximum);
        Assert.Equal("74°C", cpu.TemperatureMaximum);
        var offlineWidget = new WidgetViewModel(settings, history);
        offlineWidget.Apply(MonitoringSnapshot.Empty, now);
        Assert.Equal("47%", offlineWidget.Rows.First().UsageMaximum);
        Assert.Equal("—", offlineWidget.Rows.First().Usage);
        cpu.UsageKey = "missing";
        vm.Apply(snapshot, now);
        Assert.Equal("—", cpu.Usage);
        Assert.Equal("—", cpu.UsageMaximum);
        Assert.Contains(cpu.UsageChoices, c => c.Key == "missing");
        cpu.UsageKey = "";
        vm.Apply(snapshot, now.AddSeconds(15));
        Assert.Equal("Stale", cpu.State);
        Assert.Equal("47%", cpu.UsageMaximum);
        vm.Apply(snapshot with { Sensors = [.. snapshot.Sensors.Select(s => s with { Value = null })] }, now);
        Assert.All(vm.Rows, r => Assert.Equal("—", r.Usage));
        Assert.Equal("47%", cpu.UsageMaximum);
        vm.Apply(snapshot with { Sensors = [.. snapshot.Sensors.Select(s => s with { Value = 0 })] }, now);
        Assert.Equal("0%", cpu.Usage);
        Assert.Equal("0°C", cpu.Temperature);
        vm.Apply(snapshot with { Hardware = [], Sensors = [] }, now);
        Assert.Same(cpu, vm.Rows.First());
        Assert.All(vm.Rows, r => Assert.Equal("Unavailable", r.State));

        // Every image is explicitly marked as a fixture; these are layout evidence, not hardware evidence.
        foreach (var language in new[] { "en", "zh-CN" })
        foreach (var font in new[] { 12d, 16d, 28d })
        foreach (var scale in new[] { 1d, 1.5, 2d })
        {
            settings.Language = language;
            Localization.Apply(language);
            settings.FontSize = font;
            vm.Apply(snapshot, now);
            Render(vm, output, $"widget-fixture-{language}-{font}-{scale * 100:0}.png", scale);
        }
        settings.FontSize = 16;
        settings.Transparency = .5;
        Render(vm, output, "widget-fixture-transparency-50.png", 1);
        settings.Transparency = 0;
        Localization.Apply("en");
        vm.Apply(snapshot, now.AddSeconds(15));
        Render(vm, output, "widget-fixture-stale.png", 1);
        vm.Apply(snapshot with { Sensors = [] }, now);
        Render(vm, output, "widget-fixture-unavailable.png", 1);
        vm.Apply(MonitoringSnapshot.Empty, now);
        Render(vm, output, "widget-fixture-loading.png", 1);
        settings.SetPosition(new("TEST-MONITOR", -200, 80));
        cpu.UsageKey = "cpu/load";
        var restored = new SettingsViewModel(path);
        Assert.Equal(16, restored.FontSize);
        Assert.Equal("cpu/load", restored.Sources["cpu"].UsageKey);
        Assert.Equal(-200, restored.Position!.X);

        var backend = new TestBackend();
        var service = new MonitoringService(() => backend);
        await using var session = new MonitoringSession(settings, service,
            new("TEST OS", "X64", "TEST", "0.9.6", "Standard user", "Not detected"), new SensorHistoryService());
        using var desktop = new DesktopFixture();
        var widget = new WidgetWindow(session.Widget, _ => { }, () => { }, () => desktop.Target);
        widget.DesktopStatusChanged += session.Diagnostics.SetDesktopStatus;
        widget.Reveal();
        Assert.True(widget.IsDesktopAttached);
        Assert.DoesNotContain("AlwaysOnTop", File.ReadAllText(path));
        session.Start();
        await WaitFor(() => service.Latest.Sensors.Length > 0);
        await WaitFor(() => session.Widget.Rows.Any(r => r.Id == "test-cpu"));
        var first = new MainWindow(session) { ShowActivated = false };
        first.Show();
        first.Close();
        var second = new MainWindow(session) { ShowActivated = false };
        second.Show();
        second.OpenSettings();
        second.UpdateLayout();
        var sourceSelector = Descendants<ComboBox>(second).First(c => c.DataContext is WidgetRow row && row.Id == "test-cpu");
        sourceSelector.SelectedValue = "test-load";
        Assert.Equal("test-load", settings.Sources["test-cpu"].UsageKey);
        session.Widget.Apply(MonitoringSnapshot.Empty, DateTimeOffset.UtcNow);
        second.UpdateLayout();
        Assert.Equal("test-load", settings.Sources["test-cpu"].UsageKey);
        Assert.Equal("test-load", sourceSelector.SelectedValue);
        settings.Language = "zh-CN";
        second.UpdateLayout();
        var settingsImage = new RenderTargetBitmap((int)Math.Ceiling(second.ActualWidth), (int)Math.Ceiling(second.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        settingsImage.Render(second);
        var settingsEncoder = new PngBitmapEncoder();
        settingsEncoder.Frames.Add(BitmapFrame.Create(settingsImage));
        using (var settingsFile = File.Create(Path.Combine(output, "widget-settings-zh.png"))) settingsEncoder.Save(settingsFile);
        var menu = widget.BuildMenu();
        Assert.Equal("设置", ((MenuItem)menu.Items[1]).Header);
        var unlock = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "解锁组件"));
        unlock.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.False(settings.IsLocked);
        Assert.False(new SettingsViewModel(path).IsLocked);
        var lockItem = widget.BuildMenu().Items.OfType<MenuItem>().Single(i => Equals(i.Header, "锁定组件"));
        lockItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(settings.IsLocked);
        Assert.True(new SettingsViewModel(path).IsLocked);
        var transparency = Descendants<Slider>(second).Single(s => s.Maximum == .8);
        transparency.Value = .5;
        widget.UpdateLayout();
        Assert.Equal(.5, settings.Transparency);
        Assert.Equal(.5, new SettingsViewModel(path).Transparency);
        Assert.Equal(.5, Descendants<WidgetView>(widget).Single().Opacity);
        transparency.Value = 0;
        widget.UpdateLayout();
        Assert.Equal(1, Descendants<WidgetView>(widget).Single().Opacity);
        var slider = Descendants<Slider>(second).Single(s => s.Maximum == 28);
        slider.Value = 28;
        Assert.Equal(28, settings.FontSize);
        widget.Hide();
        Assert.False(widget.IsVisible);
        widget.Reveal();
        Assert.True(widget.IsVisible);
        Assert.Equal(WindowState.Normal, widget.WindowState);
        Assert.False(widget.Topmost);
        settings.FontSize = 28;
        await Task.Delay(100);
        Assert.True(widget.ActualWidth >= 620);
        widget.ResetPosition();
        Assert.NotEqual("TEST-MONITOR", settings.Position!.Monitor);
        Assert.Equal(1, backend.OpenCount);
        Assert.Equal(0, backend.CloseCount);
        second.Close();
        await DesktopChecks.VerifyAsync(session, output);
        Assert.Equal(1, backend.OpenCount);
        Assert.Equal(1, backend.CloseCount);
        await session.DisposeAsync();
        widget.CloseForExit();
        widget.DesktopStatusChanged -= session.Diagnostics.SetDesktopStatus;
        Assert.Equal(1, backend.CloseCount);
        File.WriteAllText(path, "{\"Language\":\"en\",\"SampleSeconds\":2,\"FontSize\":100}");
        Assert.Equal(16, new SettingsViewModel(path).FontSize);
        File.Delete(path);
        Console.WriteLine("WIDGET_CHECKS: PASS — source selection, migration, states, 18 three-background renders, shared-session windows and cleanup.");
    }

    internal static void Render(WidgetViewModel vm, string output, string name, double scale, bool fixture = true)
    {
        var width = 360 * vm.Settings.FontSize / 16;
        var probe = new WidgetView { DataContext = vm };
        var host = new Window { Content = probe, SizeToContent = SizeToContent.WidthAndHeight,
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000 };
        host.Show();
        host.UpdateLayout();
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var height = probe.DesiredSize.Height;
        Assert.True(height > 0);
        var tileWidth = width + 32;
        var canvas = new Canvas { Width = tileWidth * 3, Height = height + 78, Background = Brushes.Black };
        for (var i = 0; i < 3; i++)
        {
            var tile = new Grid { Width = tileWidth, Height = height + 78,
                Background = i == 0 ? new SolidColorBrush(Color.FromRgb(21, 27, 34)) : i == 1 ? Brushes.WhiteSmoke : BusyBackground() };
            var label = new TextBlock { Text = (fixture ? "LAYOUT FIXTURE · " : "HARDWARE CAPTURE · ") + (i == 0 ? "DARK" : i == 1 ? "LIGHT" : "BUSY"),
                FontSize = 11, Foreground = i == 1 ? Brushes.Black : Brushes.White, Margin = new Thickness(16, 12, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            tile.Children.Add(label);
            var view = new WidgetView { DataContext = vm, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16, 43, 0, 0) };
            tile.Children.Add(view);
            Canvas.SetLeft(tile, i * tileWidth);
            canvas.Children.Add(tile);
        }
        canvas.Measure(new Size(canvas.Width, canvas.Height));
        host.Content = canvas;
        host.UpdateLayout();
        canvas.Arrange(new Rect(0, 0, canvas.Width, canvas.Height));
        canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(canvas.Width * scale), (int)Math.Ceiling(canvas.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name));
        encoder.Save(file);
        host.Close();
    }
    private static async Task VerifyAnimationAsync(WidgetViewModel vm, MonitoringSnapshot snapshot, DateTimeOffset now, string output)
    {
        var view = new WidgetView { DataContext = vm };
        var panel = new StackPanel { Background = Brushes.DarkSlateGray };
        panel.Children.Add(new TextBlock { Text = "ANIMATION FIXTURE · synthetic readings", Foreground = Brushes.White });
        panel.Children.Add(view);
        var host = new Window { Content = panel, SizeToContent = SizeToContent.WidthAndHeight,
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000 };
        try
        {
            host.Show();
            host.UpdateLayout();
            await Task.Delay(100);
            var effects = Descendants<LiquidMeterEffect>(view).ToArray();
            Assert.Equal(3, effects.Length);
            Assert.All(effects, e => Assert.True(e.IsAnimating));
            byte[] Capture(string name)
            {
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth),
                    (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(panel);
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, name));
                encoder.Save(file);
                return pixels;
            }
            var before = Capture("widget-animation-fixture-a.png");
            await Task.Delay(800);
            var after = Capture("widget-animation-fixture-b.png");
            Assert.False(before.SequenceEqual(after));
            Assert.All(Descendants<FrameworkElement>(view), e => Assert.Null(e.ToolTip));
            var cpuBar = Descendants<AnimatedUsageBar>(view).First();
            void SetCpuUsage(double value) => vm.Apply(snapshot with
            {
                Sensors = [.. snapshot.Sensors.Select(s => s.Id == "cpu/load" ? s with { Value = value } : s)]
            }, now);
            SetCpuUsage(90);
            host.UpdateLayout();
            Assert.Equal(90, vm.Rows.First().Fill);
            Assert.Equal("90%", vm.Rows.First().Usage);
            Assert.InRange(cpuBar.Value, 27, 30);
            await Task.Delay(150);
            Assert.InRange(cpuBar.Value, 28, 89);
            Capture("widget-fill-fixture-rising.png");
            var interrupted = cpuBar.Value;
            SetCpuUsage(5);
            host.UpdateLayout();
            Assert.InRange(Math.Abs(cpuBar.Value - interrupted), 0, 1);
            await Task.Delay(150);
            Assert.InRange(cpuBar.Value, 5.01, interrupted - .01);
            Capture("widget-fill-fixture-falling.png");
            await WaitFor(() => !cpuBar.HasAnimatedProperties);
            Assert.Equal(5, cpuBar.Value);
            Assert.False(cpuBar.HasAnimatedProperties);
            SetCpuUsage(80);
            host.Hide();
            Assert.Equal(80, cpuBar.Value);
            Assert.False(cpuBar.HasAnimatedProperties);
            SetCpuUsage(40);
            Assert.Equal(40, cpuBar.Value);
            host.Show();
            SetCpuUsage(90);
            vm.Apply(snapshot, now.AddSeconds(15));
            host.UpdateLayout();
            Assert.Equal(27, cpuBar.Value);
            Assert.False(cpuBar.HasAnimatedProperties);
            vm.Apply(snapshot, now);
            await Task.Delay(600);
            host.UpdateLayout();
            // Each effect is bounded by the actual ProgressBar indicator, including 0/100% changes.
            foreach (var bar in Descendants<ProgressBar>(view))
            {
                var effect = Descendants<LiquidMeterEffect>(bar).Single();
                Assert.InRange(Math.Abs(effect.ActualWidth - bar.ActualWidth * bar.Value / 100), 0, 1);
            }
            host.Hide();
            Assert.All(effects, e => Assert.False(e.IsAnimating));
            host.Show();
            await Task.Delay(100);
            Assert.All(effects, e => Assert.True(e.IsAnimating));
            vm.Apply(snapshot, now.AddSeconds(15));
            host.UpdateLayout();
            Assert.All(effects, e => Assert.False(e.IsAnimating));
            vm.Apply(snapshot with { Sensors = [] }, now);
            host.UpdateLayout();
            Assert.All(effects, e => Assert.False(e.IsAnimating));
            vm.Apply(snapshot with { Sensors = [.. snapshot.Sensors.Select(s => s with { Value = 0 })] }, now);
            host.UpdateLayout();
            Assert.All(effects, e => { Assert.Equal(0, e.ActualWidth); Assert.False(e.IsAnimating); });
            vm.Apply(snapshot with { Sensors = [.. snapshot.Sensors.Select(s => s with { Value = 100 })] }, now);
            await WaitFor(() => Descendants<AnimatedUsageBar>(view).All(b => b.Value == 100 && !b.HasAnimatedProperties));
            host.UpdateLayout();
            Assert.All(effects, e => Assert.True(e.IsAnimating));
            Assert.All(Descendants<AnimatedUsageBar>(view), b => Assert.Equal(100, b.Value));
            var bars = Descendants<AnimatedUsageBar>(view).ToArray();
            SetCpuUsage(10);
            host.Content = null;
            await Task.Delay(100);
            Assert.All(effects, e => Assert.False(e.IsAnimating));
            Assert.All(bars, b => { Assert.Equal(b.TargetValue, b.Value); Assert.False(b.HasAnimatedProperties); });
        }
        finally { host.Close(); vm.Apply(snapshot, now); }
        Console.WriteLine("METER_ANIMATION_CHECKS: PASS — liquid pixels, eased fill and retargeting, fill bounds, zero/full, stale/missing, hide/show, unload, no widget tooltips.");
    }
    private static Brush BusyBackground()
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(111, 122, 83)), null, new RectangleGeometry(new Rect(0, 0, 70, 70))));
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(195, 190, 155)), null, Geometry.Parse("M0,0 L30,0 L70,40 L70,65 Z")));
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(45, 61, 54)), null, Geometry.Parse("M0,35 L35,70 L0,70 Z")));
        return new DrawingBrush(drawing) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 70, 70), ViewportUnits = BrushMappingMode.Absolute };
    }
    private static async Task WaitFor(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(); await Task.Delay(25); }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private sealed class TestBackend : IHardwareBackend
    {
        public int OpenCount, CloseCount;
        public IReadOnlyList<IMonitorHardware> Hardware { get; } = [new TestHardware()];
        public void Open() => Interlocked.Increment(ref OpenCount);
        public void Dispose() => Interlocked.Increment(ref CloseCount);
    }
    private sealed class TestHardware : IMonitorHardware
    {
        public string Id => "test-cpu";
        public string Name => "TEST CPU";
        public string Kind => "Cpu";
        public IReadOnlyList<IMonitorHardware> Children => [];
        public IReadOnlyList<RawSensor> Sensors => [new("test-load", "CPU Total", "Load", "%", 27), new("test-temp", "CPU Package", "Temperature", "°C", 54)];
        public void Update() { }
    }
}
