using System.Windows;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace FantasyMontior;

public sealed class WidgetApplication : IAsyncDisposable
{
    private readonly MonitoringSession _session;
    private readonly Func<DesktopTarget?>? _findDesktop;
    private WidgetWindow _widget;
    private readonly Forms.NotifyIcon _tray;
    private readonly Drawing.Icon _icon;
    private MainWindow? _diagnostics;
    private bool _exiting;
    private Task? _stop;

    public WidgetApplication(MonitoringSession session) : this(session, null) { }

    internal WidgetApplication(MonitoringSession session, Func<DesktopTarget?>? findDesktop)
    {
        _session = session;
        _findDesktop = findDesktop;
        _widget = CreateWidget();
        // One small original vector-like tray mark, independent of the hardware library.
        using var bitmap = new Drawing.Bitmap(32, 32);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.Clear(Drawing.Color.FromArgb(20, 30, 40));
            using var border = new Drawing.Pen(Drawing.Color.Silver, 2);
            graphics.DrawRectangle(border, 3, 5, 25, 21);
            using var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(140, 190, 95));
            graphics.FillRectangle(fill, 7, 10, 18, 3);
            graphics.FillRectangle(fill, 7, 18, 12, 3);
        }
        var handle = bitmap.GetHicon();
        try { using var temporary = Drawing.Icon.FromHandle(handle); _icon = (Drawing.Icon)temporary.Clone(); }
        finally { DestroyIcon(handle); }
        // NotifyIcon already handles TaskbarCreated and re-adds its icon after Explorer restarts.
        _tray = new Forms.NotifyIcon { Icon = _icon, Text = "FantasyMontior", Visible = true };
        _tray.DoubleClick += (_, _) => GetWidget().Reveal();
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button != Forms.MouseButtons.Right) return;
            var menu = GetWidget().BuildMenu();
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
        };
        Application.Current.MainWindow = _widget;
    }
    private WidgetWindow CreateWidget()
    {
        var widget = new WidgetWindow(_session.Widget, OpenDiagnostics, Exit, _findDesktop);
        widget.DesktopStatusChanged += _session.Diagnostics.SetDesktopStatus;
        return widget;
    }
    internal WidgetWindow GetWidget()
    {
        if (_widget.IsClosed && !_exiting)
        {
            _widget.DesktopStatusChanged -= _session.Diagnostics.SetDesktopStatus;
            _widget = CreateWidget();
            Application.Current.MainWindow = _widget;
        }
        return _widget;
    }
    public void Start() { GetWidget().Reveal(); _session.Start(); }
    public void OpenDiagnostics(bool settings)
    {
        if (_exiting) return;
        if (_diagnostics is null)
        {
            _diagnostics = new MainWindow(_session);
            _diagnostics.Closed += (_, _) => _diagnostics = null;
        }
        if (settings) _diagnostics.OpenSettings();
        _diagnostics.Show();
        if (_diagnostics.WindowState == WindowState.Minimized) _diagnostics.WindowState = WindowState.Normal;
        _diagnostics.Activate();
    }
    private async void Exit()
    {
        if (_exiting) return;
        await DisposeAsync();
        Application.Current.Shutdown();
    }
    public ValueTask DisposeAsync() => new(_stop ??= StopAsync());
    private async Task StopAsync()
    {
        _exiting = true;
        await _session.DisposeAsync();
        _diagnostics?.Close();
        _widget.CloseForExit();
        _widget.DesktopStatusChanged -= _session.Diagnostics.SetDesktopStatus;
        _tray.Visible = false;
        _tray.Dispose();
        _icon.Dispose();
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
