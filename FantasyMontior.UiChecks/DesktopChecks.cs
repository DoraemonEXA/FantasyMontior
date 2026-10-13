using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Xunit;

namespace FantasyMontior.UiChecks;

// Real HWNDs in our own process; this is NOT Explorer or Show Desktop evidence.
internal sealed class DesktopFixture : IDisposable
{
    private readonly HwndSource _parent = new(new HwndSourceParameters("FIXTURE desktop host")
    {
        WindowStyle = unchecked((int)0x90000000), PositionX = -10000, PositionY = -10000,
        Width = 4000, Height = 4000
    });
    private readonly HwndSource _icons;
    internal DesktopFixture()
    {
        _icons = new(new HwndSourceParameters("FIXTURE icon view")
        {
            ParentWindow = _parent.Handle, WindowStyle = 0x50000000, Width = 4000, Height = 4000
        });
    }
    internal DesktopTarget Target => new(_parent.Handle, _icons.Handle);
    internal void LoseIconView() => _icons.Dispose();
    public void Dispose() { _icons.Dispose(); _parent.Dispose(); }
}

internal static class DesktopChecks
{
    internal static async Task VerifyAsync(MonitoringSession session, string output)
    {
        VerifyScaledPositions();
        using var desktop = new DesktopFixture();
        var available = false;
        var discoveries = 0;
        var pointer = new PointerFixture();
        var widget = new WidgetWindow(session.Widget, _ => { }, () => { }, () =>
        {
            discoveries++;
            return available ? desktop.Target : null;
        }, pointer.Read);
        widget.DesktopStatusChanged += session.Diagnostics.SetDesktopStatus;
        try
        {
            widget.Reveal();
            Assert.False(widget.IsVisible);
            Assert.False(widget.IsDesktopAttached);
            Assert.Contains("桌面", session.Diagnostics.DesktopStatus);
            Assert.Contains(widget.BuildMenu().Items.OfType<MenuItem>(), m => Equals(m.Header, Localization.T("Retry desktop attachment")));
            available = true;
            await Task.Delay(2200);
            Assert.Equal(1, discoveries); // Availability alone never triggers recovery.
            Assert.False(widget.IsVisible);

            widget.Reveal();
            await Task.Delay(100);
            var hwnd = new WindowInteropHelper(widget).Handle;
            Assert.True(widget.IsDesktopAttached);
            Assert.True(widget.IsVisible);
            Assert.False(widget.Topmost);
            Assert.False(widget.ShowInTaskbar);
            Assert.False(widget.ShowActivated);
            Assert.Equal(desktop.Target.Window, DesktopNative.GetParent(hwnd));
            Assert.NotEqual(0L, DesktopNative.GetStyle(hwnd).ToInt64() & 0x40000000L);
            Assert.Equal(0L, DesktopNative.GetStyle(hwnd).ToInt64() & 0x80000000L);
            Assert.DoesNotContain(widget.BuildMenu().Items.OfType<MenuItem>(), m => Equals(m.Header, "Always on top"));

            await VerifyDragAsync(widget, session.Settings, pointer);
            await VerifyPositionRecoveryAsync(widget, session.Settings, desktop, pointer);
            widget.ResetPosition();
            DesktopNative.GetWindowRect(hwnd, out var before);
            Assert.True(before.Right > 0 && before.Bottom > 0); // Parent origin is -10000, not the widget's screen origin.
            widget.Hide();
            Assert.False(widget.IsVisible);
            widget.Reveal();
            DesktopNative.GetWindowRect(hwnd, out var after);
            Assert.InRange(Math.Abs(before.Left - after.Left), 0, 1);
            Assert.InRange(Math.Abs(before.Top - after.Top), 0, 1);
            Assert.Equal(2, discoveries); // Re-show uses the existing attachment.

            SendMessage(hwnd, 0x02E3, 0, 0);
            desktop.LoseIconView();
            await Task.Delay(2300);
            Assert.False(widget.IsVisible);
            Assert.False(widget.IsDesktopAttached);
            Assert.False(widget.IsPositionRestorePending);
            Assert.Equal(2, discoveries);
            Assert.Equal(0, DesktopNative.GetParent(hwnd));

            // Explicit recovery can attach the existing HWND to a new desktop host.
            using var replacement = new DesktopFixture();
            using var host = new DesktopWidgetHost(() => { }, () => replacement.Target);
            Assert.True(host.TryAttach(hwnd, out _, out _));
            Assert.True(host.MoveScreen(-400, -250));
            DesktopNative.GetWindowRect(hwnd, out var moved);
            Assert.Equal(-400, moved.Left);
            Assert.Equal(-250, moved.Top);
            host.Detach();
            Assert.False(DesktopNative.IsWindowVisible(hwnd));
            Assert.True(DesktopNative.IsWindow(replacement.Target.Window));
        }
        finally
        {
            widget.CloseForExit();
            widget.DesktopStatusChanged -= session.Diagnostics.SetDesktopStatus;
        }
        Assert.True(widget.IsClosed);
        Assert.False(widget.IsPositionRestorePending);

        // A different-awareness parent must be rejected BEFORE SetParent can reset
        // the WPF process's awareness. These native STATIC windows contain no WPF.
        var dpiWidget = new WidgetWindow(session.Widget, _ => { }, () => { });
        try
        {
            var hwnd = new WindowInteropHelper(dpiWidget).EnsureHandle();
            var originalDpi = DesktopNative.GetWindowDpiAwarenessContext(hwnd);
            var threadDpi = SetThreadDpiAwarenessContext(-1); // DPI_UNAWARE for fixture creation only.
            Assert.NotEqual(0, threadDpi);
            nint differentParent = 0, differentView = 0;
            try
            {
                differentParent = CreateWindowEx(0, "STATIC", "FIXTURE different DPI", unchecked((int)0x80000000),
                    -10000, -10000, 500, 500, 0, 0, 0, 0);
                differentView = CreateWindowEx(0, "STATIC", "FIXTURE icon view", 0x40000000,
                    0, 0, 500, 500, differentParent, 0, 0, 0);
            }
            finally { SetThreadDpiAwarenessContext(threadDpi); }
            try
            {
                Assert.NotEqual(0, differentParent);
                Assert.NotEqual(0, differentView);
                using var incompatible = new DesktopWidgetHost(() => { }, () => new(differentParent, differentView));
                Assert.False(incompatible.TryAttach(hwnd, out var reason, out _));
                Assert.Contains("DPI", reason);
                Assert.False(dpiWidget.IsVisible);
                Assert.True(DesktopNative.AreDpiAwarenessContextsEqual(originalDpi, DesktopNative.GetWindowDpiAwarenessContext(hwnd)));
            }
            finally
            {
                if (differentView != 0) DestroyWindow(differentView);
                if (differentParent != 0) DestroyWindow(differentParent);
            }
        }
        finally { dpiWidget.CloseForExit(); }

        // Exercise the real tray application's factory after native parent destruction,
        // retaining the application-owned monitoring session throughout recovery.
        using var lastDesktop = new DesktopFixture();
        using var nextDesktop = new DesktopFixture();
        var currentTarget = lastDesktop.Target;
        Console.WriteLine("DESKTOP_FIXTURE: testing native parent destruction and tray recovery.");
        await using (var shell = new WidgetApplication(session, () => currentTarget))
        {
            shell.Start();
            var original = shell.GetWidget();
            Assert.True(original.IsDesktopAttached);
            Console.WriteLine("DESKTOP_FIXTURE: destroying fixture parent.");
            lastDesktop.Dispose();
            Console.WriteLine("DESKTOP_FIXTURE: fixture parent destroyed.");
            await Task.Delay(2300);
            Assert.True(original.IsClosed || !original.IsVisible);
            Assert.False(original.IsDesktopAttached);
            currentTarget = nextDesktop.Target;
            // Opening a tray menu can create a replacement, but must not attach/show it.
            var recreated = shell.GetWidget();
            Assert.False(recreated.IsVisible);
            recreated.Reveal();
            Assert.True(recreated.IsDesktopAttached);
            Assert.Same(session.Widget, ((ScrollViewer)recreated.Content).Content is FrameworkElement view ? view.DataContext : null);
            shell.OpenDiagnostics(false);
        }
        File.WriteAllText(Path.Combine(output, "desktop-fixture-checks.txt"),
            "PASS: controlled HWND parenting, child styles, screen/client conversion, delayed placement recovery, parent movement, scaled geometry, hidden failure, manual retry, host loss and cleanup.\n" +
            "NOT VERIFIED: Explorer, Show Desktop/Win+D, physical input, fullscreen game transitions, physical mixed DPI, elevated app.\n");
        Console.WriteLine("DESKTOP_FIXTURE_CHECKS: PASS — controlled HWNDs only; not Explorer acceptance.");
    }

    private sealed class PointerFixture
    {
        internal DesktopPointer? State;
        internal int Reads;
        internal DesktopPointer? Read() { Reads++; return State; }
    }

    private static async Task VerifyPositionRecoveryAsync(WidgetWindow widget,
        ViewModels.SettingsViewModel settings, DesktopFixture desktop, PointerFixture pointer)
    {
        var hwnd = new WindowInteropHelper(widget).Handle;
        var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
        settings.SetPosition(new(screen.DeviceName, 120, 90));
        widget.RestorePosition();
        Assert.True(DesktopNative.GetWindowRect(hwnd, out var expected));
        var saved = settings.Position;
        var foreground = GetForegroundWindow();

        // A display notification can arrive before WPF/Explorer's final move.
        // Simulate that ordering without changing the user's display mode.
        SendMessage(hwnd, 0x007E, 32, 0); // WM_DISPLAYCHANGE
        await Task.Delay(80);
        var origin = new DesktopNative.Point { X = screen.WorkingArea.Left, Y = screen.WorkingArea.Top };
        Assert.True(DesktopNative.ScreenToClient(desktop.Target.Window, ref origin));
        Assert.True(DesktopNative.SetWindowPos(hwnd, 0, origin.X, origin.Y, 0, 0, 0x0015));
        await Task.Delay(650);
        AssertPosition(hwnd, expected);
        Assert.Equal(saved, settings.Position);

        // Moving the parent changes the child's screen coordinates without a
        // child WM_MOVE. Recovery must also observe the desktop host's geometry.
        Assert.True(DesktopNative.GetWindowRect(desktop.Target.Window, out var parent));
        Assert.True(DesktopNative.SetWindowPos(desktop.Target.Window, 0, parent.Left - 37, parent.Top + 19, 0, 0, 0x0015));
        await Task.Delay(650);
        AssertPosition(hwnd, expected);
        Assert.Equal(saved, settings.Position);
        Assert.False(widget.IsPositionRestorePending); // No feedback loop or idle placement timer.

        // Per-monitor-v2 children also receive a notification after the parent's
        // DPI update. Exercise the message path, without faking physical DPI.
        SendMessage(hwnd, 0x02E3, 0, 0); // WM_DPICHANGED_AFTERPARENT
        Assert.True(widget.IsPositionRestorePending);
        widget.Hide();
        Assert.False(widget.IsPositionRestorePending);
        SendMessage(hwnd, 0x007E, 32, 0);
        await Task.Delay(350);
        Assert.False(widget.IsVisible);
        Assert.False(widget.IsPositionRestorePending);
        widget.Reveal();
        AssertPosition(hwnd, expected);

        // A new drag supersedes pending automatic recovery. A display switch
        // during that drag cancels its now-invalid coordinate origin without saving it.
        settings.IsLocked = false;
        SendMessage(hwnd, 0x02E3, 0, 0);
        pointer.State = new(expected.Left + 80, expected.Top + 10, true);
        SendMouse(hwnd, 0x0201, 1, 100, 20);
        Assert.False(widget.IsPositionRestorePending);
        SendMouse(hwnd, 0x0200, 1, 100, 20);
        SendMessage(hwnd, 0x007E, 32, 0);
        var readsAfterDisplayChange = pointer.Reads;
        await Task.Delay(650);
        Assert.Equal(readsAfterDisplayChange, pointer.Reads);
        AssertPosition(hwnd, expected);
        Assert.Equal(saved, settings.Position);
        settings.IsLocked = true;
        await Task.Delay(350);
        Assert.False(widget.IsPositionRestorePending);
        Assert.Equal(foreground, GetForegroundWindow());
        Console.WriteLine("DESKTOP_POSITION_CHECKS: PASS - delayed displacement and parent movement restore saved coordinates without activation. Controlled HWNDs only.");
    }

    private static void VerifyScaledPositions()
    {
        var saved = new ViewModels.WidgetPosition("4K", 1000, 500);
        var desktop = new Rect(-3840, -300, 3840, 2080);
        foreach (var scale in new[] { 1.75, 2, 2.5 })
        {
            var size = new Size(360 * scale, 160 * scale);
            var expected = new Point(-3840 + 1000 * scale, -300 + 500 * scale);
            for (var cycle = 0; cycle < 3; cycle++)
            {
                Assert.Equal(expected, WidgetWindow.RestoredPosition(saved, "4K", desktop, scale, size));
                // A temporary lower-resolution game mode clamps the HWND only.
                var game = WidgetWindow.RestoredPosition(saved, "4K", new Rect(-1920, -300, 1920, 1000), scale, size);
                Assert.Equal(new Point(-size.Width, 700 - size.Height), game);
                Assert.Equal(expected, WidgetWindow.RestoredPosition(saved, "4K", desktop, scale, size));
            }
        }
        var fallback = new Rect(0, 0, 1920, 1040);
        Assert.Equal(new Point(1152, 672), WidgetWindow.RestoredPosition(saved, "PRIMARY", fallback, 2, new Size(720, 320)));
        Assert.Equal(new Point(1152, 672), WidgetWindow.RestoredPosition(null, "PRIMARY", fallback, 2, new Size(720, 320)));
        Assert.Equal(new Point(0, 0), WidgetWindow.RestoredPosition(saved, "4K", fallback, 2, new Size(4000, 2200)));
        Console.WriteLine("DESKTOP_SCALE_CHECKS: PASS - 175/200/250 percent geometry, repeated 4K/game-mode round trips, negative origin, fallback and oversized viewport. Simulated geometry only.");
    }

    private static void AssertPosition(nint hwnd, DesktopNative.Rect expected)
    {
        Assert.True(DesktopNative.GetWindowRect(hwnd, out var actual));
        Assert.InRange(Math.Abs(expected.Left - actual.Left), 0, 1);
        Assert.InRange(Math.Abs(expected.Top - actual.Top), 0, 1);
    }

    private static async Task VerifyDragAsync(WidgetWindow widget, ViewModels.SettingsViewModel settings, PointerFixture pointer)
    {
        var hwnd = new WindowInteropHelper(widget).Handle;
        widget.ResetPosition();
        DesktopNative.GetWindowRect(hwnd, out var before);
        var parent = DesktopNative.GetParent(hwnd);
        var foreground = GetForegroundWindow();
        // A real click first asks Windows whether to activate this background
        // child. Directly sending LBUTTONDOWN bypasses this decision and would
        // miss MA_ACTIVATEANDEAT (2), which discards the press before dragging.
        foreach (var buttonMessage in new[] { 0x0201, 0x0204 }) // Left/right down.
            Assert.Equal((nint)3, SendMessage(hwnd, 0x0021, parent, (nint)((buttonMessage << 16) | 1))); // HTCLIENT, MA_NOACTIVATE
        settings.IsLocked = true;
        pointer.State = new(before.Left + 40, before.Top - 10, true);
        SendMouse(hwnd, 0x0201, 1, 100, 20);
        SendMouse(hwnd, 0x0200, 1, 40, -10);
        SendMouse(hwnd, 0x0202, 0, 40, -10);
        DesktopNative.GetWindowRect(hwnd, out var locked);
        Assert.Equal(before.Left, locked.Left);
        Assert.Equal(before.Top, locked.Top);
        Assert.Equal(0, pointer.Reads);

        settings.IsLocked = false;
        SendMouse(hwnd, 0x0201, 1, 100, 20);
        Assert.NotEqual(hwnd, GetCapture());
        // Stale client coordinates and button flags must not override physical state.
        SendMouse(hwnd, 0x0200, 0, 100, 20);
        DesktopNative.GetWindowRect(hwnd, out var moved);
        Assert.Equal(before.Left - 60, moved.Left);
        Assert.Equal(before.Top - 30, moved.Top);
        SendMouse(hwnd, 0x0202, 0, 100, 20);
        Assert.NotEqual(hwnd, GetCapture());
        Assert.Equal(parent, DesktopNative.GetParent(hwnd));
        Assert.Equal(foreground, GetForegroundWindow());
        Assert.NotNull(settings.Position);
        widget.Hide();
        widget.Reveal();
        DesktopNative.GetWindowRect(hwnd, out var restored);
        Assert.InRange(Math.Abs(moved.Left - restored.Left), 0, 1);
        Assert.InRange(Math.Abs(moved.Top - restored.Top), 0, 1);

        // Reproduce background-window capture loss. Deliver no move/up messages to
        // the widget: the dispatcher must still follow the pointer and observe release.
        SendMouse(hwnd, 0x0201, 1, 100, 20);
        SetCapture(parent);
        Assert.Equal(parent, GetCapture());
        pointer.State = new(restored.Left + 70, restored.Top + 5, true);
        await Task.Delay(100);
        DesktopNative.GetWindowRect(hwnd, out var uncaptured);
        Assert.Equal(restored.Left - 30, uncaptured.Left);
        Assert.Equal(restored.Top - 15, uncaptured.Top);
        pointer.State = pointer.State.Value with { PrimaryDown = false };
        await Task.Delay(100);
        Assert.Equal(parent, GetCapture()); // Do not release someone else's capture.
        ReleaseCapture();
        var readsAfterRelease = pointer.Reads;
        pointer.State = new(0, 0, true);
        await Task.Delay(100);
        Assert.Equal(readsAfterRelease, pointer.Reads); // No idle pointer polling.
        widget.Hide();
        widget.Reveal();
        DesktopNative.GetWindowRect(hwnd, out var released);
        Assert.InRange(Math.Abs(uncaptured.Left - released.Left), 0, 1);
        Assert.InRange(Math.Abs(uncaptured.Top - released.Top), 0, 1);

        SendMouse(hwnd, 0x0201, 1, 100, 20);
        SendMessage(hwnd, 0x001F, 0, 0);
        Assert.NotEqual(hwnd, GetCapture());
        var readsAfterCancel = pointer.Reads;
        await Task.Delay(50);
        Assert.Equal(readsAfterCancel, pointer.Reads);
        SendMouse(hwnd, 0x0201, 1, 100, 20);
        settings.IsLocked = true;
        Assert.NotEqual(hwnd, GetCapture());
        settings.IsLocked = false;
        SendMouse(hwnd, 0x0201, 1, 100, 20);
        widget.Hide();
        Assert.NotEqual(hwnd, GetCapture());
        var readsAfterHide = pointer.Reads;
        await Task.Delay(50);
        Assert.Equal(readsAfterHide, pointer.Reads);
        widget.Reveal();
        settings.IsLocked = true;
        Assert.Equal(parent, DesktopNative.GetParent(hwnd));
        Assert.Equal(foreground, GetForegroundWindow());
        Console.WriteLine("DESKTOP_DRAG_CHECKS: PASS — activation preserves clicks, capture loss, missing move/up messages, physical-state fixture, persistence, cancellation, no idle polling/activation.");
    }

    private static void SendMouse(nint hwnd, int message, int buttons, int x, int y) =>
        SendMessage(hwnd, message, buttons, (nint)((y << 16) | (x & 0xffff)));

    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(int extendedStyle,
        string className, string title, int style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
}
