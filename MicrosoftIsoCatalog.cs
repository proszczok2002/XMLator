using System.Globalization;

namespace XMLator;

internal sealed record IsoProduct(string Version, string Release, string[] Architectures, string[]? DirectArchitectures = null);
internal sealed record IsoLanguage(string Name, string Culture, string MicrosoftName);
internal sealed record IsoAddress(Uri Uri, string? Sha256 = null);

internal static class MicrosoftIsoCatalog
{
    // Windows 11 25H2 is a separate, explicitly selected Microsoft image.
    private const string Windows11ReleaseFiles = "https://software-static.download.prss.microsoft.com/dbazure/888969d5-f34g-4e03-ac9d-1f9786c66749/";

    internal static IReadOnlyList<IsoProduct> Products { get; } =
    [
        new("Windows 11", "25H2", ["amd64", "arm64"], ["amd64", "arm64"]),
        new("Windows 10", "22H2", ["amd64", "x86"])
    ];

    internal static bool SupportsEdition(MediaEdition? edition) => edition is null ||
        edition.Id is "Core" or "CoreN" or "CoreSingleLanguage" or "Professional" or "ProfessionalN" or
            "ProfessionalWorkstation" or "ProfessionalEducation" or "Education" or "EducationN";

    private static readonly (string Culture, string MicrosoftName)[] RetailLanguages =
    [
        ("pl-PL", "Polish"), ("en-US", "English"), ("en-GB", "English International"),
        ("ar-SA", "Arabic"), ("bg-BG", "Bulgarian"), ("cs-CZ", "Czech"), ("da-DK", "Danish"), ("de-DE", "German"),
        ("el-GR", "Greek"), ("es-ES", "Spanish"), ("es-MX", "Spanish (Mexico)"), ("et-EE", "Estonian"), ("fi-FI", "Finnish"),
        ("fr-FR", "French"), ("fr-CA", "French Canadian"), ("he-IL", "Hebrew"), ("hr-HR", "Croatian"), ("hu-HU", "Hungarian"),
        ("it-IT", "Italian"), ("ja-JP", "Japanese"), ("ko-KR", "Korean"), ("lt-LT", "Lithuanian"), ("lv-LV", "Latvian"),
        ("nb-NO", "Norwegian"), ("nl-NL", "Dutch"), ("pt-BR", "Brazilian Portuguese"), ("pt-PT", "Portuguese"),
        ("ro-RO", "Romanian"), ("ru-RU", "Russian"), ("sk-SK", "Slovak"), ("sl-SI", "Slovenian"),
        ("sr-Latn-RS", "Serbian Latin"), ("sv-SE", "Swedish"), ("th-TH", "Thai"), ("tr-TR", "Turkish"), ("uk-UA", "Ukrainian"),
        ("zh-CN", "Chinese (Simplified)"), ("zh-TW", "Chinese (Traditional)")
    ];

    internal static IsoLanguage[] Languages(IsoProduct product, string architecture, bool polish)
        => Languages(product, architecture, polish ? "pl" : "en");

    internal static IsoLanguage[] Languages(IsoProduct product, string architecture, string uiLanguage)
    {
        var names = LocaleCatalog.Languages(uiLanguage).ToDictionary(item => item.Value, item => item.Label, StringComparer.OrdinalIgnoreCase);
        // The public Microsoft CDN has no Dutch Windows 11 25H2 ARM64 image at this release address.
        return RetailLanguages.Where(item => product.Release != "25H2" || architecture != "arm64" || item.Culture != "nl-NL")
            .Select(item => new IsoLanguage(names.GetValueOrDefault(item.Culture, CultureInfo.GetCultureInfo(item.Culture).EnglishName),
                item.Culture, item.MicrosoftName)).ToArray();
    }

    internal static IsoAddress? FindDirect(IsoProduct product, string architecture, IsoLanguage language)
    {
        if (product.Release != "25H2" || product.DirectArchitectures?.Contains(architecture) != true)
            return null;
        string arch = architecture == "arm64" ? "A64FRE" : "x64FRE";
        return new IsoAddress(new Uri(Windows11ReleaseFiles +
            $"26200.6584.250915-1905.25h2_ge_release_svc_refresh_CLIENT_CONSUMER_{arch}_{language.Culture.ToLowerInvariant()}.iso"));
    }

    internal static async Task<IsoProduct[]> FindAvailable(IntPtr owner, string version, string architecture,
        string culture, CancellationToken cancel, Action<IsoProduct[]>? report = null)
    {
        if (!MicrosoftIsoPage.PublicArchitectures(version).Contains(architecture))
            return [];
        var published = Products.Where(product => product.Version == version)
            .Select(product => CheckDirect(product, architecture, culture, cancel));
        var language = Languages(new(version, "", [architecture]), architecture, false)
            .FirstOrDefault(item => item.Culture == culture) ?? new IsoLanguage("English", "en-US", "English");
        var pending = published.Append(MicrosoftIsoPage.Discover(owner, version, architecture, language, cancel)).ToList();
        var offers = new List<IsoProduct>();
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);
            if (await completed is { } offer)
            {
                offers.Add(offer);
                if (pending.Count > 0)
                    report?.Invoke(MergeOffers());
            }
        }
        return MergeOffers();

        IsoProduct[] MergeOffers() => offers.GroupBy(offer => offer.Release)
            .OrderByDescending(group => group.Key, StringComparer.Ordinal)
            .Select(group => new IsoProduct(version, group.Key, group.SelectMany(offer => offer.Architectures).Distinct().ToArray(),
                group.SelectMany(offer => offer.DirectArchitectures ?? []).Distinct().ToArray()))
            .ToArray();
    }

    private static async Task<IsoProduct?> CheckDirect(IsoProduct product, string architecture, string culture, CancellationToken cancel)
    {
        var languages = Languages(product, architecture, false);
        var preferred = languages.FirstOrDefault(item => item.Culture == culture) ?? languages.First(item => item.Culture == "en-US");
        var address = FindDirect(product, architecture, preferred);
        if (address is null || !await IsoDownload.IsAvailable(address.Uri, IsoSource.Microsoft, cancel))
            return null;
        return product with { Architectures = [architecture], DirectArchitectures = [architecture] };
    }
}
