using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FantasyMontior.ViewModels;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace FantasyMontior;

public sealed class WidgetWindow : Window
{
    private readonly SettingsViewModel _settings;
    private readonly Action<bool> _openDiagnostics;
    private readonly Action _exit;
    private readonly DesktopWidgetHost _desktop;
    private readonly WidgetView _view;
    private HwndSource? _inputSource;
    private readonly DispatcherTimer _dragTimer;
    private readonly DispatcherTimer _positionTimer;
    internal bool IsPositionRestorePending => _positionTimer.IsEnabled;
    private readonly Func<DesktopPointer?> _readPointer;
    private bool _allowClose, _placing;
    private DesktopNative.Point? _dragStart;
    private DesktopNative.Rect _dragRect;
    internal bool IsClosed { get; private set; }
    internal bool IsDesktopAttached => _desktop.IsAttached;
    internal event Action<string, string>? DesktopStatusChanged;
    public WidgetWindow(WidgetViewModel viewModel, Action<bool> openDiagnostics, Action exit)
        : this(viewModel, openDiagnostics, exit, null) { }

    internal WidgetWindow(WidgetViewModel viewModel, Action<bool> openDiagnostics, Action exit,
        Func<DesktopTarget?>? findDesktop, Func<DesktopPointer?>? readPointer = null)
    {
        _readPointer = readPointer ?? DesktopNative.ReadPointer;
        _dragTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _dragTimer.Tick += OnDragTick;
        _positionTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _positionTimer.Tick += OnPositionTick;
        _desktop = new DesktopWidgetHost(OnDesktopLost, findDesktop, QueueRestorePosition);
        _settings = viewModel.Settings;
        _openDiagnostics = openDiagnostics;
        _exit = exit;
        Title = "FantasyMontior";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = false;
        _view = new WidgetView { DataContext = viewModel };
        Content = new ScrollViewer { Content = _view,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SourceInitialized += (_, _) =>
        {
            _inputSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _inputSource?.AddHook(DragWindowProc);
            if (_inputSource is not null) _inputSource.Disposed += OnWindowDestroyed;
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) { _positionTimer.Stop(); EndDrag(); }
        };
        Loaded += (_, _) => RestorePosition();
        SizeChanged += (_, _) => QueueRestorePosition();
        DpiChanged += (_, _) => QueueRestorePosition();
        LocationChanged += (_, _) => QueueRestorePosition();
        MouseRightButtonUp += (_, e) => { OpenMenu(); e.Handled = true; };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Apps || e.Key == Key.F10 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            { OpenMenu(); e.Handled = true; }
        };
        Closing += OnClosing;
        Closed += OnWindowDestroyed;
        _settings.SettingsChanged += OnSettings;
        SystemEvents.DisplaySettingsChanged += OnDisplays;
    }

    private void OnWindowDestroyed(object? sender, EventArgs e)
    {
        // HwndSource disposal can make WPF's Window unusable without raising Closed.
        // Retire it on either notification; never reparent/restyle a dying HWND.
        if (IsClosed) return;
        IsClosed = true;
        if (_inputSource is not null) _inputSource.Disposed -= OnWindowDestroyed;
        _inputSource = null;
        _dragStart = null;
        _desktop.WindowDestroyed();
        Cleanup();
        if (!_allowClose) DesktopStatusChanged?.Invoke("Desktop connection lost. Use Retry desktop attachment from the tray.", "");
    }

    private void OnSettings() { if (_settings.IsLocked) EndDrag(); QueueRestorePosition(); }
    private void OnDisplays(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(OnDisplayChange));
    private void OnDisplayChange()
    {
        if (IsClosed) return;
        // A mode switch invalidates the drag's screen coordinates. Do not save a
        // transient/clamped position over the user's preferred monitor-relative DIPs.
        EndDrag(save: false);
        QueueRestorePosition();
    }
    private void QueueRestorePosition()
    {
        if (_placing || _dragStart is not null || !IsLoaded || !IsVisible || IsClosed || !_desktop.IsAttached) return;
        // Display, child DPI and Explorer moves need not arrive together. Debounce
        // their final layout, then restore intent rather than constrain a displaced HWND.
        _positionTimer.Stop();
        _positionTimer.Start();
    }
    private void OnPositionTick(object? sender, EventArgs e)
    {
        _positionTimer.Stop();
        if (IsVisible && _dragStart is null) RestorePosition();
    }
    private nint DragWindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // Explorer owns the foreground window. Handle the HWND's actual button
        // messages before WPF's active-source/button-state filtering; never activate
        // Explorer or the widget to make dragging work.
        if (message == 0x0002) { _dragStart = null; _dragTimer.Stop(); _positionTimer.Stop(); return 0; } // WM_DESTROY
        if (message == 0x007E) { OnDisplayChange(); return 0; } // WM_DISPLAYCHANGE
        if (message == 0x02E3) { QueueRestorePosition(); return 0; } // WM_DPICHANGED_AFTERPARENT
        if (message == 0x001F) { EndDrag(); return 0; } // WM_CANCELMODE
        if (message is 0x0201 or 0x0203) // LBUTTONDOWN / LBUTTONDBLCLK
        {
            if (_settings.IsLocked || !_desktop.IsAttached || !IsVisible || _inputSource?.CompositionTarget is not { } target)
                return 0;
            var client = MousePoint(lParam);
            var hit = InputHitTest(target.TransformFromDevice.Transform(new Point(client.X, client.Y))) as Visual;
            // Start only over widget content, leaving viewport scrollbars to WPF.
            if (hit is null || hit != _view && !_view.IsAncestorOf(hit)) return 0;
            if (!DesktopNative.ClientToScreen(hwnd, ref client) || !DesktopNative.GetWindowRect(hwnd, out _dragRect)) return 0;
            _positionTimer.Stop();
            _dragStart = client;
            // Do not take foreground mouse capture from Explorer. Tracking exists
            // only for this held-button gesture and stops on physical release.
            _dragTimer.Start();
            handled = true;
        }
        else if (message == 0x0200 && _dragStart is not null) // MOUSEMOVE
        {
            handled = true;
            TrackDrag();
        }
        else if (message == 0x0202 && _dragStart is not null) // LBUTTONUP
        {
            handled = true;
            TrackDrag();
            EndDrag();
        }
        return 0;
    }
    private void OnDragTick(object? sender, EventArgs e) => TrackDrag();
    private void TrackDrag()
    {
        if (_dragStart is not { } start) return;
        if (_settings.IsLocked || !_desktop.IsAttached || !IsVisible || _readPointer() is not { } pointer)
        { EndDrag(); return; }
        if (pointer.Cancel) { EndDrag(); return; }
        // Screen coordinates remain valid when the cursor leaves this HWND or
        // Explorer owns capture. Do not use stale client coordinates/button flags.
        if (DesktopNative.GetWindowRect(new WindowInteropHelper(this).Handle, out var current))
        {
            var x = _dragRect.Left + pointer.X - start.X;
            var y = _dragRect.Top + pointer.Y - start.Y;
            if (x != current.Left || y != current.Top) { _desktop.MoveScreen(x, y); Constrain(); }
        }
        if (!pointer.PrimaryDown) EndDrag();
    }
    private static DesktopNative.Point MousePoint(nint lParam) => new()
    {
        X = unchecked((short)lParam.ToInt64()), Y = unchecked((short)(lParam.ToInt64() >> 16))
    };
    private void EndDrag(bool save = true)
    {
        if (_dragStart is null) return;
        _dragStart = null;
        _dragTimer.Stop();
        if (save && _desktop.IsAttached) { Constrain(); SavePosition(); }
    }
    private void OnDesktopLost()
    {
        if (IsClosed) return;
        EndDrag(save: false);
        Hide();
        _desktop.Detach();
        DesktopStatusChanged?.Invoke("Desktop connection lost. Use Retry desktop attachment from the tray.", "");
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Hide();
    }
    public void CloseForExit()
    {
        _allowClose = true;
        if (IsClosed) return;
        EndDrag();
        Hide();
        Cleanup();
        Close();
    }
    private void Cleanup()
    {
        _positionTimer.Stop();
        _positionTimer.Tick -= OnPositionTick;
        _dragTimer.Stop();
        _dragTimer.Tick -= OnDragTick;
        if (_inputSource is not null)
        {
            _inputSource.Disposed -= OnWindowDestroyed;
            if (!_inputSource.IsDisposed) _inputSource.RemoveHook(DragWindowProc);
        }
        _inputSource = null;
        _settings.SettingsChanged -= OnSettings;
        SystemEvents.DisplaySettingsChanged -= OnDisplays;
        _desktop.Dispose();
    }
    private void OpenMenu() { ContextMenu = BuildMenu(); ContextMenu.IsOpen = true; }
    public void Reveal()
    {
        if (IsClosed) return;
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (!_desktop.TryAttach(hwnd, out var reason, out var detail))
        {
            Hide();
            DesktopStatusChanged?.Invoke(reason, detail);
            return;
        }
        WindowState = WindowState.Normal;
        Show();
        RestorePosition();
        DesktopStatusChanged?.Invoke("Widget attached to the desktop.", "");
    }
    public void ResetPosition() { _positionTimer.Stop(); Place(null); SavePosition(); }
    public void RestorePosition() { _positionTimer.Stop(); Place(_settings.Position); }
    public ContextMenu BuildMenu() => BuildMenu(() => IsClosed ? null : this);
    internal ContextMenu BuildMenu(Func<WidgetWindow?> getWidget)
    {
        var menu = new ContextMenu();
        void Add(string text, Action action, bool check = false, bool selected = false)
        {
            var item = new MenuItem { Header = Localization.T(text), IsCheckable = check, IsChecked = selected };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        // A tray menu can outlive this HWND. Resolve its target when clicked.
        Add(IsVisible ? "Hide widget" : "Show widget", () =>
        {
            if (getWidget() is not { } widget) return;
            if (widget.IsVisible) widget.Hide(); else widget.Reveal();
        });
        Add("Settings", () => _openDiagnostics(true));
        Add("Diagnostics", () => _openDiagnostics(false));
        Add(_settings.IsLocked ? "Unlock widget" : "Lock widget", () => _settings.IsLocked = !_settings.IsLocked);
        Add("Reset position", () => getWidget()?.ResetPosition());
        if (!_desktop.IsAttached) Add("Retry desktop attachment", () => getWidget()?.Reveal());
        menu.Items.Add(new Separator());
        Add("Exit", _exit);
        return menu;
    }

    // Store monitor-relative DIPs, but move the HWND in physical pixels. This avoids treating
    // the virtual desktop as one uniform DPI space on mixed-scale monitors.
    private void Place(WidgetPosition? position)
    {
        if (_placing || !_desktop.IsAttached || IsClosed) return;
        _placing = true;
        try
        {
            var screen = Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == position?.Monitor)
                ?? Forms.Screen.PrimaryScreen!;
            var area = screen.WorkingArea;
            var hwnd = new WindowInteropHelper(this).Handle;
            // Move onto the destination monitor first so WPF receives WM_DPICHANGED and
            // reports the window's actual scale, without a system-DPI monitor query.
            if (Forms.Screen.FromHandle(hwnd).DeviceName != screen.DeviceName)
                _desktop.MoveScreen(area.Left, area.Top);
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            MaxHeight = area.Height / dpi;
            MaxWidth = area.Width / dpi;
            UpdateLayout();
            var point = RestoredPosition(position, screen.DeviceName,
                new Rect(area.X, area.Y, area.Width, area.Height), dpi,
                new Size(ActualWidth * dpi, ActualHeight * dpi));
            _desktop.MoveScreen((int)Math.Round(point.X), (int)Math.Round(point.Y));
        }
        finally { _placing = false; }
    }
    internal static Point RestoredPosition(WidgetPosition? position, string monitor, Rect area, double dpi, Size pixels)
    {
        var x = position is not null && position.Monitor == monitor ? area.Left + position.X * dpi : area.Right - pixels.Width - 24 * dpi;
        var y = position is not null && position.Monitor == monitor ? area.Top + position.Y * dpi : area.Bottom - pixels.Height - 24 * dpi;
        return ClampPosition(area, x, y, pixels.Width, pixels.Height);
    }
    private static Point ClampPosition(Rect area, double x, double y, double width, double height) => new(
        Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width)),
        Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - height)));

    private void Constrain()
    {
        if (_placing || !IsLoaded || IsClosed || !_desktop.IsAttached) return;
        _placing = true;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (!DesktopNative.GetWindowRect(hwnd, out var rect)) return;
            var area = Forms.Screen.FromHandle(hwnd).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            MaxHeight = area.Height / dpi;
            MaxWidth = area.Width / dpi;
            var point = ClampPosition(new Rect(area.X, area.Y, area.Width, area.Height),
                rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            _desktop.MoveScreen((int)Math.Round(point.X), (int)Math.Round(point.Y));
        }
        finally { _placing = false; }
    }
    private void SavePosition()
    {
        if (!_desktop.IsAttached || IsClosed) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!DesktopNative.GetWindowRect(hwnd, out var rect)) return;
        var screen = Forms.Screen.FromHandle(hwnd);
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _settings.SetPosition(new(screen.DeviceName, (rect.Left - screen.WorkingArea.Left) / dpi, (rect.Top - screen.WorkingArea.Top) / dpi));
    }
}
