using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XMLator;

internal sealed record ReleaseUpdate(Version Version, Uri Asset, long Size, string Sha256, string Notes)
{
    internal string NotesFor(string language)
    {
        var sections = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        string section = "";
        foreach (string line in Notes.ReplaceLineEndings("\n").Split('\n'))
        {
            string display = line.Trim();
            if (display.StartsWith("## ", StringComparison.Ordinal))
            {
                string title = display[3..];
                string? code = UiText.Languages.FirstOrDefault(candidate => candidate.Label == title)?.Value;
                if (code is not null)
                {
                    section = code;
                    continue;
                }
            }
            if (display.StartsWith("<!--", StringComparison.Ordinal))
                continue;
            if (!sections.TryGetValue(section, out var text))
                sections.Add(section, text = new StringBuilder());
            display = display.TrimStart('#').TrimStart();
            if (display.StartsWith("- ", StringComparison.Ordinal) || display.StartsWith("* ", StringComparison.Ordinal))
                display = "• " + display[2..];
            text.AppendLine(new string(display.Where(c => !char.IsControl(c)).ToArray()));
        }
        var selected = sections.GetValueOrDefault(language) ?? sections.GetValueOrDefault("en") ??
            sections.GetValueOrDefault("") ?? sections.Values.FirstOrDefault();
        string notes = selected?.ToString().Trim() ?? "";
        return notes.Length <= 1800 ? notes : notes[..1799] + "…";
    }
}

internal sealed class UpdateService(string executable, Version currentVersion, HttpClient http)
{
    internal const string Repository = "https://github.com/proszczok2002/XMLator";
    internal const string AssetName = "XMLator.exe";
    private static readonly Uri Latest = new("https://api.github.com/repos/proszczok2002/XMLator/releases/latest");
    private static readonly HttpClient GitHub = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private const int MaximumReleaseResponse = 256 * 1024;
    private const long MaximumExecutableSize = 200 * 1024 * 1024;

    internal string Executable { get; } = Path.GetFullPath(executable);

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "The empty assembly location identifies a bundled app eligible for replacement.")]
    internal static UpdateService? ForCurrentExecutable() =>
        typeof(UpdateService).Assembly.Location.Length == 0 && Environment.ProcessPath is string path
            ? new(path, typeof(UpdateService).Assembly.GetName().Version!, GitHub)
            : null;

    internal async Task<ReleaseUpdate?> CheckAsync(CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = NewRequest(Latest);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        var buffer = new byte[MaximumReleaseResponse + 1];
        int length = await source.ReadAtLeastAsync(buffer, buffer.Length, false, timeout.Token).ConfigureAwait(false);
        if (length > MaximumReleaseResponse)
            throw new InvalidDataException("GitHub returned an oversized release description.");
        return ParseRelease(buffer.AsSpan(0, length), currentVersion);
    }

    internal static ReleaseUpdate? ParseRelease(ReadOnlySpan<byte> json, Version installed)
    {
        var release = JsonSerializer.Deserialize<GitHubRelease>(json);
        string tag = release?.Tag ?? "";
        if (release is null || release.Draft is not false || release.Prerelease is not false || !tag.StartsWith('v') ||
            !Version.TryParse(tag[1..], out var candidate) || candidate.Build < 0 || candidate.Revision >= 0 ||
            tag != $"v{candidate.ToString(3)}")
            throw new InvalidDataException("GitHub returned invalid stable release metadata.");
        var version = new Version(candidate.Major, candidate.Minor, candidate.Build, 0);
        if (version <= installed)
            return null;

        var matches = release.Assets?.Where(item => item.Name == AssetName).ToArray() ?? [];
        if (matches is not [var asset] || asset.State != "uploaded" || asset.Size is <= 0 or > MaximumExecutableSize ||
            asset.Address != $"{Repository}/releases/download/{tag}/{AssetName}" ||
            asset.Digest is not { Length: 71 } digest || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            digest.AsSpan(7).ContainsAnyExcept("0123456789ABCDEFabcdef"))
            throw new InvalidDataException("GitHub returned invalid update asset metadata.");
        return new ReleaseUpdate(version, new Uri(asset.Address), asset.Size, digest[7..], release.Body ?? "");
    }

    internal async Task<string> DownloadAsync(ReleaseUpdate update, IProgress<int>? progress, CancellationToken cancellation)
    {
        string staged = Path.Combine(Path.GetDirectoryName(Executable)!, $".xmlator-update-{Guid.NewGuid():N}.exe");
        bool verified = false;
        try
        {
            using var response = await DownloadResponse(update.Asset, cancellation).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is long length && length != update.Size)
                throw new InvalidDataException("The update size differs from the GitHub release.");
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false))
            await using (var target = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                byte[] buffer = new byte[65536];
                long received = 0;
                int percent = 0;
                progress?.Report(percent);
                while (await source.ReadAsync(buffer, cancellation).ConfigureAwait(false) is int count && count > 0)
                {
                    received += count;
                    if (received > update.Size)
                        throw new InvalidDataException("The update download exceeded its declared size.");
                    await target.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
                    digest.AppendData(buffer, 0, count);
                    int reached = (int)(received * 100 / update.Size);
                    while (percent < Math.Min(reached, 99))
                    {
                        percent++;
                        progress?.Report(percent);
                    }
                }
                if (received != update.Size ||
                    !CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), Convert.FromHexString(update.Sha256)))
                    throw new InvalidDataException("The update SHA-256 or size differs from the GitHub release.");
            }
            var file = FileVersionInfo.GetVersionInfo(staged);
            if (file.ProductName != "XMLator" || VersionOf(file) != update.Version)
                throw new InvalidDataException("The downloaded file is not the expected XMLator version.");
            verified = true;
            progress?.Report(100);
            return staged;
        }
        finally
        {
            if (!verified)
                File.Delete(staged);
        }
    }

    private async Task<HttpResponseMessage> DownloadResponse(Uri asset, CancellationToken cancellation)
    {
        using var initial = NewRequest(asset);
        var response = await http.SendAsync(initial, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        try
        {
            if (response.StatusCode is HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect)
            {
                var next = response.Headers.Location;
                if (next is not { IsAbsoluteUri: true, Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 } ||
                    next.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                    throw new InvalidDataException("GitHub redirected the update to an unexpected host.");
                response.Dispose();
                using var redirected = NewRequest(next);
                response = await http.SendAsync(redirected, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            }
            if (response.StatusCode != HttpStatusCode.OK)
                throw new HttpRequestException("GitHub did not return the update file.", null, response.StatusCode);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    internal static Version VersionOf(FileVersionInfo file) =>
        new(file.FileMajorPart, file.FileMinorPart, file.FileBuildPart, file.FilePrivatePart);

    private static HttpRequestMessage NewRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("XMLator");
        return request;
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? Tag,
        [property: JsonPropertyName("draft")] bool? Draft,
        [property: JsonPropertyName("prerelease")] bool? Prerelease,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("assets")] GitHubAsset[]? Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("digest")] string? Digest,
        [property: JsonPropertyName("browser_download_url")] string? Address);
}
