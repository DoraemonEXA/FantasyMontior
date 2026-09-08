using System.Globalization;
using System.IO;
using System.Text.Json;

namespace FantasyMontior.ViewModels;

public sealed record LanguageChoice(string Code, string Label);
public sealed record DeviceSources(string Name, string Kind, string? UsageKey = null, string? TemperatureKey = null);
public sealed record WidgetPosition(string Monitor, double X, double Y);
public sealed record UserSettings(string Language, int SampleSeconds, double FontSize = 16,
    WidgetPosition? Position = null, Dictionary<string, DeviceSources>? Sources = null,
    bool IsLocked = true, double Transparency = 0);

public sealed class SettingsViewModel : ObservableObject
{
    private readonly string _path;
    private string _language;
    private int _sampleSeconds;
    private double _fontSize = 16;
    private double _transparency;
    private bool _isLocked = true;
    public WidgetPosition? Position { get; private set; }
    public Dictionary<string, DeviceSources> Sources { get; } = new(StringComparer.Ordinal);
    private string _saveError = "";
    private string _errorContext = "Could not load settings: {0}";
    public event Action? SettingsChanged;
    public LanguageChoice[] Languages { get; } = [new("en", "English"), new("zh-CN", "简体中文")];
    public int[] Intervals { get; } = [1, 2, 5, 10];
    public string SaveError => _saveError.Length == 0 ? "" : Localization.F(_errorContext, _saveError);
    public SettingsViewModel(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FantasyMontior", "settings.json");
        _language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "zh-CN" : "en";
        _sampleSeconds = 1;
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_path)) is { } settings)
            {
                if (settings.Language is "en" or "zh-CN") _language = settings.Language;
                if (Intervals.Contains(settings.SampleSeconds)) _sampleSeconds = settings.SampleSeconds;
                if (double.IsFinite(settings.FontSize) && settings.FontSize is >= 12 and <= 28) _fontSize = settings.FontSize;
                _isLocked = settings.IsLocked;
                if (double.IsFinite(settings.Transparency) && settings.Transparency is >= 0 and <= .8) _transparency = settings.Transparency;
                if (settings.Position is { } position && !string.IsNullOrWhiteSpace(position.Monitor) &&
                    double.IsFinite(position.X) && double.IsFinite(position.Y)) Position = position;
                if (settings.Sources is not null)
                    foreach (var (id, source) in settings.Sources)
                        if (!string.IsNullOrWhiteSpace(id) && source is not null &&
                            source.Kind is "Cpu" or "GpuNvidia" or "GpuAmd" or "GpuIntel" or "Memory")
                            Sources[id] = source with
                            {
                                Name = string.IsNullOrWhiteSpace(source.Name) ? id : source.Name,
                                UsageKey = string.IsNullOrWhiteSpace(source.UsageKey) ? null : source.UsageKey,
                                TemperatureKey = string.IsNullOrWhiteSpace(source.TemperatureKey) ? null : source.TemperatureKey
                            };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _saveError = ex.Message; }
    }
    public string Language
    {
        get => _language;
        set { if (value is "en" or "zh-CN" && Set(ref _language, value)) Save(); }
    }
    public int SampleSeconds
    {
        get => _sampleSeconds;
        set { if (Intervals.Contains(value) && Set(ref _sampleSeconds, value)) Save(); }
    }
    public double FontSize
    {
        get => _fontSize;
        set { if (double.IsFinite(value) && value is >= 12 and <= 28 && Set(ref _fontSize, value)) Save(); }
    }
    public double Transparency
    {
        get => _transparency;
        set
        {
            if (!double.IsFinite(value) || value is < 0 or > .8 || !Set(ref _transparency, value)) return;
            Changed(nameof(WidgetOpacity));
            Save();
        }
    }
    public double WidgetOpacity => 1 - Transparency;
    public bool IsLocked
    {
        get => _isLocked;
        set { if (Set(ref _isLocked, value)) Save(); }
    }
    public void SetPosition(WidgetPosition position) { Position = position; Save(notify: false); }
    public void SetSource(string id, DeviceSources source)
    {
        if (Sources.TryGetValue(id, out var previous) && previous == source) return;
        Sources[id] = source;
        Save();
    }
    public void RememberDevices(IEnumerable<KeyValuePair<string, DeviceSources>> sources)
    {
        var changed = false;
        foreach (var (id, source) in sources) changed |= Sources.TryAdd(id, source);
        if (changed) Save(notify: false);
    }
    private void Save(bool notify = true)
    {
        if (notify) SettingsChanged?.Invoke();
        _errorContext = "Could not save settings: {0}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(new UserSettings(Language, SampleSeconds,
                FontSize, Position, Sources, IsLocked, Transparency)));
            File.Move(_path + ".tmp", _path, true);
            _saveError = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _saveError = ex.Message; }
        Changed(nameof(SaveError));
    }
}
