using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FantasyMontior;

internal readonly record struct DesktopTarget(nint Window, nint IconView);
internal readonly record struct DesktopPointer(int X, int Y, bool PrimaryDown, bool Cancel = false);

// Explorer's window hierarchy is an internal shell detail. Failure must leave our
// HWND hidden, never turn it into a floating fallback or modify Explorer itself.
internal sealed class DesktopWidgetHost : IDisposable
{
    private readonly Func<DesktopTarget?> _findDesktop;
    private readonly Action _lost;
    private readonly DispatcherTimer _health;
    private readonly DesktopNative.WinEventProc _eventProc;
    private DesktopTarget _target;
    private nint _hwnd, _eventHook, _originalStyle;
    private HwndSource? _source;
    private bool _disposed;
    internal bool IsAttached { get; private set; }

    internal DesktopWidgetHost(Action lost, Func<DesktopTarget?>? findDesktop = null)
    {
        _findDesktop = findDesktop ?? FindDesktop;
        _lost = lost;
        _health = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _health.Tick += OnHealth;
        _eventProc = OnShellEvent;
    }

    internal bool TryAttach(nint hwnd, out string reason, out string detail)
    {
        reason = detail = "";
        if (_disposed) throw new ObjectDisposedException(nameof(DesktopWidgetHost));
        if (IsAttached && Validate()) return true;
        Detach();
        if (_findDesktop() is not { } target)
        {
            reason = "Desktop attachment unavailable. Use Retry desktop attachment from the tray.";
            return false;
        }
        // Never allow SetParent to reset the DPI awareness of the entire WPF process.
        try
        {
            if (!DesktopNative.AreDpiAwarenessContextsEqual(DesktopNative.GetWindowDpiAwarenessContext(hwnd),
                    DesktopNative.GetWindowDpiAwarenessContext(target.Window)))
            {
                reason = "Desktop attachment blocked by incompatible DPI awareness. Use Retry desktop attachment from the tray.";
                return false;
            }
        }
        catch (EntryPointNotFoundException)
        {
            reason = "Desktop attachment needs Windows 10 version 1607 or later.";
            return false;
        }

        _hwnd = hwnd;
        _target = target;
        _originalStyle = DesktopNative.GetStyle(hwnd);
        try
        {
            DesktopNative.SetStyle(hwnd, (_originalStyle.ToInt64() & ~0x80000000L) | 0x40000000L); // POPUP -> CHILD
            Marshal.SetLastPInvokeError(0);
            if (DesktopNative.SetParent(hwnd, target.Window) == 0 && Marshal.GetLastPInvokeError() != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (DesktopNative.GetParent(hwnd) != target.Window)
                throw new InvalidOperationException("The desktop parent could not be verified.");
            // HWND_TOP is local to this parent's children, not the application's desktop z-order.
            DesktopNative.Check(DesktopNative.SetWindowPos(hwnd, 0, 0, 0, 0, 0, 0x0033));
            _source = HwndSource.FromHwnd(hwnd);
            _source?.AddHook(WindowProc);
            DesktopNative.GetWindowThreadProcessId(target.Window, out var process);
            _eventHook = DesktopNative.SetWinEventHook(0x8001, 0x8001, 0, _eventProc, process, 0, 0);
            // The lightweight check also covers shell reparenting and missed destroy notifications.
            IsAttached = true;
            if (!Validate()) throw new InvalidOperationException("The desktop host changed during attachment.");
            _health.Start();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Detach();
            reason = "Desktop attachment failed. Use Retry desktop attachment from the tray.";
            detail = ex.Message;
            return false;
        }
    }

    private bool Validate() => DesktopNative.IsWindow(_hwnd) && DesktopNative.IsWindow(_target.Window) &&
        DesktopNative.IsWindow(_target.IconView) && DesktopNative.GetParent(_hwnd) == _target.Window &&
        DesktopNative.GetParent(_target.IconView) == _target.Window;

    private void OnHealth(object? sender, EventArgs e) { if (IsAttached && !Validate()) _lost(); }
    private void OnShellEvent(nint hook, uint evt, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (IsAttached && objectId == 0 && childId == 0 && (hwnd == _target.Window || hwnd == _target.IconView))
        {
            var target = _target;
            // Leave the native callback before touching WPF, and discard notifications
            // for a previous attachment if a manual retry has already replaced it.
            _health.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsAttached && _target == target) _lost();
            }));
        }
    }

    private nint WindowProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        const int wmMouseActivate = 0x0021;
        const int maNoActivate = 3; // Deliver the press without activation; 2 discards it.
        if (msg == wmMouseActivate) { handled = true; return maNoActivate; }
        return 0;
    }

    internal bool MoveScreen(int x, int y)
    {
        if (!IsAttached || !Validate()) { if (IsAttached) _lost(); return false; }
        var point = new DesktopNative.Point { X = x, Y = y };
        if (!DesktopNative.ScreenToClient(_target.Window, ref point)) return false;
        return DesktopNative.SetWindowPos(_hwnd, 0, point.X, point.Y, 0, 0, 0x0015);
    }

    internal void Detach()
    {
        _health.Stop();
        IsAttached = false;
        if (_eventHook != 0) { DesktopNative.UnhookWinEvent(_eventHook); _eventHook = 0; }
        if (_source is { IsDisposed: false }) _source.RemoveHook(WindowProc);
        _source = null;
        if (_hwnd != 0 && DesktopNative.IsWindow(_hwnd))
        {
            DesktopNative.ShowWindow(_hwnd, 0);
            DesktopNative.SetParent(_hwnd, 0);
            // Best effort on teardown: never throw during hardware/tray cleanup.
            DesktopNative.TrySetStyle(_hwnd, _originalStyle.ToInt64());
            DesktopNative.SetWindowPos(_hwnd, 0, 0, 0, 0, 0, 0x0037);
        }
        _hwnd = 0;
        _target = default;
    }

    internal void WindowDestroyed()
    {
        // WM_DESTROY is already tearing down the HWND. Reparenting it from a WPF
        // Closed handler would re-enter native destruction and can terminate WPF.
        _hwnd = 0;
        _source = null;
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Detach();
        _health.Tick -= OnHealth;
        _disposed = true;
    }

    private static DesktopTarget? FindDesktop()
    {
        DesktopTarget? result = null;
        var shell = DesktopNative.GetShellWindow();
        if (shell == 0) return null;
        DesktopNative.GetWindowThreadProcessId(shell, out var shellProcess);
        DesktopNative.EnumWindows((hwnd, _) =>
        {
            var name = new StringBuilder(256);
            DesktopNative.GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() is not ("Progman" or "WorkerW")) return true;
            DesktopNative.GetWindowThreadProcessId(hwnd, out var process);
            if (process != shellProcess || !DesktopNative.IsWindowVisible(hwnd)) return true;
            var view = DesktopNative.FindWindowEx(hwnd, 0, "SHELLDLL_DefView", null);
            if (view == 0) return true;
            result = new(hwnd, view);
            return false;
        }, 0);
        return result;
    }
}

internal static class DesktopNative
{
    internal static DesktopPointer? ReadPointer() => GetCursorPos(out var point)
        ? new(point.X, point.Y, (GetAsyncKeyState(GetSystemMetrics(23) == 0 ? 1 : 2) & 0x8000) != 0,
            (GetAsyncKeyState(0x1B) & 0x8000) != 0)
        : null;
    [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left, Top, Right, Bottom; }
    internal delegate bool EnumProc(nint hwnd, nint state);
    internal delegate void WinEventProc(nint hook, uint evt, nint hwnd, int objectId, int childId, uint thread, uint time);
    internal static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastPInvokeError()); }
    internal static nint GetStyle(nint hwnd) => IntPtr.Size == 8 ? GetWindowLongPtr(hwnd, -16) : GetWindowLong(hwnd, -16);
    internal static void SetStyle(nint hwnd, long value) => Check(TrySetStyle(hwnd, value));
    internal static bool TrySetStyle(nint hwnd, long value)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = IntPtr.Size == 8 ? SetWindowLongPtr(hwnd, -16, (nint)value) : SetWindowLong(hwnd, -16, unchecked((int)value));
        return previous != 0 || Marshal.GetLastPInvokeError() == 0;
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong(nint hwnd, int index, int value);
    [DllImport("user32.dll")] internal static extern nint GetShellWindow();
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, nint state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint hwnd, StringBuilder name, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint FindWindowEx(nint parent, nint after, string name, string? title);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll")] internal static extern nint GetWindowDpiAwarenessContext(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetParent(nint hwnd, nint parent);
    [DllImport("user32.dll")] internal static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool ScreenToClient(nint hwnd, ref Point point);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(nint hook);
}
