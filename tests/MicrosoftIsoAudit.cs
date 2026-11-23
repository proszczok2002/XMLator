using System.Net.Http;
using System.Net.Http.Headers;
using XMLator;

internal static class MicrosoftIsoAudit
{
    internal static void Discover()
    {
        using var owner = new Form { ShowInTaskbar = false, Opacity = 0 };
        Exception? failure = null;
        owner.Shown += async (_, _) =>
        {
            try
            {
                var files = await ProszczokIsoCatalog.Load(CancellationToken.None);
                var siteChecks = files.Select(async file =>
                    (File: file, Available: await IsoDownload.IsAvailable(file.Uri, IsoSource.Proszczok, CancellationToken.None))).ToArray();
                foreach (var target in Target.All)
                    foreach (string arch in target.Architectures)
                    {
                        var microsoft = await MicrosoftIsoCatalog.FindAvailable(owner.Handle, target.Name, arch, "pl-PL", CancellationToken.None);
                        var site = (await Task.WhenAll(siteChecks)).Where(check => check.Available && check.File.Target == target &&
                            ProszczokIsoCatalog.SupportsArchitecture(check.File, arch));
                        Console.WriteLine($"{target.Name} / {arch}: local; Microsoft [{string.Join(", ", microsoft.Select(product =>
                            product.Release))}]; proszczok.pl [{string.Join(", ", site.Select(check => check.File.FileName))}]");
                    }
            }
            catch (Exception error) { failure = error; }
            finally { owner.Close(); }
        };
        Application.Run(owner);
        if (failure is not null)
            throw new InvalidOperationException("The live source scan failed.", failure);
    }

    internal static void Resolve(string version, string release, string architecture, string culture)
    {
        var product = MicrosoftIsoCatalog.Products.SingleOrDefault(item => item.Version == version && item.Release == release)
            ?? new IsoProduct(version, release, [architecture]);
        var language = MicrosoftIsoCatalog.Languages(product, architecture, false).Single(item => item.Culture == culture);
        using var owner = new Form { ShowInTaskbar = false, Opacity = 0 };
        Exception? failure = null;
        owner.Shown += async (_, _) =>
        {
            try
            {
                var link = MicrosoftIsoCatalog.FindDirect(product, architecture, language)
                    ?? await MicrosoftIsoPage.Resolve(owner.Handle, product, architecture, language, CancellationToken.None);
                using var client = new HttpClient();
                await CheckHeader(client, link.Uri);
                Console.WriteLine($"{version} {release}, {architecture}, {culture}: {link.Uri.Host}{link.Uri.AbsolutePath}, valid ISO header, SHA-256 {link.Sha256 ?? "unpublished with link"}.");
            }
            catch (Exception error) { failure = error; }
            finally { owner.Close(); }
        };
        Application.Run(owner);
        if (failure is not null)
            throw new InvalidOperationException("Could not resolve a Microsoft ISO download.", failure);
    }

    internal static async Task Run()
    {
        var product = MicrosoftIsoCatalog.Products.Single(item => item.Release == "25H2");
        using var client = new HttpClient();
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Parallel.ForEachAsync(product.Architectures.SelectMany(architecture =>
            MicrosoftIsoCatalog.Languages(product, architecture, true).Select(language => (architecture, language))),
            new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (selection, _) =>
        {
            var uri = MicrosoftIsoCatalog.FindDirect(product, selection.architecture, selection.language)!.Uri;
            string description = $"{product.Version} {product.Release}, {selection.architecture}, {selection.language.Culture}";
            try
            {
                await CheckHeader(client, uri);
                Console.WriteLine($"{description}: valid Microsoft ISO header.");
            }
            catch (Exception error) { failures.Add($"{description}: {error.Message}"); }
        });
        if (!failures.IsEmpty)
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
    }

    private static async Task CheckHeader(HttpClient client, Uri uri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(0, 65535);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
            throw new InvalidDataException($"HTTP {(int)response.StatusCode} for {uri.AbsolutePath}.");
        using var source = await response.Content.ReadAsStreamAsync();
        var data = new byte[65536];
        await source.ReadExactlyAsync(data);
        if (!Enumerable.Range(16, 16).Any(sector => data.AsSpan(sector * 2048 + 1, 5).SequenceEqual("CD001"u8) ||
            data.AsSpan(sector * 2048 + 1, 5).SequenceEqual("NSR02"u8) || data.AsSpan(sector * 2048 + 1, 5).SequenceEqual("NSR03"u8)))
            throw new InvalidDataException("Invalid ISO signature.");
    }
}
