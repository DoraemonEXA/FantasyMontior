using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using FantasyMontior.Core;

namespace FantasyMontior.ViewModels;

public sealed record TrendChartData(HistorySeries? Series, DateTimeOffset Now, string Unit);
public sealed record HistorySensorChoice(HistorySensor Sensor)
{
    public SensorSeriesId Id => Sensor.Id;
    public string Label => $"{Sensor.HardwareName} · {Sensor.Name} ({Sensor.Id.Unit}) · {Sensor.Id.SensorKey}";
}

public sealed class TrendPlotViewModel : ObservableObject
{
    private TrendChartData _data = new(null, DateTimeOffset.UtcNow, "");
    public SensorSeriesId? Id { get; private set; }
    public TrendChartData Data { get => _data; private set => Set(ref _data, value); }
    public void Update(SensorSeriesId? id, HistorySeries? series, DateTimeOffset now, string unit)
    {
        Id = id;
        Data = new(series, now, unit);
    }
}

public sealed class TrendDeviceCard(WidgetRow row, string hardwareName)
{
    public WidgetRow Row { get; } = row;
    public string HardwareName { get; } = hardwareName;
    public TrendPlotViewModel Usage { get; } = new();
    public TrendPlotViewModel Temperature { get; } = new();
}

public sealed class TrendsViewModel : ObservableObject
{
    private readonly SensorHistoryService _history;
    private readonly WidgetViewModel _widget;
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private bool _active;
    private string _filter = "", _status = "", _detailTitle = "", _detailMaximum = "—";
    private HistorySensorChoice? _selected;
    private int _languageRevision = -1;
    public ObservableCollection<TrendDeviceCard> Devices { get; } = [];
    public ObservableCollection<HistorySensorChoice> Sensors { get; } = [];
    public ICollectionView SensorView { get; }
    public TrendPlotViewModel Detail { get; } = new();
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string DetailTitle { get => _detailTitle; private set => Set(ref _detailTitle, value); }
    public string DetailMaximum { get => _detailMaximum; private set => Set(ref _detailMaximum, value); }
    public bool IsActive
    {
        get => _active;
        set { if (Set(ref _active, value) && value) Refresh(DateTimeOffset.UtcNow, true); }
    }
    public string Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) SensorView.Refresh(); }
    }
    public HistorySensorChoice? SelectedSensor
    {
        get => _selected;
        set
        {
            // Filtering the picker must not erase the chart the user is inspecting.
            if (value is not null && Set(ref _selected, value)) UpdateDetail(DateTimeOffset.UtcNow);
        }
    }

    public TrendsViewModel(SensorHistoryService history, WidgetViewModel widget)
    {
        _history = history;
        _widget = widget;
        SensorView = CollectionViewSource.GetDefaultView(Sensors);
        SensorView.Filter = o => o is HistorySensorChoice sensor &&
            (string.IsNullOrWhiteSpace(Filter) || sensor.Label.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase) ||
             sensor.Id.Kind.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public void Select(TrendPlotViewModel plot)
    {
        if (plot.Id is null) return;
        Filter = "";
        SelectedSensor = Sensors.FirstOrDefault(s => s.Id == plot.Id);
    }

    public void Refresh(DateTimeOffset now, bool force = false)
    {
        if (!IsActive || !force && now >= _lastRefresh && now - _lastRefresh < TimeSpan.FromSeconds(1) &&
            _languageRevision == Localization.Revision) return;
        _lastRefresh = now;
        _languageRevision = Localization.Revision;
        var status = _history.Status;
        Status = status.Loading ? Localization.T("Loading saved history…") : Localization.T("Recording · Last 24 hours · 1-minute averages and ranges");
        if (status.LoadError is not null) Status += "\n" + Localization.F("Saved history could not be loaded: {0}", status.LoadError);
        if (status.SaveError is not null) Status += "\n" + Localization.F("History is recording in memory; saving failed: {0}", status.SaveError);

        var descriptors = _history.Sensors.OrderBy(s => s.HardwareName, StringComparer.Ordinal)
            .ThenBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Id.SensorKey, StringComparer.Ordinal).ToArray();
        var ids = descriptors.Select(s => s.Id).ToHashSet();
        foreach (var removed in Sensors.Where(s => !ids.Contains(s.Id)).ToArray()) Sensors.Remove(removed);
        for (var i = 0; i < descriptors.Length; i++)
        {
            var descriptor = descriptors[i];
            var choice = Sensors.FirstOrDefault(s => s.Id == descriptor.Id);
            if (choice is null) Sensors.Insert(i, new(descriptor));
            else
            {
                if (Sensors.IndexOf(choice) != i) Sensors.Move(Sensors.IndexOf(choice), i);
                if (choice.Sensor != descriptor)
                {
                    var replacement = new HistorySensorChoice(descriptor);
                    Sensors[i] = replacement;
                    if (_selected?.Id == replacement.Id) { _selected = replacement; Changed(nameof(SelectedSensor)); }
                }
            }
        }
        if (_selected is null || !ids.Contains(_selected.Id))
        {
            _selected = Sensors.FirstOrDefault(s => s.Id == _widget.Rows.FirstOrDefault()?.UsageHistoryId) ?? Sensors.FirstOrDefault();
            Changed(nameof(SelectedSensor));
        }
        foreach (var removed in Devices.Where(d => !_widget.Rows.Contains(d.Row)).ToArray()) Devices.Remove(removed);
        for (var i = 0; i < _widget.Rows.Count; i++)
        {
            var row = _widget.Rows[i];
            var card = Devices.FirstOrDefault(d => d.Row == row);
            if (card is null)
            {
                card = new(row, _widget.Settings.Sources.GetValueOrDefault(row.Id)?.Name ?? row.Label);
                Devices.Insert(i, card);
            }
            else if (Devices.IndexOf(card) != i) Devices.Move(Devices.IndexOf(card), i);
            card.Usage.Update(row.UsageHistoryId, _history.GetSeries(row.UsageHistoryId), now, "%");
            card.Temperature.Update(row.TemperatureHistoryId, _history.GetSeries(row.TemperatureHistoryId), now, "°C");
        }
        UpdateDetail(now);
    }

    private void UpdateDetail(DateTimeOffset now)
    {
        var series = _history.GetSeries(_selected?.Id);
        Detail.Update(_selected?.Id, series, now, _selected?.Id.Unit ?? "");
        DetailTitle = _selected is { } selected ? $"{selected.Sensor.HardwareName} · {selected.Sensor.Name}" : Localization.T("Choose a sensor");
        DetailMaximum = series?.Maximum is { } max ? $"{max:0.##} {series.Sensor.Id.Unit}" : "—";
    }
}
