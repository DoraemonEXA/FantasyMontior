using System.Collections.ObjectModel;
using System.Windows;
using FantasyMontior.Core;

namespace FantasyMontior.ViewModels;

public sealed record SourceChoice(string Key, string Label);

public sealed class WidgetRow : ObservableObject
{
    private readonly SettingsViewModel _settings;
    private string _label = "", _usage = "—", _temperature = "—", _detail = "", _state = "";
    private double _fill, _readingOpacity = 1;
    private Thickness _indent;
    private bool _updatingChoices, _isUsageLive;
    public string Id { get; }
    public string Kind { get; }
    public string Icon => Kind == "Cpu" ? "cpu" : Kind == "Memory" ? "ram" : "gpu";
    public bool HasTemperature => Kind != "Memory";
    public string Label { get => _label; private set => Set(ref _label, value); }
    public string Usage { get => _usage; private set => Set(ref _usage, value); }
    public string Temperature { get => _temperature; private set => Set(ref _temperature, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public string State { get => _state; private set => Set(ref _state, value); }
    public double Fill { get => _fill; private set => Set(ref _fill, value); }
    public bool IsUsageLive { get => _isUsageLive; private set => Set(ref _isUsageLive, value); }
    public double ReadingOpacity { get => _readingOpacity; private set => Set(ref _readingOpacity, value); }
    public Thickness Indent { get => _indent; private set => Set(ref _indent, value); }
    public ObservableCollection<SourceChoice> UsageChoices { get; } = [];
    public ObservableCollection<SourceChoice> TemperatureChoices { get; } = [];
    public string UsageKey
    {
        get => _settings.Sources.GetValueOrDefault(Id)?.UsageKey ?? "";
        set { if (!_updatingChoices && value is not null && value != UsageKey) { _settings.SetSource(Id, Source with { UsageKey = value.Length == 0 ? null : value }); Changed(); } }
    }
    public string TemperatureKey
    {
        get => _settings.Sources.GetValueOrDefault(Id)?.TemperatureKey ?? "";
        set { if (!_updatingChoices && value is not null && value != TemperatureKey) { _settings.SetSource(Id, Source with { TemperatureKey = value.Length == 0 ? null : value }); Changed(); } }
    }
    private DeviceSources Source => _settings.Sources.GetValueOrDefault(Id) ?? new(Id, Kind);

    public WidgetRow(string id, string kind, SettingsViewModel settings) { Id = id; Kind = kind; _settings = settings; }

    public void Apply(MonitoringSnapshot snapshot, DateTimeOffset now, string label, int index)
    {
        Label = label;
        Indent = new Thickness(Math.Min(index, 4) * 9, 0, (4 - Math.Min(index, 4)) * 9, 3);
        var usage = WidgetSources.Resolve(snapshot, Id, Kind, "Load", Source.UsageKey);
        var temperature = HasTemperature ? WidgetSources.Resolve(snapshot, Id, Kind, "Temperature", Source.TemperatureKey) : null;
        var staleAfter = TimeSpan.FromSeconds(_settings.SampleSeconds * 3);
        var usageState = usage?.StateAt(now, staleAfter) ?? ReadingState.Unavailable;
        var temperatureState = temperature?.StateAt(now, staleAfter) ?? ReadingState.Unavailable;
        IsUsageLive = usageState == ReadingState.Live;
        Usage = Format(usage, "%");
        Temperature = HasTemperature ? Format(temperature, "°C") : "";
        Fill = usageState == ReadingState.Unavailable ? 0 : Math.Clamp(usage!.Value!.Value, 0, 100);
        State = usageState == ReadingState.Stale || HasTemperature && temperatureState == ReadingState.Stale
            ? Localization.T("Stale") : usageState == ReadingState.Unavailable || HasTemperature && temperatureState == ReadingState.Unavailable
                ? Localization.T("Unavailable") : "";
        ReadingOpacity = State.Length == 0 ? 1 : .55;
        Detail = $"{Source.Name}\n{Describe(usage)}" + (HasTemperature ? $"\n{Describe(temperature)}" : "");
        _updatingChoices = true;
        try
        {
            UpdateChoices(UsageChoices, WidgetSources.Candidates(snapshot, Id, "Load"), UsageKey);
            if (HasTemperature) UpdateChoices(TemperatureChoices, WidgetSources.Candidates(snapshot, Id, "Temperature"), TemperatureKey);
        }
        finally { _updatingChoices = false; }
        Changed(nameof(UsageKey));
        Changed(nameof(TemperatureKey));
    }

    private static string Format(SensorReading? reading, string unit) => reading?.Value is { } value && double.IsFinite(value)
        ? $"{value:0}{unit}" : "—";
    private static string Describe(SensorReading? reading) => reading is null ? Localization.T("Choose a sensor in Settings")
        : $"{reading.Name} · {reading.Key}\n{reading.LastSuccess?.ToLocalTime():G}";
    private static void UpdateChoices(ObservableCollection<SourceChoice> target, SensorReading[] candidates, string selected)
    {
        var choices = new List<SourceChoice> { new("", Localization.T("Automatic")) };
        choices.AddRange(candidates.Select(s => new SourceChoice(s.Key, $"{s.Name} · {s.Key}")));
        if (selected.Length > 0 && choices.All(c => c.Key != selected)) choices.Add(new(selected, $"{Localization.T("Unavailable")} · {selected}"));
        if (target.SequenceEqual(choices)) return;
        // Preserve existing items where possible; do not clear a bound SelectedValue on every reading.
        for (var i = 0; i < choices.Count; i++)
        {
            if (i < target.Count && target[i] == choices[i]) continue;
            if (i < target.Count) target[i] = choices[i]; else target.Add(choices[i]);
        }
        while (target.Count > choices.Count) target.RemoveAt(target.Count - 1);
    }
}

public sealed class WidgetViewModel : ObservableObject
{
    public SettingsViewModel Settings { get; }
    public ObservableCollection<WidgetRow> Rows { get; } = [];
    public WidgetViewModel(SettingsViewModel settings) => Settings = settings;

    public void Apply(MonitoringSnapshot snapshot, DateTimeOffset now)
    {
        var devices = snapshot.Hardware.Where(WidgetSources.IsWidgetDevice).ToArray();
        Settings.RememberDevices(devices.Select(h => KeyValuePair.Create(h.Id, new DeviceSources(h.Name, h.Kind))));
        var descriptors = Settings.Sources.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        if (!descriptors.Values.Any(d => d.Kind == "Cpu")) descriptors["waiting:cpu"] = new("CPU", "Cpu");
        if (!descriptors.Values.Any(d => d.Kind.StartsWith("Gpu", StringComparison.Ordinal))) descriptors["waiting:gpu"] = new("GPU", "GpuNvidia");
        if (!descriptors.Values.Any(d => d.Kind == "Memory")) descriptors["waiting:ram"] = new("RAM", "Memory");
        var ordered = descriptors.OrderBy(p => p.Value.Kind == "Cpu" ? 0 : p.Value.Kind == "Memory" ? 2 : 1)
            .ThenBy(p => p.Key, StringComparer.Ordinal).ToArray();
        foreach (var row in Rows.Where(r => !descriptors.ContainsKey(r.Id)).ToArray()) Rows.Remove(row);
        for (var i = 0; i < ordered.Length; i++)
        {
            var (id, device) = ordered[i];
            var row = Rows.FirstOrDefault(r => r.Id == id);
            if (row is null) { row = new(id, device.Kind, Settings); Rows.Insert(i, row); }
            else if (Rows.IndexOf(row) != i) Rows.Move(Rows.IndexOf(row), i);
            var category = device.Kind == "Cpu" ? "CPU" : device.Kind == "Memory" ? "RAM" : "GPU";
            var siblings = ordered.Where(p => (p.Value.Kind == "Cpu" ? "CPU" : p.Value.Kind == "Memory" ? "RAM" : "GPU") == category).ToArray();
            var label = category + (siblings.Length > 1 ? $" {Array.FindIndex(siblings, p => p.Key == id) + 1}" : "");
            row.Apply(snapshot, now, label, i);
        }
    }
}
