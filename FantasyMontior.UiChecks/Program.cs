using System.Collections.Immutable;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FantasyMontior.Core;
using FantasyMontior.ViewModels;
using Xunit;

namespace FantasyMontior.UiChecks;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static int Run(string[] args)
    {
        // Own a real WPF message loop on the process main thread. Never construct
        // Application on an abandoned testhost thread: pending shutdown callbacks can outlive the test.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/FantasyMontior;component/Resources/Theme.xaml")
        });
        app.DispatcherUnhandledException += (_, e) =>
        {
            Console.Error.WriteLine(e.Exception);
            e.Handled = true;
            app.Shutdown(1);
        };
        var passed = false;
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            try
            {
                if (args.Length == 0)
                {
                    VerifyPresentation();
                    await WidgetChecks.VerifyAsync(Path.Combine(FindRepository(), "artifacts", "ui"));
                }
                else if (args.Length == 3 && args[0] == "--widget-probe" && int.TryParse(args[1], out var seconds) && seconds is >= 1 and <= 3600)
                    await WidgetHardwareProbe.RunAsync(seconds, Path.GetFullPath(args[2]));
                else throw new ArgumentException("Usage: FantasyMontior.UiChecks [--widget-probe <seconds: 1..3600> <output-directory>]");
                passed = true;
                app.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                app.Shutdown(1);
            }
        }));
        var exitCode = app.Run();
        if (exitCode == 0 && passed && app.Dispatcher.HasShutdownFinished)
        {
            Console.WriteLine(args.Length == 0
                ? "UI_CHECKS: PASS — bindings, identity, filtering, selection, scrolling, localization, settings persistence, rendering; WPF dispatcher shut down."
                : "WIDGET_PROBE: PASS — capture complete; WPF dispatcher shut down. This does not establish sensor accuracy.");
            return 0;
        }
        Console.Error.WriteLine("UI_CHECKS: FAIL — validation or dispatcher shutdown did not complete.");
        return 1;
    }

    private static void VerifyPresentation()
    {
        var now = DateTimeOffset.UtcNow;
        Localization.Apply("en");
        var settingsPath = Path.Combine(FindRepository(), "artifacts", "ui", "settings-test.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, "{\"Language\":\"en\",\"SampleSeconds\":1}");
        var settings = new SettingsViewModel(settingsPath);
        var runtime = new RuntimeInfo("TEST OS", "X64", ".NET test", "0.9.6", "Standard user", "Not detected");
        var vm = new MonitorViewModel(runtime, settings);
        var snapshot = new MonitoringSnapshot(now, now, "Monitoring",
            [new("cpu", "TEST CPU", "Cpu", "cpu"), new("gpu1", "TEST GPU", "GpuNvidia", "gpu1"), new("gpu2", "TEST GPU", "GpuNvidia", "gpu2")],
            [new("cpu/load", "cpu", "CPU Total", "Load", "%", 0, now),
             new("cpu/temp", "cpu", "CPU Package", "Temperature", "°C", null, null),
             new("gpu1/load", "gpu1", "GPU Core", "Load", "%", 40, now),
             new("gpu2/load", "gpu2", "GPU Core", "Load", "%", 60, now)], []);
        vm.Apply(snapshot, now);
        Assert.Equal(3, vm.Groups.Count);
        Assert.Contains("MISSING", vm.Groups[0].Coverage);
        var original = vm.Sensors.Single(s => s.Id == "gpu1/load");
        vm.SensorView.SortDescriptions.Add(new(nameof(SensorRow.NumericValue), ListSortDirection.Descending));
        vm.Filter = "gpu1";
        Assert.Same(original, Assert.Single(vm.SensorView.Cast<SensorRow>()));
        vm.Apply(snapshot with { Sensors = snapshot.Sensors.Reverse().Select(s => s with { Value = s.Value + 1 }).ToImmutableArray() }, now);
        Assert.Same(original, vm.Sensors.Single(s => s.Id == "gpu1/load"));
        Assert.Same(original, Assert.Single(vm.SensorView.Cast<SensorRow>()));
        Assert.Single(vm.SensorView.SortDescriptions);
        vm.Apply(snapshot, now.AddSeconds(3));
        Assert.Equal("Stale", vm.Sensors.Single(s => s.Id == "cpu/load").State);
        Assert.Equal("Unavailable", vm.Sensors.Single(s => s.Id == "cpu/temp").State);
        vm.Filter = "";

        var window = new MainWindow(new SettingsViewModel(settingsPath));
        var root = (FrameworkElement)window.Content;
        var background = window.Background;
        var foreground = window.Foreground;
        var font = window.FontFamily;
        var fontSize = window.FontSize;
        window.Content = null;
        // Keep resources attached to an Application window so runtime resource
        // invalidation follows the same path as the real UI, without starting hardware.
        var host = new Window { Content = root, ShowActivated = false, ShowInTaskbar = false, Left = -10000 };
        ((Grid)root).Background = background;
        TextElement.SetForeground(root, foreground);
        TextElement.SetFontFamily(root, font);
        TextElement.SetFontSize(root, fontSize);
        root.DataContext = vm;
        var repo = FindRepository();
        var output = Path.Combine(repo, "artifacts", "ui");
        Directory.CreateDirectory(output);
        var loading = new MonitorViewModel(runtime, settings);
        root.DataContext = loading;
        Assert.True(loading.IsLoading);
        Assert.False(loading.CanRetry);
        Render(root, 1080, 740, 1, Path.Combine(output, "loading-en.png"));
        Assert.True(Descendants<ProgressBar>(root).Single().IsIndeterminate);
        var loadingTabs = Descendants<TabControl>(root).Single();
        loadingTabs.SelectedIndex = 1;
        settings.Language = "zh-CN";
        Render(root, 760, 600, 1.5, Path.Combine(output, "loading-zh-minimum-150.png"));
        Assert.True(loading.IsLoading);
        Assert.True(Descendants<ProgressBar>(root).Single().IsIndeterminate);
        loading.Apply(MonitoringSnapshot.Empty with { Status = "Collection failed", Errors = ["Test discovery failure"] }, now);
        Assert.False(loading.IsLoading);
        Assert.True(loading.CanRetry);
        root.UpdateLayout();
        Assert.Equal(Visibility.Visible, ((FrameworkElement)Descendants<DataGrid>(root).Single().Parent).Visibility);
        loading.Apply(MonitoringSnapshot.Empty, now);
        Assert.True(loading.IsLoading);
        loading.Apply(snapshot, now);
        Assert.False(loading.IsLoading);
        loading.Apply(MonitoringSnapshot.Empty, now);
        loading.SetStopping();
        Assert.False(loading.IsLoading);
        Assert.False(loading.CanRetry);
        settings.Language = "en";
        loadingTabs.SelectedIndex = 0;
        root.DataContext = vm;
        vm.Notice = "TEST DATA — unavailable and stale state fixture. Not real measurements.";
        Render(root, 1080, 740, 1, Path.Combine(output, "states-fixture.png"));

        // Render the actual captured readings if a local hardware probe has been run.
        var capturePath = Path.Combine(repo, "artifacts", "verification", "snapshot.json");
        if (File.Exists(capturePath))
        {
            using var capture = JsonDocument.Parse(File.ReadAllText(capturePath));
            var real = capture.RootElement.GetProperty("Snapshot").Deserialize<MonitoringSnapshot>()!;
            var realRuntime = capture.RootElement.GetProperty("Environment").Deserialize<RuntimeInfo>()!;
            vm = new MonitorViewModel(realRuntime, settings);
            vm.Apply(real, real.CapturedAt);
            vm.Notice = "";
            root.DataContext = vm;
        }
        Render(root, 1080, 740, 1, Path.Combine(output, "cpu-gpu.png"));
        var tabs = Descendants<TabControl>(root).Single();
        var deviceExpanders = Descendants<Expander>(tabs).ToArray();
        foreach (var expander in deviceExpanders) expander.IsExpanded = false;
        root.UpdateLayout();
        var lastDevice = deviceExpanders.LastOrDefault();
        if (lastDevice is not null) lastDevice.IsExpanded = true;
        Render(root, 760, 600, 1.5, Path.Combine(output, "gpu-minimum-150.png"));

        tabs.SelectedIndex = 1;
        Render(root, 1080, 740, 1, Path.Combine(output, "all-sensors.png"));
        var grid = Descendants<DataGrid>(root).Single();
        Assert.Same(vm.SensorView, grid.ItemsSource);
        var selected = vm.Sensors.Last();
        grid.SelectedItem = selected;
        grid.ScrollIntoView(selected);
        root.UpdateLayout();
        var scroll = Descendants<ScrollViewer>(grid).First();
        var offset = scroll.VerticalOffset;
        // Same snapshot refresh must not replace the table's selected row or reset scroll.
        using var export = JsonDocument.Parse(vm.CopyDiagnostics());
        var current = export.RootElement.GetProperty("Snapshot").Deserialize<MonitoringSnapshot>()!;
        vm.Apply(current, current.CapturedAt);
        root.UpdateLayout();
        Assert.Same(selected, grid.SelectedItem);
        Assert.Equal(offset, scroll.VerticalOffset);
        vm.Filter = "Temperature";
        root.UpdateLayout();
        Assert.All(vm.SensorView.Cast<SensorRow>(), row => Assert.Equal("Temperature", row.Kind));
        Render(root, 760, 600, 1.5, Path.Combine(output, "filtered-minimum-150.png"));
        tabs.SelectedIndex = 2;
        Render(root, 1080, 740, 1, Path.Combine(output, "pawnio-setup.png"));
        Render(root, 760, 600, 1.5, Path.Combine(output, "pawnio-setup-minimum-150.png"));
        var setupScroll = Descendants<ScrollViewer>(tabs).Single();
        Assert.True(setupScroll.ScrollableHeight > 0);
        setupScroll.ScrollToEnd();
        root.UpdateLayout();
        Assert.True(setupScroll.VerticalOffset > 0);
        Render(root, 760, 600, 1.5, Path.Combine(output, "pawnio-setup-bottom-150.png"));
        tabs.SelectedIndex = 3;
        Render(root, 1080, 740, 1, Path.Combine(output, "settings-en.png"));
        var combos = Descendants<ComboBox>(tabs).ToArray();
        Assert.Equal(2, combos.Length);
        combos[0].SelectedValue = "zh-CN";
        combos[1].SelectedItem = 5;
        root.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        root.UpdateLayout();
        Assert.Equal("zh-CN", settings.Language);
        Assert.Equal(5, settings.SampleSeconds);
        Assert.Equal("设置", ((TabItem)tabs.Items[3]).Header);
        var reloaded = new SettingsViewModel(settingsPath);
        Assert.Equal("zh-CN", reloaded.Language);
        Assert.Equal(5, reloaded.SampleSeconds);
        vm.Apply(snapshot, now.AddSeconds(6));
        Assert.Equal("实时", vm.Sensors.Single(s => s.Id == "cpu/load").State);
        Assert.Equal("不可用", vm.Sensors.Single(s => s.Id == "cpu/temp").State);
        vm.Apply(snapshot, now.AddSeconds(15));
        Assert.Equal("已过期", vm.Sensors.Single(s => s.Id == "cpu/load").State);
        Render(root, 760, 600, 1.5, Path.Combine(output, "settings-zh-minimum-150.png"));
        tabs.SelectedIndex = 0;
        Render(root, 760, 600, 1.5, Path.Combine(output, "sensors-zh-minimum-150.png"));
        tabs.SelectedIndex = 2;
        Render(root, 760, 600, 1.5, Path.Combine(output, "setup-zh-minimum-150.png"));
        settings.Language = "en";
        root.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Equal("Settings", ((TabItem)tabs.Items[3]).Header);
        File.WriteAllText(settingsPath, "invalid json");
        Assert.Equal(1, new SettingsViewModel(settingsPath).SampleSeconds);
        File.Delete(settingsPath);
        host.Close();
        window.Close();
    }

    private static string FindRepository()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null && !File.Exists(Path.Combine(path.FullName, "FantasyMontior.sln"))) path = path.Parent;
        return path?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
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
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        var image = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var drawing = backdrop.RenderOpen()) drawing.DrawRectangle(((Grid)root).Background, null, new Rect(0, 0, width, height));
        image.Render(backdrop);
        image.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
