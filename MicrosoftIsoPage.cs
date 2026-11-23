using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace XMLator;

internal static class MicrosoftIsoPage
{
    private const string DownloadOrigin = "https://www.microsoft.com";
    private const string DownloadUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";

    internal static async Task<IsoAddress> Resolve(IntPtr owner, IsoProduct product, string architecture,
        IsoLanguage language, CancellationToken cancellation) =>
        (await ReadOffer(owner, product.Version, product.Release, architecture, language.MicrosoftName, cancellation,
            TimeSpan.FromSeconds(45))).Address;

    internal static async Task<IsoProduct?> Discover(IntPtr owner, string version, string architecture, IsoLanguage language,
        CancellationToken cancel)
    {
        try
        {
            var offer = await ReadOffer(owner, version, null, architecture, language.MicrosoftName, cancel, TimeSpan.FromSeconds(18));
            if (await IsoDownload.IsAvailable(offer.Address.Uri, IsoSource.Microsoft, cancel))
                return new(version, offer.Release, [architecture]);
        }
        catch (Exception error) when (!cancel.IsCancellationRequested && error is SetupException or IOException or UnauthorizedAccessException) { }
        return null;
    }

    internal static string[] PublicArchitectures(string version) => version switch
    {
        "Windows 10" => ["amd64", "x86"],
        "Windows 11" => ["amd64", "arm64"],
        _ => []
    };

    private static async Task<(string Release, IsoAddress Address)> ReadOffer(IntPtr owner, string version, string? expectedRelease,
        string architecture, string language, CancellationToken cancellation, TimeSpan limit)
    {
        string page = (version, architecture) switch
        {
            ("Windows 10", "amd64" or "x86") => "windows10ISO",
            ("Windows 11", "amd64") => "windows11",
            ("Windows 11", "arm64") => "windows11arm64",
            _ => throw Unavailable()
        };
        var address = new Uri($"{DownloadOrigin}/en-us/software-download/{page}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(limit);
        var token = timeout.Token;
        var directory = Directory.CreateTempSubdirectory("XMLator-ISO-WebView-");
        CoreWebView2Controller? controller = null;
        bool browserStarted = false;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: directory.FullName);
            environment.BrowserProcessExited += (_, _) => RemoveProfile(directory);
            token.ThrowIfCancellationRequested();
            controller = await environment.CreateCoreWebView2ControllerAsync(owner);
            browserStarted = true;
            token.ThrowIfCancellationRequested();
            controller.IsVisible = false;
            var view = controller.CoreWebView2;
            view.Settings.IsWebMessageEnabled = false;
            view.Settings.AreHostObjectsAllowed = false;
            view.Settings.AreDevToolsEnabled = false;
            view.Settings.AreDefaultScriptDialogsEnabled = false;
            view.Settings.IsPasswordAutosaveEnabled = false;
            view.Settings.IsGeneralAutofillEnabled = false;
            view.Settings.UserAgent = DownloadUserAgent;
            view.NavigationStarting += (_, args) => args.Cancel = !IsMicrosoftPage(args.Uri);
            view.NewWindowRequested += (_, args) => args.Handled = true;
            view.DownloadStarting += (_, args) => args.Cancel = true;
            view.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            var loaded = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            view.NavigationCompleted += (_, args) => loaded.TrySetResult(args);
            view.Navigate(address.AbsoluteUri);
            if (!(await loaded.Task.WaitAsync(token)).IsSuccess || !IsMicrosoftPage(view.Source))
                throw Unavailable();
            var release = await Poll<string>(view, """
                (() => {
                    const select = document.getElementById("product-edition");
                    if (!select || !Array.from(select.options).some(option => /^[0-9]+$/.test(option.value))) return "";
                    return document.body.innerText.match(/(?:\bCurrent release:[^\n]+|\bWindows 10[^\n]+)\bVersion\s+(\d{2}H[12])\b/i)?.[1] ?? "";
                })()
                """, value => value.Length != 0, token);
            if (expectedRelease is not null && release != expectedRelease)
                throw new SetupException("Formularz Microsoftu nie oferuje wybranego wydania Windows. Wybierz inne wydanie lub lokalny obraz ISO.",
                    "The Microsoft form does not offer the selected Windows release. Choose another release or a local ISO.");
            await Poll<bool>(view, """
                (() => {
                    const select = document.getElementById("product-edition");
                    const confirm = document.getElementById("submit-product-edition");
                    const retail = select && Array.from(select.options).find(option => /^[0-9]+$/.test(option.value));
                    if (!retail || !confirm || confirm.disabled) return false;
                    select.value = retail.value;
                    select.dispatchEvent(new Event("change", { bubbles: true }));
                    confirm.click();
                    return true;
                })()
                """, value => value, token);
            string selectedLanguage = JsonSerializer.Serialize(language);
            string fallback = expectedRelease is null ? """ || find("English") || find("Polish")""" : "";
            int result = await Poll<int>(view, $$"""
                (() => {
                    const select = document.getElementById("product-languages");
                    const confirm = document.getElementById("submit-sku");
                    if (!select || !confirm || select.options.length < 2 || confirm.disabled) return 0;
                    const find = name => Array.from(select.options).find(option =>
                        option.value.startsWith("{") && JSON.parse(option.value).language === name);
                    const language = find({{selectedLanguage}}){{fallback}};
                    if (!language) return -1;
                    select.value = language.value;
                    select.dispatchEvent(new Event("change", { bubbles: true }));
                    confirm.click();
                    return 1;
                })()
                """, value => value != 0, token);
            if (result == -1)
                throw new SetupException("Microsoft nie udostępnia tego języka dla wybranego ISO. Wybierz inny język lub lokalny plik.",
                    "Microsoft does not offer this language for the selected ISO. Choose another language or a local file.");

            string architecturePattern = architecture switch
            {
                "x86" => "/_x(?:32|86)(?=[_.v])/i",
                "arm64" => "/_(?:arm64|a64)(?=[_.v])/i",
                _ => "/_x64(?=[_.v])/i"
            };
            string bits = architecture switch { "x86" => "32-bit", "arm64" => "Arm64", _ => "64-bit" };
            var download = await Poll<PageDownload?>(view, $$"""
                (() => {
                    const pattern = {{architecturePattern}};
                    const link = Array.from(document.links).find(link => {
                        const path = new URL(link.href).pathname;
                        return path.toLowerCase().endsWith(".iso") && pattern.test(path) &&
                            new RegExp("(?:^|[^a-z0-9])" + {{JsonSerializer.Serialize(release)}} + "(?:[^a-z0-9]|$)", "i").test(path);
                    });
                    if (!link)
                        return document.body?.innerText.includes("715-123130") ? { Rejected: true } : null;
                    const selected = document.getElementById("product-languages")?.value;
                    const language = (selected?.startsWith("{") ? JSON.parse(selected).language : {{selectedLanguage}})
                        .replace(/^Chinese \((.+)\)$/, "Chinese $1");
                    const label = (language + " " + {{JsonSerializer.Serialize(bits)}}).toLowerCase();
                    const row = Array.from(document.querySelectorAll("tr")).find(row =>
                        row.cells.length === 2 &&
                        row.cells[0].textContent.trim().replace(/\s+/g, " ").toLowerCase() === label);
                    const hash = row?.cells[1].textContent.trim();
                    return { Address: link.href, Sha256: /^[0-9a-f]{64}$/i.test(hash) ? hash : null };
                })()
                """, value => value is not null, token);
            if (download?.Rejected == true)
                throw new SetupException("Microsoft odmówił wydania linku do ISO (715-123130). Wybierz inne wydanie lub lokalny obraz ISO.",
                    "Microsoft refused to issue the ISO link (715-123130). Choose another release or a local ISO.");
            if (!Uri.TryCreate(download?.Address, UriKind.Absolute, out var image) || !IsoDownload.IsMicrosoftDownload(image))
                throw Unavailable();
            return (release, new IsoAddress(image, download.Sha256));
        }
        catch (WebView2RuntimeNotFoundException)
        {
            throw new SetupException("Pobranie tego obrazu wymaga środowiska Microsoft Edge WebView2. Możesz wybrać lokalny plik ISO.",
                "Downloading this image requires the Microsoft Edge WebView2 Runtime. You can select a local ISO file.");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw Unavailable();
        }
        catch (COMException) when (!cancellation.IsCancellationRequested)
        {
            throw Unavailable();
        }
        finally
        {
            controller?.Close();
            if (!browserStarted)
                RemoveProfile(directory);
        }
    }

    private static async Task<T> Poll<T>(CoreWebView2 view, string script, Func<T, bool> ready, CancellationToken cancel)
    {
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            if (!IsMicrosoftPage(view.Source))
                throw Unavailable();
            var text = await view.ExecuteScriptAsync(script).WaitAsync(cancel);
            if (text != "null")
            {
                var value = JsonSerializer.Deserialize<T>(text)!;
                if (ready(value))
                    return value;
            }
            await Task.Delay(400, cancel);
        }
    }

    private static bool IsMicrosoftPage(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
        uri.GetLeftPart(UriPartial.Authority) == DownloadOrigin &&
        uri.AbsolutePath.Contains("/software-download/", StringComparison.OrdinalIgnoreCase);

    private static SetupException Unavailable() => new(
        "Formularz Microsoftu nie zwrócił adresu ISO. Spróbuj ponownie, wybierz inne wydanie lub wskaż lokalny obraz ISO.",
        "The Microsoft form did not provide an ISO address. Retry, select another release or choose a local ISO.");

    private static void RemoveProfile(DirectoryInfo directory)
    {
        try
        {
            if (Directory.Exists(directory.FullName))
                Directory.Delete(directory.FullName, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record PageDownload(string? Address, string? Sha256, bool Rejected);
}
