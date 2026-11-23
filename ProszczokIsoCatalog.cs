using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace XMLator;

internal sealed record ProszczokIso(string FileName, Uri Uri, Target Target);

internal static class ProszczokIsoCatalog
{
    private static readonly Uri DirectoryUrl = new("https://proszczok.pl/windows/");
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 1024 * 1024 };
    private static readonly Regex Links = new(
        """<a\b[^<>]*?\s+href\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'<>]+))""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex NameSeparators = new(@"[\s_-]+", RegexOptions.CultureInvariant);
    private static readonly Regex LanguagePrefix = new(
        @"\A[a-z]{2,3}(?:[-_][a-z]{4})?(?:[-_](?:[a-z]{2}|[0-9]{3}))?[\s_-]+(?=(?:Windows|Microsoft)[\s_-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex ArchitectureTags = new(
        @"(?<![\p{L}\p{N}])(?:x86[-_]64|x64|amd64|64-bit|x86|x32|32-bit|arm64|aarch64|ia64)(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly (string Name, Target Target)[] LongestNamesFirst = Target.All
        .Select(target => (Name: NormalizeName(target.Name), Target: target))
        .OrderByDescending(entry => entry.Name.Length).ToArray();

    internal static async Task<ProszczokIso[]> Load(CancellationToken cancel, HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, DirectoryUrl);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await (client ?? Client).SendAsync(request, HttpCompletionOption.ResponseContentRead, cancel).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "text/html")
            throw SetupException.Format($"Nie udało się odczytać katalogu proszczok.pl. Odpowiedź HTTP {(int)response.StatusCode}.",
                $"Could not read the proszczok.pl catalog. HTTP {(int)response.StatusCode}.");
        return Parse(await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
    }

    internal static async Task<ProszczokIso[]> FindAvailable(Target target, string architecture, CancellationToken cancel, HttpClient? client = null)
    {
        var matches = (await Load(cancel, client).ConfigureAwait(false))
            .Where(image => image.Target == target && SupportsArchitecture(image, architecture)).ToArray();
        var available = new bool[matches.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, matches.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancel }, async (index, token) =>
                available[index] = await IsoDownload.IsAvailable(matches[index].Uri, IsoSource.Proszczok, token, client))
            .ConfigureAwait(false);
        return matches.Where((_, index) => available[index]).ToArray();
    }

    internal static bool SupportsArchitecture(ProszczokIso image, string architecture)
    {
        var tags = ArchitectureTags.Matches(image.FileName);
        if (tags.Count == 0)
            return architecture == image.Target.Architectures[0];
        return tags.Any(tag => (tag.Value.ToLowerInvariant() switch
        {
            "x86" or "x32" or "32-bit" => "x86",
            "arm64" or "aarch64" => "arm64",
            "ia64" => "ia64",
            _ => "amd64"
        }) == architecture);
    }

    internal static ProszczokIso[] Parse(string html)
    {
        var images = new List<ProszczokIso>();
        var seen = new HashSet<Uri>();
        foreach (Match link in Links.Matches(html))
        {
            string href = WebUtility.HtmlDecode(link.Groups.Values.Skip(1).First(group => group.Success).Value);
            if (!Uri.TryCreate(DirectoryUrl, href, out var uri) || !IsDownload(uri) || !seen.Add(uri))
                continue;

            string fileName = Uri.UnescapeDataString(uri.Segments[^1]);
            string stem = Path.GetFileNameWithoutExtension(fileName);
            string title = NormalizeName(LanguagePrefix.Replace(stem, "", count: 1));
            var target = LongestNamesFirst.FirstOrDefault(entry => MatchesTarget(title, entry.Name)).Target;
            if (target is not null)
                images.Add(new(fileName, uri, target));
        }
        return images.ToArray();
    }

    private static string NormalizeName(string name) =>
        NameSeparators.Replace(name.Replace(",", "", StringComparison.Ordinal), " ");

    private static bool MatchesTarget(string title, string target) =>
        title.Equals(target, StringComparison.OrdinalIgnoreCase)
        || title.StartsWith(target + " ", StringComparison.OrdinalIgnoreCase);

    internal static bool IsDownload(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.IdnHost != DirectoryUrl.IdnHost || uri.Fragment.Length != 0 ||
            !uri.AbsolutePath.StartsWith(DirectoryUrl.AbsolutePath, StringComparison.Ordinal))
            return false;

        var parts = uri.Segments.Skip(2).Select(segment => Uri.UnescapeDataString(segment.TrimEnd('/'))).ToArray();
        return parts.Length > 0 && !uri.AbsolutePath.EndsWith('/') &&
            parts.All(part => part.Length > 0 && part is not ("." or "..") && part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0) &&
            Path.GetExtension(parts[^1]).Equals(".iso", StringComparison.OrdinalIgnoreCase);
    }
}
