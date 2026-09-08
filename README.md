# FantasyMontior

A Windows 10+ desktop hardware widget built with .NET 9, WPF, and pinned [LibreHardwareMonitorLib 0.9.6](https://www.nuget.org/packages/LibreHardwareMonitorLib/0.9.6).

The default view is a recreation of the Final Fantasy XIII party-status menu adapted to CPU, GPU, and physical RAM readings: staggered translucent strips, metallic rails, simple SVG status icons, and green utilization bars. The diagnostic window remains available from the tray or widget context menu. Usage fills contain a subtle FF13-inspired liquid/starfield effect: two seamless vector layers drift at different speeds over the green gradient, clipped to the measured fill. Animation is capped at 24 fps and stops for hidden/unloaded widgets, empty bars, and stale or unavailable usage; sensor polling is unchanged. When a live usage reading changes, the fill eases toward its target over 450 ms at up to 24 fps, retargeting from its current displayed position if interrupted. Numeric readings update immediately. Hidden/unloaded bars and non-live readings snap to their latest target and release transition clocks. Widget rows have no hover tooltips; sensor details remain in Settings/Diagnostics. Other game-like motion effects remain deferred.

## Run

Use Windows and the .NET 9 SDK:

```powershell
dotnet restore FantasyMontior.sln
dotnet build FantasyMontior.sln --no-restore
dotnet run --project FantasyMontior/FantasyMontior.csproj --no-build
```

The app requests administrator access through the Windows UAC prompt at launch for low-level sensor access through PawnIO. Approve the prompt to open the app; cancelling leaves it closed. PawnIO installation is still a separate setup step. Rider and `dotnet run` can start the executable normally: its manifest uses `asInvoker`, then startup requests elevation with Windows `runas` before constructing the sensor window. The unelevated launcher exits after the handoff. For debugging sensor code, run Rider as administrator so the original process stays under the debugger; an unelevated debugger does not automatically follow the elevated child. The probe and test runners retain their existing privileges.

Sensor discovery and collection run asynchronously on a serialized background worker. During startup or rediscovery, both sensor tabs show a localized loading panel with an indeterminate progress bar until the first collection finishes. Settings and Sensor setup remain accessible. A discovery failure ends the loading state and exposes the existing error and retry controls.

## Desktop widget

- The widget starts locked. Right-click it or the tray icon and choose **Unlock widget**, then drag a row to any position within a monitor's desktop work area. Choose **Lock widget** to prevent accidental dragging. Lock state and position persist across restarts. The menu also offers **Show/Hide**, **Settings**, **Diagnostics**, **Reset position**, and **Exit**. Double-click the tray icon to show it on the desktop without raising it over applications.
- The widget attaches to Explorer’s desktop icon host using C# P/Invoke, with no additional dependencies. It is designed to remain clickable on the desktop and visible after **Show Desktop / Win+D**, below normal applications. It is hidden from the taskbar and has no always-on-top option. Old settings files remain readable; their obsolete `AlwaysOnTop` value is ignored. Closing diagnostics leaves monitoring running; closing the widget hides it. Use **Exit** to release hardware and remove the tray icon.
- If desktop attachment fails or Explorer replaces its host, the widget stays hidden while monitoring and tray controls continue. Open **Diagnostics** for the status and choose **Retry desktop attachment** from the tray when ready; recovery is manual. Windows 10 1607+ and compatible Explorer/WPF DPI awareness are required for attachment. Diagnostics and tray remain available when attachment is unsupported.
- Font size defaults to 16 and adjusts from 12–28 in Settings, scaling the composition together. Widget transparency adjusts from 0–80%, applies immediately to the entire widget, and is saved automatically (default 0%, preserving the original appearance). The bottom notice footer is removed; sensor details remain available in Diagnostics. Position is saved relative to its monitor; unavailable monitors fall back to the primary work area. A scrollable viewport handles limited desktop space.
- CPU/GPU rows show utilization and temperature; RAM shows physical memory utilization. Bars represent utilization, not remaining capacity or temperature severity. Multiple CPU/GPU devices remain separate.
- **Widget sensors** in Settings chooses usage and temperature sources. Automatic selection recognizes total/core/package roles within the identified device. Unknown or ambiguous roles stay unavailable until explicitly selected. Saved sources never silently switch when disconnected.
- `—` means unavailable; stale values retain their last reading with a visible stale label and muted appearance. Full device/source names are available in Diagnostics. Raw readings and prerequisite guidance remain in Diagnostics.
- SVG assets live in `FantasyMontior/Resources/Icons/`; their restricted path-only loader creates cached WPF drawings. Panel textures are static vector gradients. Usage-bar animations run only while the fill is active and live.

## Inspect readings

- **CPU & GPU:** expand or collapse each device. Temperatures appear first, followed by the reported load sensors. Source names distinguish package/core/hotspot temperatures and different utilization engines; the app does not synthesize an aggregate.
- **All sensors:** inspect CPU, GPU, memory, motherboard, storage, network, and controller readings. Filter by device, name, type, or identifier. Click column headers to sort; Value sorts numerically. Row identities, selection, and scroll remain stable during ordinary refreshes.
- **Live** means a finite value was returned. It does **not** establish accuracy. Valid zero values are preserved.
- **Unavailable** means no finite reading exists. **Stale** means the last value belongs to a failed update or is at least three sampling intervals old (three seconds at the default interval).
- Open **Diagnostics** from the widget or tray to access the original sensor tabs, retry, setup, and clipboard export.
- **Retry discovery** waits for the current hardware call, closes access, and opens a new monitoring instance. A cleanup failure prevents reopening until cleanup succeeds. Exiting the application also waits for active access to finish; an unresponsive native driver can delay shutdown.
- **Copy diagnostics** copies JSON with environment/prerequisite information, the current immutable snapshot, source identifiers, units, timestamps, per-reading states, and errors.

The library can return duplicate sensor identifiers, including different GPU load sensors sharing one identifier. The app preserves that original identifier and creates a separate internal identity using type and source label **only for collisions**. It reports the collision rather than silently merging readings. Indistinguishable duplicates are reported as a collection failure.

## Settings and language

Open **Settings / 设置** to switch between **English** and **简体中文**, and choose a sensor sampling interval of **1, 2, 5, or 10 seconds**. Changes apply immediately without reopening hardware. Polling remains serialized; the interval is the wait between completed collection cycles, so slow hardware calls can lengthen it.

Preferences are saved automatically to `%LOCALAPPDATA%\FantasyMontior\settings.json` for the account running the app. First launch follows the Windows UI language (Chinese uses Simplified Chinese; other languages use English) and defaults to one second. Invalid or missing settings use defaults; file access failures appear in Settings. Hardware names, source sensor labels, identifiers, and raw diagnostic/error details retain their original text.

## Missing or suspicious temperatures

Open **Sensor setup**, or use the **CPU temperature missing or stuck at 0°C?** button above the readings. The scrollable guide shows the startup prerequisite status, links to the official PawnIO download, explains installation and restart, and provides an **Open application folder** button to locate the executable. It includes troubleshooting and an official LibreHardwareMonitor comparison link. The guide explains the startup administrator prompt; opening it does not install drivers. Restart after installation: the library's startup driver detection is not refreshed by Retry discovery.

Some sensors need administrator privileges and working [PawnIO](https://pawnio.eu/) access. The library's [integration instructions](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/README.md) describe elevation; its [upstream discussion](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/discussions/2149) covers PawnIO requirements.

1. Launch FantasyMontior.exe and approve the Windows administrator prompt.
2. Check that the privilege label shows **Administrator**. No manual Run as administrator action is needed.
3. If PawnIO is missing, install the official signed distribution manually from its website, then restart the app and approve the startup prompt. The installation indicator establishes presence, not successful driver access.
4. If readings remain absent, implausible, or fixed at zero, record them as unverified. Compare the same sensor in the official LibreHardwareMonitor application under equivalent conditions.

Do not accept an all-zero CPU temperature as proof of retrieval merely because the API returned a number. Unsupported hardware can remain unavailable even with prerequisites installed.

## Development and automated checks

- `FantasyMontior.Core`: hardware adapter, recursive collection, immutable snapshots, serialized polling, and diagnostics. This project has no WPF dependency.
- `FantasyMontior`: static widget, tray, diagnostic window, settings, SVG assets, and an application-owned monitoring session shared by all windows.
- `FantasyMontior.Tests`: hardware-independent collection/lifecycle tests, with no WPF runtime dependency.
- `FantasyMontior.UiChecks`: standalone WPF presentation checks with a main-thread dispatcher and explicit process exit result.
- `FantasyMontior.Probe`: read-only command-line capture using the same monitoring service.

```powershell
dotnet test FantasyMontior.Tests/FantasyMontior.Tests.csproj --no-restore
dotnet run --project FantasyMontior.UiChecks --no-restore
dotnet build FantasyMontior.sln -c Release --no-restore
dotnet run --project FantasyMontior.Probe -c Release --no-build -- 600 artifacts/verification
```

The probe accepts a duration of 1–3600 seconds and an output directory. It writes `snapshot.json` throughout the run and `summary.json` after cleanup, including sensor ranges, process resource samples, errors, and shutdown duration. Exit code 0 means at least one live sensor was observed at completion, **not** that all required CPU/GPU metrics passed validation.

The standalone UI runner renders actual controls into `artifacts/ui/`, checks filtering and selection/scroll retention, and exercises stale/unavailable states. If `artifacts/verification/snapshot.json` exists, it also renders that real capture; otherwise it uses explicitly named test devices. It prints `UI_CHECKS: PASS` only after validation and dispatcher shutdown; failures print exceptions and return nonzero. Rendered images are static layout evidence, not an interactive desktop session or a physical DPI transition test. Generated artifacts are ignored by Git.

Keep WPF checks out of `testhost.exe`. Run them in the standalone UI runner with its normal main-thread dispatcher lifecycle, and check both assertions and process exit status.

## Widget verification

```powershell
dotnet run --project FantasyMontior.UiChecks --no-restore -- --widget-probe 60 artifacts/verification-widget
```

The normal UI runner uses labeled fixtures to check both languages, font sizes and rendering scales, dark/light/patterned backgrounds, missing/stale readings, settings persistence, usage-bar animation and cleanup, and shared monitoring lifetime. Controlled offscreen HWND fixtures cover desktop attachment, coordinate conversion, explicit retry, host destruction, widget recreation, and dragging when mouse capture or subsequent messages are lost.

The optional widget probe opens the real widget and tray at the runner's existing privileges. It requires desktop attachment throughout the capture and writes snapshots, resource measurements, and a labeled hardware image to the output directory. It also checks shared-backend lifetime and cleanup. A successful probe does not establish sensor accuracy or manual interaction acceptance.

Validate taskbar Show Desktop, Win+D, application overlap, physical mouse/tray/keyboard input, Explorer restart, and mixed-DPI monitor transitions separately in an interactive session. Scaled renders and offscreen fixtures do not establish those behaviors. Sensor accuracy and compatibility with other hardware and Windows versions require testing on those systems.

## Sharing diagnostics

Diagnostic exports and probe captures contain hardware names and identifiers, environment details, timestamps, readings, and errors. Error text can include local paths; screenshots can expose device details. Review and redact these files before posting them publicly. Generated captures stay in the Git-ignored `artifacts/` directory by default. Local settings, logs, credentials, and IDE workspace files should not be included in source or release uploads.
