using System.Globalization;
using System.Text.Json;
using System.Windows;

namespace FantasyMontior;

public static class Localization
{
    private static readonly Dictionary<string, string[]> Resources = Load();
    public static string Language { get; private set; } = "en";
    public static int Revision { get; private set; }
    private static Dictionary<string, string[]> Load()
    {
        using var stream = typeof(Localization).Assembly.GetManifestResourceStream("FantasyMontior.Resources.Strings.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream)!;
    }
    public static string T(string text)
    {
        if (Language != "zh-CN") return text;
        return Resources.TryGetValue(text, out var pair) ? pair[1] : text;
    }
    public static string F(string text, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(text), args);
    public static void Apply(string language)
    {
        Language = language == "zh-CN" ? language : "en";
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(Language);
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
        foreach (var (key, values) in Resources)
            Application.Current.Resources["Text." + key] = values[Language == "zh-CN" ? 1 : 0];
        Revision++;
    }
}
