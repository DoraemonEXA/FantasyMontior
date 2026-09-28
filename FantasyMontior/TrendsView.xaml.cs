using System.Windows;
using System.Windows.Controls;
using FantasyMontior.ViewModels;

namespace FantasyMontior;

public partial class TrendsView : UserControl
{
    private Window? _window;
    public TrendsView() => InitializeComponent();
    private void UpdateActivity()
    {
        if (DataContext is TrendsViewModel vm) vm.IsActive = IsLoaded && IsVisible && _window?.WindowState != WindowState.Minimized;
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is not null) _window.StateChanged += OnWindowStateChanged;
        UpdateActivity();
    }
    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateActivity();
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateActivity();
    }
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is TrendsViewModel old) old.IsActive = false;
        UpdateActivity();
    }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is TrendsViewModel vm) vm.IsActive = false;
        if (_window is not null) _window.StateChanged -= OnWindowStateChanged;
        _window = null;
    }
    private void OnCardsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = Math.Max(1, e.NewSize.Width);
        var columns = width >= 900 ? 3 : width >= 620 ? 2 : 1;
        DeviceCards.Tag = Math.Max(1, width / columns - 10);
    }
    private void OnSelectPlot(object sender, RoutedEventArgs e)
    {
        if (DataContext is TrendsViewModel vm && sender is Button { Tag: TrendPlotViewModel plot } && plot.Id is not null)
        {
            vm.Select(plot);
            DetailPanel.BringIntoView();
        }
    }
}
