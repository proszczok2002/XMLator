using System.Globalization;
using System.Text.Json;

namespace XMLator;

internal static class UiText
{
    internal static IReadOnlyList<Choice<string>> Languages { get; } =
    [
        new("pl", "Polski"), new("de", "Deutsch"), new("en", "English"), new("ru", "Русский"),
        new("hr", "Hrvatski"), new("fr", "Français"), new("es", "Español"), new("zh-Hans", "简体中文")
    ];

    private static readonly Dictionary<string, Lazy<Dictionary<string, string>>> Catalogs = Languages
        .Where(choice => choice.Value != "en")
        .ToDictionary(choice => choice.Value, choice => new Lazy<Dictionary<string, string>>(() => Read(choice.Value)));

    internal static string PreferenceCode(string code) => Languages.FirstOrDefault(item =>
        item.Value.Equals(code, StringComparison.OrdinalIgnoreCase))?.Value ?? "auto";

    internal static string FromWindows(string culture)
    {
        string language = culture.Split('-', 2)[0];
        return language.Equals("zh", StringComparison.OrdinalIgnoreCase) ? "zh-Hans"
            : Languages.FirstOrDefault(item => item.Value.Equals(language, StringComparison.OrdinalIgnoreCase))?.Value ?? "en";
    }

    internal static string CultureName(string code) => code switch
    {
        "pl" => "pl-PL", "de" => "de-DE", "ru" => "ru-RU", "hr" => "hr-HR", "fr" => "fr-FR",
        "es" => "es-ES", "zh-Hans" => "zh-CN", _ => "en-US"
    };

    internal static CultureInfo Culture(string code) => CultureInfo.GetCultureInfo(CultureName(code));

    internal static string Get(string language, string english) =>
        Catalogs.TryGetValue(language, out var catalog) && catalog.Value.TryGetValue(english, out var translated)
            ? translated : english;

    internal static string Format(string language, FormattableString english) =>
        string.Format(Culture(language), Get(language, english.Format), english.GetArguments());

    internal static string UsbDiagnostics(string language)
    {
        using var source = typeof(UiText).Assembly.GetManifestResourceStream("XMLator.UsbDiagnosticKeys.txt")!;
        using var reader = new StreamReader(source);
        var messages = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
            messages.Add(line, Get(language, line));
        return JsonSerializer.Serialize(messages);
    }

    private static Dictionary<string, string> Read(string language)
    {
        using var resource = typeof(UiText).Assembly.GetManifestResourceStream($"XMLator.Translations.{language}.json");
        return resource is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(resource) ?? [];
    }
}
