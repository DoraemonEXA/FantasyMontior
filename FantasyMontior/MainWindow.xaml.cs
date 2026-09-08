using static FantasyMontior.Localization;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using FantasyMontior.ViewModels;

namespace FantasyMontior;

public partial class MainWindow : Window
{
    private readonly MonitoringSession _session;
    private readonly bool _ownsSession;
    private readonly MonitorViewModel _viewModel;
    private bool _closing;
    private bool _closed;

    public MainWindow() : this(new SettingsViewModel()) { }

    public MainWindow(SettingsViewModel settings) : this(new MonitoringSession(settings), true) { }

    public MainWindow(MonitoringSession session) : this(session, false) { }

    private MainWindow(MonitoringSession session, bool ownsSession)
    {
        _session = session;
        _ownsSession = ownsSession;
        _viewModel = session.Diagnostics;
        Localization.Apply(session.Settings.Language);
        InitializeComponent();
        DataContext = _viewModel;
    }

    public void OpenSettings() => MainTabs.SelectedIndex = 3;
    private void OnLoaded(object sender, RoutedEventArgs e) => _session.Start();

    private void OnRetry(object sender, RoutedEventArgs e)
    {
        _viewModel.Notice = "Rediscovery requested. Waiting for the current hardware call to finish…";
        _session.Retry();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_viewModel.CopyDiagnostics());
            _viewModel.Notice = "Diagnostics copied to clipboard.";
        }
        catch (Exception ex) { _viewModel.Notice = F("Could not copy diagnostics: {0}", ex.Message); }
    }

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { _viewModel.Notice = F("Could not open the browser: {0}", ex.Message); }
        e.Handled = true;
    }

    private void OnOpenSetup(object sender, RoutedEventArgs e)
    {
        SetupTab.IsSelected = true;
        SetupTab.Focus();
    }

    private void OnOpenApplicationFolder(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(AppContext.BaseDirectory) { UseShellExecute = true }); }
        catch (Exception ex) { _viewModel.Notice = F("Could not open the application folder: {0}", ex.Message); }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_ownsSession || _closed) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _viewModel.SetStopping();
        _viewModel.Notice = "Closing hardware access. Waiting for any active driver call to finish…";
        await _session.DisposeAsync();
        _closed = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}
