using static FantasyMontior.Localization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using FantasyMontior.Core;

namespace FantasyMontior.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(name);
        return true;
    }
}

public sealed class SensorRow : ObservableObject
{
    private SensorReading _reading;
    private ReadingState _state;
    private string _hardware;
    private int _languageRevision = Localization.Revision;
    public SensorRow(SensorReading reading, string hardware, DateTimeOffset now, TimeSpan? staleAfter = null)
    {
        _reading = reading;
        _hardware = hardware;
        _state = reading.StateAt(now, staleAfter);
    }
    public string Id => _reading.Id;
    public string Key => _reading.Key;
    public string Hardware => _hardware;
    public string Name => _reading.Name;
    public string Kind => T(_reading.Kind);
    public string RawKind => _reading.Kind;
    public string Unit => _reading.Unit;
    public double? NumericValue => _reading.Value;
    public string Value => T(_reading.FormattedValue);
    public string State => T(_state.ToString());
    public ReadingState RawState => _state;
    public string Detail => F("{0}\nLast valid reading: {1}\n{2}", Id, _reading.LastSuccess?.ToLocalTime().ToString("G") ?? T("Never"), _reading.Error ?? "");

    public void Update(SensorReading reading, string hardware, DateTimeOffset now, TimeSpan? staleAfter = null)
    {
        var state = reading.StateAt(now, staleAfter);
        if (_reading == reading && _hardware == hardware && _state == state && _languageRevision == Localization.Revision) return;
        _languageRevision = Localization.Revision;
        _reading = reading;
        _hardware = hardware;
        _state = state;
        Changed(string.Empty);
    }
}

public sealed class HardwareGroup(HardwareInfo hardware) : ObservableObject
{
    public void RefreshLanguage() => Changed(string.Empty);
    public bool MissingReadings { get; set; }
    public string Id => hardware.Id;
    public string Title => $"{hardware.Name} · {T(hardware.Kind)}";
    public ObservableCollection<SensorRow> Sensors { get; } = [];
    private string _coverage = "Waiting for readings";
    public string Coverage { get => T(_coverage); set => Set(ref _coverage, value); }
}

public sealed class MonitorViewModel : ObservableObject
{
    private readonly Dictionary<string, SensorRow> _rows = new(StringComparer.Ordinal);
    private MonitoringSnapshot _snapshot = MonitoringSnapshot.Empty;
    private string _filter = "";
    private string _status = "Initializing";
    private string _lastRefresh = "Waiting for first collection";
    private string _errors = "";
    private string _notice = "";
    private string _coverage = "Discovering CPU and GPU sensors…";

    public MonitorViewModel(RuntimeInfo? runtime = null, SettingsViewModel? settings = null)
    {
        Settings = settings ?? new SettingsViewModel();
        Runtime = runtime ?? RuntimeInfo.Detect();
        SensorView = CollectionViewSource.GetDefaultView(Sensors);
        SensorView.Filter = o => o is SensorRow row && Matches(row);
        Settings.SettingsChanged += () =>
        {
            if (Localization.Language != Settings.Language) Localization.Apply(Settings.Language);
            RefreshLanguage();
        };
    }

    public SettingsViewModel Settings { get; }
    public WidgetViewModel? Widget { get; internal set; }
    public TimeSpan StaleAfter => TimeSpan.FromSeconds(Settings.SampleSeconds * 3);
    public void RefreshLanguage()
    {
        Apply(_snapshot, DateTimeOffset.UtcNow);
        foreach (var group in Groups) group.RefreshLanguage();
        SensorView.Refresh();
        Changed(string.Empty);
    }
    public RuntimeInfo Runtime { get; }
    public ObservableCollection<SensorRow> Sensors { get; } = [];
    public ObservableCollection<HardwareGroup> Groups { get; } = [];
    public ICollectionView SensorView { get; }
    public string EnvironmentLabel => $"{T(Runtime.Privileges)}   •   PawnIO: {(Runtime.PawnIo.StartsWith("Installed", StringComparison.Ordinal) ? Runtime.PawnIo.Replace("Installed", T("Installed")) : T(Runtime.PawnIo))}   •   LibreHardwareMonitor {Runtime.LibraryVersion}";
    public string SetupStatus => Runtime.PawnIo.StartsWith("Installed", StringComparison.Ordinal)
        ? T("PawnIO was detected. Start at step 3; if readings are still missing, follow the troubleshooting steps below.")
        : Runtime.PawnIo == "Not detected"
            ? T("PawnIO was not detected. Start at step 1, then restart the app to check again.")
            : T("We could not confirm PawnIO’s installation status. Check whether it is installed, then restart the app. Use Copy diagnostics if this continues.");
    public string PrerequisiteNotice => Runtime.PawnIo.StartsWith("Installed", StringComparison.Ordinal)
        ? T("Live means a value was returned, not that accuracy has been verified.")
        : T("PawnIO is not confirmed installed. CPU/motherboard readings may be missing or misleading (including 0°C). Sensor feasibility is not yet proven.");
    public string Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) SensorView.Refresh(); }
    }
    public string Status { get => T(_status); private set => Set(ref _status, value); }
    public string LastRefresh { get => T(_lastRefresh); private set => Set(ref _lastRefresh, value); }
    public string Errors { get => _errors; private set => Set(ref _errors, value); }
    private string _desktopStatus = "Widget desktop attachment has not been requested.";
    private string _desktopDetail = "";
    public string DesktopStatus => T(_desktopStatus) + (_desktopDetail.Length == 0 ? "" : " " + _desktopDetail);
    internal void SetDesktopStatus(string status, string detail)
    {
        _desktopStatus = status;
        _desktopDetail = detail;
        Changed(nameof(DesktopStatus));
    }
    public string Notice { get => T(_notice); set => Set(ref _notice, value); }
    public string Coverage { get => T(_coverage); private set => Set(ref _coverage, value); }
    public string SensorCount => F("{0} detected sensors", Sensors.Count);
    public bool IsLoading => _status == "Initializing";
    public bool CanRetry => _status != "Initializing" && _status != "Stopping";

    private bool Matches(SensorRow row) => string.IsNullOrWhiteSpace(Filter) ||
        $"{row.Hardware} {row.Name} {row.Kind} {row.RawKind} {row.Id}".Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase);

    public void Apply(MonitoringSnapshot snapshot, DateTimeOffset now)
    {
        _snapshot = snapshot;
        Status = snapshot.Status;
        if (_status == "Monitoring" && now - snapshot.CapturedAt >= StaleAfter) Status = "Refresh delayed — readings may be stale";
        Changed(nameof(CanRetry));
        Changed(nameof(IsLoading));
        LastRefresh = snapshot.LastSuccessfulRefresh is { } time
            ? F("Last successful refresh: {0:HH:mm:ss}   •   {1:0}s ago", time.ToLocalTime(), Math.Max(0, (now - time).TotalSeconds))
            : IsLoading ? T("Waiting for first collection") : T("No successful refresh yet");
        Errors = string.Join(Environment.NewLine, snapshot.Errors);
        var hardware = snapshot.Hardware.ToDictionary(h => h.Id, StringComparer.Ordinal);
        var ids = snapshot.Sensors.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var removed in _rows.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            Sensors.Remove(_rows[removed]);
            _rows.Remove(removed);
        }
        foreach (var sensor in snapshot.Sensors)
        {
            var name = hardware.TryGetValue(sensor.HardwareId, out var device) ? device.Name : sensor.HardwareId;
            if (_rows.TryGetValue(sensor.Key, out var row)) row.Update(sensor, name, now, StaleAfter);
            else
            {
                row = new(sensor, name, now, StaleAfter);
                _rows.Add(sensor.Key, row);
                Sensors.Add(row);
            }
        }

        var primary = snapshot.Hardware.Where(h => h.IsCpuOrGpu &&
            (h.Id == h.RootId || !hardware.TryGetValue(h.RootId, out var root) || !root.IsCpuOrGpu)).ToArray();
        foreach (var removed in Groups.Where(g => primary.All(h => h.Id != g.Id)).ToArray()) Groups.Remove(removed);
        foreach (var device in primary)
        {
            var group = Groups.FirstOrDefault(g => g.Id == device.Id);
            if (group is null) { group = new(device); Groups.Add(group); }
            var relevant = snapshot.Sensors.Where(s => (s.Kind is "Load" or "Temperature") &&
                hardware.TryGetValue(s.HardwareId, out var h) && (h.Id == device.Id || h.RootId == device.Id))
                .OrderBy(s => s.Kind == "Temperature" ? 0 : 1).ToArray();
            var rows = relevant.Select(s => _rows[s.Key]).ToArray();
            foreach (var removed in group.Sensors.Except(rows).ToArray()) group.Sensors.Remove(removed);
            for (var i = 0; i < rows.Length; i++)
            {
                var current = group.Sensors.IndexOf(rows[i]);
                if (current < 0) group.Sensors.Insert(i, rows[i]);
                else if (current != i) group.Sensors.Move(current, i);
            }
            var loads = relevant.Count(s => s.Kind == "Load" && s.StateAt(now, StaleAfter) == ReadingState.Live);
            var temperatures = relevant.Count(s => s.Kind == "Temperature" && s.StateAt(now, StaleAfter) == ReadingState.Live);
            group.MissingReadings = loads == 0 || temperatures == 0;
            group.Coverage = F("Usage: {0}   •   Temperature: {1}", loads > 0 ? F("{0} live", loads) : T("MISSING / UNAVAILABLE"), temperatures > 0 ? F("{0} live", temperatures) : T("MISSING / UNAVAILABLE"));
        }
        var missing = new List<string>();
        if (!primary.Any(h => h.Kind == "Cpu")) missing.Add(T("CPU not detected"));
        if (!primary.Any(h => h.Kind.StartsWith("Gpu", StringComparison.Ordinal))) missing.Add(T("GPU not detected"));
        if (Groups.Any(g => g.MissingReadings)) missing.Add(T("Required readings missing or stale; inspect each device below"));
        Coverage = snapshot.Status == "Initializing" ? T("Discovering CPU and GPU sensors…")
            : missing.Count > 0 ? string.Join(". ", missing)
            : T("CPU/GPU values reported. Verify prerequisites and compare under workload before accepting them.");
        Changed(nameof(SensorCount));
    }

    public string CopyDiagnostics() => Diagnostics.ToJson(_snapshot, Runtime, DateTimeOffset.UtcNow, StaleAfter);
    public void SetStopping() { Status = "Stopping"; Changed(nameof(CanRetry)); Changed(nameof(IsLoading)); }
}
