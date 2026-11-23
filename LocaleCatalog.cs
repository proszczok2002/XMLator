using System.Globalization;
using Schneegans.Unattend;

namespace XMLator;

internal static class LocaleCatalog
{
    private static readonly string[] Popular = ["pl-PL", "en-US", "en-GB"];
    private static UnattendGenerator Engine => ModernOptions.Engine;

    private static readonly Dictionary<string, string> PolishLanguages = new()
    {
        ["pl-PL"] = "Polski (Polska)", ["en-US"] = "Angielski (Stany Zjednoczone)", ["en-GB"] = "Angielski (Wielka Brytania)",
        ["ar-SA"] = "Arabski", ["bg-BG"] = "Bułgarski", ["zh-CN"] = "Chiński uproszczony (Chiny)", ["zh-TW"] = "Chiński tradycyjny (Tajwan)",
        ["hr-HR"] = "Chorwacki", ["cs-CZ"] = "Czeski", ["da-DK"] = "Duński", ["et-EE"] = "Estoński",
        ["fi-FI"] = "Fiński", ["fr-FR"] = "Francuski (Francja)", ["fr-CA"] = "Francuski (Kanada)", ["el-GR"] = "Grecki",
        ["he-IL"] = "Hebrajski", ["es-ES"] = "Hiszpański (Hiszpania)", ["es-MX"] = "Hiszpański (Meksyk)",
        ["ja-JP"] = "Japoński", ["ko-KR"] = "Koreański", ["lt-LT"] = "Litewski", ["lv-LV"] = "Łotewski",
        ["nl-NL"] = "Niderlandzki", ["de-DE"] = "Niemiecki", ["nb-NO"] = "Norweski (Bokmål)",
        ["pt-BR"] = "Portugalski (Brazylia)", ["pt-PT"] = "Portugalski (Portugalia)", ["ru-RU"] = "Rosyjski", ["ro-RO"] = "Rumuński",
        ["sr-Latn-RS"] = "Serbski (łacinka)", ["sk-SK"] = "Słowacki", ["sl-SI"] = "Słoweński", ["sv-SE"] = "Szwedzki",
        ["th-TH"] = "Tajski", ["tr-TR"] = "Turecki", ["uk-UA"] = "Ukraiński", ["hu-HU"] = "Węgierski", ["it-IT"] = "Włoski"
    };

    private static readonly Dictionary<string, string> EnglishLanguages = new()
    {
        ["pl-PL"] = "Polish (Poland)", ["en-US"] = "English (United States)", ["en-GB"] = "English (United Kingdom)"
    };

    private static readonly Dictionary<string, string> PolishCountries = new()
    {
        ["Poland"] = "Polska", ["United States"] = "Stany Zjednoczone", ["United Kingdom"] = "Wielka Brytania",
        ["Canada"] = "Kanada", ["Croatia"] = "Chorwacja", ["France"] = "Francja",
        ["Germany"] = "Niemcy", ["Greece"] = "Grecja", ["Hungary"] = "Węgry", ["Italy"] = "Włochy", ["Japan"] = "Japonia",
        ["Portugal"] = "Portugalia", ["Romania"] = "Rumunia", ["Russia"] = "Rosja", ["Slovakia"] = "Słowacja",
        ["Spain"] = "Hiszpania", ["Sweden"] = "Szwecja", ["Türkiye"] = "Turcja", ["Ukraine"] = "Ukraina"
    };

    private static readonly Dictionary<string, string> PolishKeyboards = new(StringComparer.OrdinalIgnoreCase)
    {
        ["00000415"] = "Polski (programisty)", ["00010415"] = "Polski (214)", ["00000409"] = "Angielski (USA)",
        ["00000809"] = "Angielski (Wielka Brytania)", ["00020409"] = "Angielski (USA, międzynarodowy)",
        ["00000407"] = "Niemiecki", ["0000040c"] = "Francuski", ["0000040a"] = "Hiszpański",
        ["00000410"] = "Włoski", ["00000419"] = "Rosyjski", ["00000422"] = "Ukraiński", ["00000405"] = "Czeski",
        ["00000411"] = "Japoński", ["00000408"] = "Grecki", ["0000040e"] = "Węgierski"
    };

    private static readonly Lazy<IReadOnlyDictionary<string, RegionInfo>> Regions = new(() =>
        CultureInfo.GetCultures(CultureTypes.SpecificCultures)
            .Select(culture => TryRegion(culture.Name)).OfType<RegionInfo>()
            .GroupBy(region => region.GeoId.ToString(CultureInfo.InvariantCulture))
            .ToDictionary(group => group.Key, group => group.First()));

    public static Choice<string>[] Languages(bool polish) => Languages(polish ? "pl" : "en");
    public static Choice<string>[] Languages(string uiLanguage) => Sort(Engine.ImageLanguages.Values.Select(language =>
        new Choice<string>(language.Id, uiLanguage switch
        {
            "pl" => PolishLanguages.GetValueOrDefault(language.Id, language.DisplayName),
            "en" => EnglishLanguages.GetValueOrDefault(language.Id, language.DisplayName),
            _ => IcuLocaleNames.DisplayName(language.Id, uiLanguage) ?? language.DisplayName
        })), uiLanguage, Popular);

    public static Choice<string>[] Formats(string uiLanguage) => Sort(Engine.UserLocales.Values
        .Where(locale => SpecificCulture(locale.Id) is not null).Select(locale => new Choice<string>(locale.Id, uiLanguage switch
        {
            "pl" => PolishLanguages.GetValueOrDefault(locale.Id, IcuLocaleNames.DisplayName(locale.Id, "pl") ?? locale.DisplayName),
            "en" => locale.DisplayName,
            _ => IcuLocaleNames.DisplayName(locale.Id, uiLanguage) ?? locale.DisplayName
        })), uiLanguage, Popular);

    public static Choice<string>[] Countries(string uiLanguage) => Sort(Engine.GeoLocations.Values.Select(country =>
    {
        string? region = Regions.Value.GetValueOrDefault(country.Id)?.TwoLetterISORegionName;
        string localized = region is not null && uiLanguage != "en"
            ? IcuLocaleNames.Country(region, uiLanguage) ?? country.DisplayName : country.DisplayName;
        return new Choice<string>(country.Id, uiLanguage == "pl" ? PolishCountries.GetValueOrDefault(country.DisplayName, localized) : localized);
    }), uiLanguage, ["191", "244", "242"]);

    public static Choice<string>[] Keyboards(bool polish) => Keyboards(polish ? "pl" : "en");
    public static Choice<string>[] Keyboards(string uiLanguage)
    {
        var layouts = Engine.KeyboardIdentifiers.Values.Where(keyboard => keyboard.Type == InputType.IME || keyboard.Id.Length == 8)
            .ToDictionary(keyboard => InputProfile(keyboard), keyboard => KeyboardName(keyboard, uiLanguage), StringComparer.OrdinalIgnoreCase);
        foreach (var language in Languages(uiLanguage))
        {
            string input = DefaultKeyboard(language.Value, FormatLocale(language.Value));
            var resolved = ResolveKeyboard(input, language.Value);
            string caption = resolved is null ? language.Label : $"{KeyboardName(resolved.Keyboard, uiLanguage)} — {language.Label}";
            layouts.TryAdd(input, caption);
        }
        return Sort(layouts.Select(entry => new Choice<string>(entry.Key, entry.Value)), uiLanguage,
            ["0415:00000415", "0415:00010415", "0409:00000409", "0809:00000809"]);
    }

    public static Choice<string>[] TimeZones(string uiLanguage) => TimeZoneInfo.GetSystemTimeZones()
        .Select(zone => new Choice<string>(zone.Id, TimeZoneLabel(zone, uiLanguage))).ToArray();

    private static string TimeZoneLabel(TimeZoneInfo zone, string language)
    {
        string source = Engine.TimeOffsets.GetValueOrDefault(zone.Id)?.DisplayName ?? zone.DisplayName;
        string name = UiText.Get(language, source);
        if (language == "pl" && name == source && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pl")
            name = zone.DisplayName;
        int end = name.IndexOf(')');
        if (!name.StartsWith("(UTC", StringComparison.Ordinal) || end < 0)
            return name;

        TimeSpan offset = zone.BaseUtcOffset;
        string prefix = offset == TimeSpan.Zero ? "(UTC)" :
            $"(UTC{(offset < TimeSpan.Zero ? '-' : '+')}{offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture)})";
        return prefix + name[(end + 1)..];
    }

    public static void ApplyLanguage(SetupProfile profile, string language)
    {
        profile.SetLocale = true;
        profile.UiLocale = language;
        var locale = FormatLocale(language);
        profile.SystemLocale = profile.UserLocale = locale?.Id ?? language;
        profile.GeoId = locale?.GeoLocation?.Id ?? "";
        profile.Keyboard = DefaultKeyboard(language, locale);
        profile.TimeZone = language switch
        {
            "pl-PL" => "Central European Standard Time",
            "en-GB" => "GMT Standard Time",
            "cs-CZ" or "hu-HU" or "sk-SK" => "Central Europe Standard Time",
            "de-DE" or "it-IT" or "nl-NL" or "sv-SE" => "W. Europe Standard Time",
            "fr-FR" => "Romance Standard Time",
            "ja-JP" => "Tokyo Standard Time",
            "ko-KR" => "Korea Standard Time",
            _ => ""
        };
    }

    public static LocaleAndKeyboard? ResolveKeyboard(string input, string locale)
    {
        string identifier = input;
        if (!Engine.KeyboardIdentifiers.ContainsKey(identifier) && input.IndexOf(':') is int colon and >= 0)
            identifier = input[(colon + 1)..];
        if (!Engine.KeyboardIdentifiers.TryGetValue(identifier, out var keyboard))
            return null;
        string prefix = input.Split(':', 2)[0];
        var language = FormatLocale(locale);
        if (language is null || !language.LCID.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            language = Engine.UserLocales.Values.FirstOrDefault(value => value.LCID.Equals(prefix, StringComparison.OrdinalIgnoreCase) &&
                value.KeyboardLayout?.Id == identifier) ?? language;
        return language is null ? null : new LocaleAndKeyboard(language, keyboard);
    }

    private static string InputProfile(KeyboardIdentifier keyboard, string? lcid = null) =>
        keyboard.Type == InputType.IME ? keyboard.Id : $"{lcid ?? keyboard.Id[^4..]}:{keyboard.Id}";

    private static UserLocale? FormatLocale(string language) => Engine.UserLocales.GetValueOrDefault(language switch
    {
        "zh-CN" => "zh-Hans-CN", "zh-TW" => "zh-Hant-TW", _ => language
    });

    private static string DefaultKeyboard(string language, UserLocale? locale) => language switch
    {
        "zh-CN" => "0804:00000804",
        "zh-TW" => "0404:00000404",
        "sr-Latn-RS" => "241a:0000081a",
        _ => locale?.KeyboardLayout is KeyboardIdentifier keyboard && locale.LCID != "1000"
            ? InputProfile(keyboard, locale.LCID) : "0409:00000409"
    };

    private static string KeyboardName(KeyboardIdentifier keyboard, string uiLanguage) => uiLanguage == "pl"
        ? PolishKeyboards.GetValueOrDefault(keyboard.Id, UiText.Get(uiLanguage, keyboard.DisplayName))
        : uiLanguage != "en" ? UiText.Get(uiLanguage, keyboard.DisplayName) : keyboard.Id switch
        {
            "00000409" => "English (US)", "00000809" => "English (UK)", _ => keyboard.DisplayName
        };

    private static Choice<string>[] Sort(IEnumerable<Choice<string>> values, string uiLanguage, string[] featured) => values
        .OrderBy(value => Array.IndexOf(featured, value.Value) is int pos and >= 0 ? pos : featured.Length)
        .ThenBy(value => value.Label, StringComparer.Create(UiText.Culture(uiLanguage), true)).ToArray();

    private static CultureInfo? SpecificCulture(string name)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(name);
            return culture.IsNeutralCulture || name.Length == 0 ? null : culture;
        }
        catch (CultureNotFoundException) { return null; }
    }

    private static RegionInfo? TryRegion(string locale)
    {
        try { return new RegionInfo(locale); }
        catch (ArgumentException) { return null; }
    }
}
