using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace XMLator;

internal enum IsoSource { Local, Microsoft, Proszczok }

internal sealed record DownloadedIso(string Path, long Length, DateTime Created, DateTime Modified)
{
    internal static DownloadedIso Snapshot(string path)
    {
        var file = new FileInfo(path);
        return new(file.FullName, file.Length, file.CreationTimeUtc, file.LastWriteTimeUtc);
    }

    internal void Delete()
    {
        var current = new FileInfo(Path);
        if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0 || current.Length != Length ||
            current.CreationTimeUtc != Created || current.LastWriteTimeUtc != Modified)
            throw new SetupException("Pobrany obraz został zmieniony lub przeniesiony. Pozostawiono plik.",
                "The downloaded image was changed or moved. The file was left in place.");
        File.Delete(Path);
    }
}

internal static class IsoDownload
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };

    internal static bool IsMicrosoftDownload(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.IdnHost is "www.microsoft.com" or "go.microsoft.com" or "aka.ms" or "download.microsoft.com" or
        "software-download.microsoft.com" or "software.download.prss.microsoft.com" or "software-static.download.prss.microsoft.com";

    internal static string LocalPath(string directory, string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string stem = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim(' ', '.');
        if (stem.Length == 0)
            stem = "Windows";
        string path = Path.Combine(directory, stem + ".iso");
        for (int suffix = 2; File.Exists(path) || Directory.Exists(path); suffix++)
            path = Path.Combine(directory, $"{stem} ({suffix}).iso");
        return path;
    }

    internal static async Task Save(Uri address, string destination, IProgress<int> progress,
        CancellationToken cancel, HttpClient? client = null, bool overwrite = true, string? expectedSha256 = null,
        IsoSource source = IsoSource.Microsoft)
    {
        var http = client ?? Client;
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using var response = await GetFile(http, address, source, cancel).ConfigureAwait(false);
            long? size = response.Content.Headers.ContentLength;
            await using var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            byte[] buffer = new byte[128 * 1024];
            int read = await stream.ReadAtLeastAsync(buffer.AsMemory(0, 64 * 1024), 64 * 1024, false, cancel).ConfigureAwait(false);
            if (!HasIsoSignature(buffer.AsSpan(0, read)))
                throw new SetupException("Serwer nie zwrócił obrazu ISO. Ponów pobieranie lub wskaż zapisany obraz.",
                    "The server did not return an ISO image. Retry the download or choose a local ISO.");
            long received = 0;
            int reported = -1;
            using var hash = expectedSha256 is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true))
            {
                while (read > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                    hash?.AppendData(buffer, 0, read);
                    received += read;
                    int percent = size is > 0 ? Math.Min(99, (int)(received * 100.0 / size.Value)) : -1;
                    if (percent != reported)
                    {
                        progress.Report(percent);
                        reported = percent;
                    }
                    read = await stream.ReadAsync(buffer, cancel).ConfigureAwait(false);
                }
            }
            if (size is > 0 && size != received)
                throw new SetupException("Pobieranie ISO przerwano przed zapisaniem całości.", "The ISO download ended before the whole file arrived.");
            if (hash is not null && !Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new SetupException("Suma SHA-256 pobranego obrazu nie zgadza się z sumą Microsoftu. Wybierz ponowne pobranie lub lokalny plik ISO.",
                    "The ISO's SHA-256 did not match Microsoft's checksum. Download again or choose a local ISO.");
            cancel.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite);
        }
        finally { File.Delete(temporary); }
    }

    internal static async Task<bool> IsAvailable(Uri address, IsoSource source, CancellationToken cancel, HttpClient? client = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await GetFile(client ?? Client, address, source, timeout.Token, probe: true).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var header = new byte[64 * 1024];
            int read = await stream.ReadAtLeastAsync(header, header.Length, false, timeout.Token).ConfigureAwait(false);
            return HasIsoSignature(header.AsSpan(0, read));
        }
        catch (Exception error) when (!cancel.IsCancellationRequested &&
            error is HttpRequestException or IOException or SetupException or OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<HttpResponseMessage> GetFile(HttpClient http, Uri address, IsoSource source,
        CancellationToken cancel, bool probe = false)
    {
        string provider = source == IsoSource.Proszczok ? "proszczok.pl" : "Microsoft";
        for (int redirect = 0; redirect < 7; redirect++)
        {
            bool allowed = source switch
            {
                IsoSource.Microsoft => IsMicrosoftDownload(address),
                IsoSource.Proszczok => ProszczokIsoCatalog.IsDownload(address),
                _ => false
            };
            if (!allowed)
                break;
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            if (probe)
                request.Headers.Range = new RangeHeaderValue(0, 65535);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is Uri location)
            {
                response.Dispose();
                address = new Uri(address, location);
                continue;
            }
            if ((response.StatusCode == HttpStatusCode.OK || probe && response.StatusCode == HttpStatusCode.PartialContent) &&
                response.Content.Headers.ContentType?.MediaType != "text/html")
                return response;
            var status = response.StatusCode;
            response.Dispose();
            throw SetupException.Format($"Serwer {provider} nie udostępnił pliku ISO. Odpowiedź HTTP {(int)status}.",
                $"The {provider} server did not provide the ISO file. HTTP {(int)status}.");
        }
        throw SetupException.Format($"Serwer {provider} nie zwrócił obsługiwanego adresu obrazu ISO.",
            $"The {provider} server did not return a supported ISO address.");
    }

    internal static bool HasIsoSignature(ReadOnlySpan<byte> data)
    {
        const int sector = 2048;
        for (int offset = 16 * sector + 1; offset + 5 <= data.Length; offset += sector)
        {
            var signature = data.Slice(offset, 5);
            if (signature.SequenceEqual("CD001"u8) || signature.SequenceEqual("NSR02"u8) || signature.SequenceEqual("NSR03"u8))
                return true;
        }
        return false;
    }
}
