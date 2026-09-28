# Project guidance

## Purpose and requirements

FantasyMontior is a Windows desktop hardware-monitoring widget with a visual style inspired by the Final Fantasy XIII battle status menu.

- Support Windows 10 and later.
- Use C#, .NET 9, WPF, and LibreHardwareMonitorLib.
- Display CPU and GPU usage and temperature, with support for additional available hardware sensors.
- Keep the widget readable, responsive, and inexpensive to run continuously on the desktop.
- The current phase is the static desktop widget. Follow the Final Fantasy XIII lower-right party-status visual style: staggered compact rows, silver rails, dark translucent strips, framed SVG icons, and green usage bars. Keep font size adjustable. Usage bars include FF13-style liquid/starry motion. Keep this effect clipped to the actual fill, bounded to 24 fps, and stopped for hidden/unloaded, empty, stale, or unavailable usage. Other animated game effects remain deferred.

## Repository state and layout

The repository contains the desktop widget and its retained diagnostic window, with LibreHardwareMonitorLib pinned to 0.9.6. See README.md for usage, verification commands, and manual/hardware validation requirements.

- `FantasyMontior.sln`: solution entry point.
- `FantasyMontior/FantasyMontior.csproj`: WPF executable targeting `net9.0-windows`, with nullable reference types and implicit usings enabled.
- `FantasyMontior/Properties/PublishProfiles/SingleFile.pubxml`: self-contained Windows x64 single-executable publishing; use `dotnet publish FantasyMontior/FantasyMontior.csproj -c Release -p:PublishProfile=SingleFile`. Native libraries extract at launch; PawnIO remains a separate prerequisite.
- `FantasyMontior/App.xaml` and `App.xaml.cs`: application lifecycle; `Resources/Theme.xaml` contains shared presentation resources.
- `FantasyMontior/WidgetView.xaml`, `WidgetWindow.cs`, and `WidgetApplication.cs`: static widget presentation, placement, context menus, and tray lifetime. MainWindow is the on-demand diagnostic/settings window.
- `FantasyMontior/DesktopWidgetHost.cs`: C# P/Invoke desktop attachment to Explorer's icon-view host, native child styles, DPI compatibility checks, screen/client coordinate conversion, and host-loss detection. Never fall back to a floating window or change Explorer's styles. Recovery is explicit through the tray, not automatic.
- `FantasyMontior/MonitoringSession.cs`: one application-owned monitoring service and shared widget/diagnostic view models. Closing diagnostics or hiding the widget must not stop polling or create another backend. Exit awaits cleanup before disposing the tray.
- `FantasyMontior.Core/SensorHistory.cs`: application-owned rolling 24-hour history, injectable clock/store, UTC minute summaries with gap segments, and timestamped maximum candidates for exact expiry. Record each successful sensor publication once, never the UI refresh ticks. Persist compressed versioned JSON under `%LOCALAPPDATA%/FantasyMontior/history/` on a background worker with atomic minute checkpoints and an exit flush. Keep collection running in memory if persistence fails; expose errors in Trends. History identity includes hardware, collision-safe sensor key, type, and unit.
- `FantasyMontior/TrendsView.xaml`, `ViewModels/TrendsViewModel.cs`, and `SensorTrendChart.cs`: default main-window dashboard, searchable history picker, and native WPF average/range charts. Keep percentage axes at 0–100, units separate, gaps visible, and redraws at most once per second while visible. Hidden/minimized/unloaded charts do no periodic work. Tab navigation uses names, not indexes. Widget Max 24h values follow the selected source and remain distinct from current reading state.
- `FantasyMontior/AutoStartService.cs`: opt-in per-user Task Scheduler registration with interactive administrator privileges and a 30-second logon delay. Task Scheduler is the source of truth; read/change it asynchronously, never register on settings load, and keep automated UI checks isolated through the service interface. Sign-out/sign-in acceptance remains manual.
- `FantasyMontior/AnimatedUsageBar.cs`: eases live fill changes over 450 ms from the current displayed value; keep sensor targets separate, and release clocks on completion, hide/unload, or non-live readings. Widget rows have no hover tooltips.
- `FantasyMontior/LiquidMeterEffect.cs`: tiled vector light currents inside usage fills; animation clocks must be removed when inactive, without changing sensor polling or bar values.
- `FantasyMontior/Resources/Icons`: original path-only SVGs rendered through a restricted packaged-asset loader; no external SVG input.
- `FantasyMontior.Core/WidgetSources.cs`: device-scoped role selection; unknown or ambiguous automatic sources stay unavailable, and explicit selections use collision-safe keys.
- `FantasyMontior/ViewModels`: stable sensor rows, device groups, filtering, and display state.
- `FantasyMontior/Localization.cs` and `Resources/Strings.json`: runtime English/Simplified Chinese presentation strings. Keep hardware identifiers, source labels, and diagnostic data unchanged.
- `ViewModels/SettingsViewModel.cs`: persisted language, sampling interval, font size, widget transparency (0–80%, default 0%), monitor-relative position, lock state, and sensor selections in `%LOCALAPPDATA%/FantasyMontior/settings.json`; obsolete `AlwaysOnTop` JSON fields are ignored. Transparency applies to WidgetView only; the widget has no bottom notice footer. Settings changes must retain serialized polling and an interval-aware stale threshold.
- `FantasyMontior.Core`: hardware adapter, snapshots, collector, monitoring lifecycle, and diagnostics; no WPF dependency.
- `FantasyMontior.Tests`: xUnit collection/lifecycle verification with no WPF runtime dependency.
- `FantasyMontior.UiChecks`: standalone WPF presentation verification with a normal main-thread dispatcher lifecycle.
- `FantasyMontior.Probe`: real-hardware capture and resource/cleanup measurements.

Preserve the existing `FantasyMontior` spelling in project names and namespaces unless a rename is requested. Update this guidance when the repository structure or development workflow materially changes.

## Implementation conventions

- Keep hardware access separate from presentation. Prefer services for sensor collection and view models with data binding for display state; reserve code-behind for window-specific interaction.
- Use reusable XAML resources for colors, brushes, typography, and menu styles.
- Avoid blocking the WPF UI thread during hardware discovery or polling. Serialize access to the monitoring instance, cancel polling on shutdown, and release hardware resources reliably.
- Identify hardware and sensors using their identifiers and types rather than collection order or display-name assumptions. Handle multiple GPUs and nested hardware where applicable.
- LibreHardwareMonitor 0.9.6 can return duplicate GPU sensor identifiers. Preserve original identifiers in diagnostics and disambiguate collisions by type/source label; never silently merge them. Keep this behavior covered by tests.
- Treat missing or inaccessible readings as unavailable, not zero. Show units clearly and distinguish unavailable or stale data from live measurements. Never present sample data as actual readings.
- A finite value establishes only that the library reported it. Missing PawnIO can produce misleading zero temperatures. Preserve raw values and show prerequisite guidance; do not declare sensor feasibility proven from those readings.
- Handle unsupported sensors, hardware-library failures, and permission limitations gracefully. Explain any required elevation; do not silently restart with administrator privileges.
- The WPF executable declares `asInvoker` in `app.manifest` so Rider and dotnet can create it. App startup checks the actual administrator token and requests elevation with `runas` before constructing MainWindow or opening hardware. Cancellation exits cleanly; a relaunch marker prevents prompt loops. Keep probe and test runners at their existing privileges. Debug sensor code from an elevated Rider instance: the debugger does not automatically follow the elevated child.
- Keep refresh frequency bounded and avoid overlapping polling cycles. Do not collect faster than the display needs.
- Keep the widget clickable on the desktop, below normal applications, with no foreground activation on show or retry. Keep monitor-relative DIPs in settings and convert screen pixels to the desktop parent's client coordinates when moving its child HWND. The DPI compatibility APIs require Windows 10 1607+; older systems retain diagnostics/tray with an unavailable attachment status.
- Dragging starts from the widget HWND's native mouse-down message and WPF content hit test, then uses a 16 ms dispatcher timer to read screen cursor position and physical primary-button state only during the gesture. Do not acquire foreground capture or require subsequent mouse-move/up messages from Explorer. Stop tracking on release, Escape, cancellation, hide, lock, host loss, and destruction; preserve scrollbar input and no idle pointer polling. Retain the capture-loss/missing-message regression in the standalone WPF runner.
- Native parent destruction can destroy a WPF child HWND. In the `Closed` path, forget the HWND before disposing the desktop host; do not reparent or restyle a window during native destruction. Recreate the widget only on tray interaction, retaining the application-owned monitoring session. WinForms NotifyIcon handles Explorer's TaskbarCreated notification.
- Keep changes focused, preserve unrelated work, and avoid unnecessary dependencies or speculative infrastructure.

## Build and validation

Run from the repository root on Windows with a .NET 9 SDK and WPF build support:

```powershell
dotnet restore FantasyMontior.sln
dotnet build FantasyMontior.sln --no-restore
dotnet run --project FantasyMontior/FantasyMontior.csproj --no-build
dotnet test FantasyMontior.Tests/FantasyMontior.Tests.csproj --no-restore
dotnet run --project FantasyMontior.UiChecks --no-restore
dotnet run --project FantasyMontior.Probe -- 600 artifacts/verification
```

Retain meaningful automated coverage for sensor identity, traversal, units, missing/stale data, lifecycle, and presentation behavior. Keep collection tests independent of physical sensors. The standalone WPF runner writes to ignored `artifacts/ui/` and optionally uses `artifacts/verification/snapshot.json` from a real probe. Never confuse test fixtures with hardware evidence.

Do not construct WPF Application instances on temporary xUnit/testhost threads. Without a normal dispatcher lifecycle, testhost can raise exceptions even after assertions pass. Keep WPF in the standalone runner, await full dispatcher shutdown, and check process exit status as well as assertions.

For UI changes, inspect both the widget and diagnostic window for readability, resizing, DPI scaling, and interaction. The WPF runner renders labeled widget fixtures over three backgrounds and checks shared-session window lifetime. `dotnet run --project FantasyMontior.UiChecks -- --widget-probe 60 artifacts/verification-widget` measures the real widget plus tray at the runner’s existing privileges; it never requests elevation. Keep fixture and real-hardware captures clearly labeled. For sensor changes, verify readings on real hardware and check unavailable-sensor behavior. A build or rendered/mock-data test does not establish manual interaction, real sensor accuracy, or compatibility on untested Windows versions. Report actual observations and remaining validation gaps. Driver installation and administrator comparison remain manual setup steps.

`DesktopChecks` uses controlled, offscreen HWND fixtures for attachment, coordinates, manual retry, host destruction, window recreation, and session/tray cleanup. These are not Explorer/Show Desktop acceptance. The real widget probe requires attachment throughout its capture and fails if the widget is unattached or loses its host. Verify the taskbar Show Desktop button, Win+D, application overlap, mouse input, Explorer restart, and mixed-DPI monitors separately in an interactive session.

## Public repository hygiene

Keep personal machine specifications, local paths, private development notes, and raw diagnostic captures out of committed documentation. Review diagnostic JSON and screenshots before sharing; they can expose hardware identifiers, environment details, and paths in errors. Keep generated captures in ignored `artifacts/` and never commit credentials, signing keys, or local settings.
