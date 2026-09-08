using System.Windows.Threading;
using FantasyMontior.Core;
using FantasyMontior.ViewModels;

namespace FantasyMontior;

public sealed class MonitoringSession : IAsyncDisposable
{
    private readonly MonitoringService _monitor;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _started;
    private Task? _stop;
    public MonitorViewModel Diagnostics { get; }
    public WidgetViewModel Widget { get; }
    public SettingsViewModel Settings => Diagnostics.Settings;

    public MonitoringSession(SettingsViewModel settings, MonitoringService? monitor = null, RuntimeInfo? runtime = null)
    {
        _monitor = monitor ?? new MonitoringService();
        Diagnostics = new(runtime, settings);
        Widget = new(settings);
        Diagnostics.Widget = Widget;
        _monitor.SetInterval(TimeSpan.FromSeconds(settings.SampleSeconds));
        settings.SettingsChanged += OnSettingsChanged;
        _timer.Tick += OnTick;
        Refresh();
    }

    private void OnSettingsChanged() { _monitor.SetInterval(TimeSpan.FromSeconds(Settings.SampleSeconds)); Refresh(); }
    private void OnTick(object? sender, EventArgs e) => Refresh();
    private void Refresh()
    {
        var now = DateTimeOffset.UtcNow;
        Diagnostics.Apply(_monitor.Latest, now);
        Widget.Apply(_monitor.Latest, now);
    }
    public void Start()
    {
        if (_started || _stop is not null) return;
        _started = true;
        _monitor.Start();
        _timer.Start();
    }
    public void Retry() => _monitor.RetryDiscovery();
    public ValueTask DisposeAsync() => new(_stop ??= StopAsync());
    private async Task StopAsync()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        Settings.SettingsChanged -= OnSettingsChanged;
        Diagnostics.SetStopping();
        Widget.Apply(_monitor.Latest with { Status = "Stopping" }, DateTimeOffset.UtcNow);
        await _monitor.DisposeAsync();
    }
}
