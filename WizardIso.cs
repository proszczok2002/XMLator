using System.ComponentModel;

namespace XMLator;

internal sealed partial class Wizard
{
    private readonly Dictionary<string, DownloadedIso> downloadedIsos = new(StringComparer.OrdinalIgnoreCase);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<IntPtr, string, string, string, CancellationToken, Action<IsoProduct[]>, Task<IsoProduct[]>> FindMicrosoftIsos { private get; init; } =
        MicrosoftIsoCatalog.FindAvailable;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<Target, string, CancellationToken, Task<ProszczokIso[]>> FindSiteIsos { private get; init; } =
        (target, architecture, cancel) => ProszczokIsoCatalog.FindAvailable(target, architecture, cancel);

    private Action<IsoProduct[]> MicrosoftIsoOptions(Fields f, TextBox iso, Action useDownloadedIso)
    {
        string architecture = profile.Architecture;
        var edition = f.Select<IsoProduct?>(L("Wydan&ie ISO (Home / Pro / Education)", "ISO release (Home / Pro / &Education)"), null, []);
        var language = f.Select<IsoLanguage?>(L("Jęz&yk obrazu ISO", "ISO lan&guage"), null,
            [new(null, L("Wybierz język", "Select a language"))]);
        var start = f.Button(L("Pobierz i &użyj ISO…", "&Download and use ISO…"));
        start.Click += (_, _) => _ = Download();
        edition.SelectedIndexChanged += (_, _) => SelectLanguage();

        return products =>
        {
            var previous = Selected<IsoProduct>(edition);
            if (edition.Items.OfType<Choice<IsoProduct?>>().Select(item => item.Value!)
                .Select(ProductKey).SequenceEqual(products.Select(ProductKey)))
                return;
            Fields.UpdateChoices<IsoProduct?>(edition, products.FirstOrDefault(item => item.Release == previous?.Release),
                products.Select(item => new Choice<IsoProduct?>(item, item.Release)));
        };

        static string ProductKey(IsoProduct item) =>
            $"{item.Release}:{string.Join(',', item.Architectures)}:{string.Join(',', item.DirectArchitectures ?? [])}";

        void SelectLanguage()
        {
            if (Selected<IsoProduct>(edition) is not { } product)
                return;
            string currentLanguage = Selected<IsoLanguage>(language)?.Culture ?? (profile.SetLocale ? profile.UiLocale : UiText.CultureName(UiLanguage));
            var languages = MicrosoftIsoCatalog.Languages(product, architecture, UiLanguage);
            var initial = languages.FirstOrDefault(item => item.Culture.Equals(currentLanguage, StringComparison.OrdinalIgnoreCase)) ?? languages[0];
            ReplaceChoices<IsoLanguage>(language, languages.Select(item => new Choice<IsoLanguage?>(item, item.Name)), initial);
        }

        Task Download()
        {
            if (Selected<IsoProduct>(edition) is not { } product || Selected<IsoLanguage>(language) is not { } selected)
                return Task.CompletedTask;
            string name = $"{product.Version} {product.Release} {(architecture == "amd64" ? "x64" : architecture)} {selected.Culture}";
            return DownloadIso(start, iso, useDownloadedIso, IsoSource.Microsoft, name, async cancel =>
                MicrosoftIsoCatalog.FindDirect(product, architecture, selected) ??
                await MicrosoftIsoPage.Resolve(Handle, product, architecture, selected, cancel));
        }
    }

    private Action<ProszczokIso[]> ProszczokIsoOptions(Fields f, TextBox iso, Action useDownloadedIso)
    {
        var images = f.Select<ProszczokIso?>(L("Plik &ISO z proszczok.pl", "ISO fil&e from proszczok.pl"), null, []);
        var start = f.Button(L("Pobierz i &użyj ISO…", "&Download and use ISO…"));
        start.Enabled = false;
        images.SelectedIndexChanged += (_, _) => start.Enabled = Selected<ProszczokIso>(images) is not null;
        start.Click += (_, _) =>
        {
            if (Selected<ProszczokIso>(images) is { } image)
                _ = DownloadIso(start, iso, useDownloadedIso, IsoSource.Proszczok, Path.GetFileNameWithoutExtension(image.FileName),
                    _ => Task.FromResult(new IsoAddress(image.Uri)));
        };
        return matches =>
        {
            if (images.Items.OfType<Choice<ProszczokIso?>>().Select(item => item.Value).SequenceEqual(matches))
                return;
            Uri? previous = Selected<ProszczokIso>(images)?.Uri;
            Fields.UpdateChoices<ProszczokIso?>(images, matches.FirstOrDefault(item => item.Uri == previous),
                matches.Select(item => new Choice<ProszczokIso?>(item, item.FileName)));
        };
    }

    private async Task DownloadIso(Button start, TextBox iso, Action useDownloadedIso, IsoSource source, string name,
        Func<CancellationToken, Task<IsoAddress>> resolve)
    {
        if (working)
            return;
        string destination = IsoDownload.LocalPath(AppContext.BaseDirectory, name);
        using var cancellation = new CancellationTokenSource();
        using var stop = Fields.ActionButton(L("&Anuluj pobieranie", "&Cancel download"), () => cancellation.Cancel());
        navigation.Controls.Add(stop);
        ApplyColors(stop, UseDarkTheme());
        string downloading = source == IsoSource.Proszczok
            ? L("Pobieranie obrazu ISO z proszczok.pl…", "Downloading the ISO from proszczok.pl…")
            : L("Pobieranie obrazu ISO z Microsoftu…", "Downloading the ISO from Microsoft…");
        string progressName = L("Postęp pobierania ISO", "ISO download progress");
        SetBusy(true);
        stop.Select();
        ReportOperation(progressName, L("Pobieranie adresu obrazu ISO…", "Requesting the ISO address…"));
        bool saved = false;
        bool canceled = false;
        try
        {
            var address = await resolve(cancellation.Token);
            ReportOperation(progressName, downloading, 0);
            var progress = new Progress<int>(percent =>
            {
                if (working && !stop.IsDisposed && percent >= 0)
                    ReportOperation(progressName, downloading, percent);
            });
            await IsoDownload.Save(address.Uri, destination, progress, cancellation.Token, overwrite: false,
                expectedSha256: address.Sha256, source: source);
            ReportOperation(progressName, downloading, 100);
            downloadedIsos[destination] = DownloadedIso.Snapshot(destination);
            iso.Text = destination;
            saved = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { canceled = true; }
        catch (UnauthorizedAccessException)
        {
            Error(new SetupException("Brak uprawnień do zapisu ISO obok XMLator.exe.", "The ISO cannot be written next to XMLator.exe."));
        }
        catch (Exception e) { Error(e); }
        finally { SetBusy(false); }
        if (saved)
        {
            ShowMessage(this, destination, MessageBoxIcon.Information, title: L("Pobrano obraz ISO", "The ISO was downloaded"));
            useDownloadedIso();
        }
        else
        {
            if (canceled)
                ShowMessage(this, L("Anulowano pobieranie ISO.", "The ISO download was canceled."), MessageBoxIcon.Information,
                    title: L("Anulowano pobieranie", "Download canceled"));
            start.Select();
        }
    }

    private void DeleteDownloadedIso(string path)
    {
        if (!downloadedIsos.TryGetValue(path, out var downloaded) || !File.Exists(path))
            return;
        try
        {
            downloaded.Delete();
            downloadedIsos.Remove(path);
            usbIsoPath = "";
            ShowStep();
            ShowMessage(this, L("Usunięto pobrany plik ISO.", "The downloaded ISO was deleted."), MessageBoxIcon.Information,
                title: L("Usunięto ISO", "ISO deleted"));
        }
        catch (Exception error) { Error(error); }
    }

    private static T? Selected<T>(ComboBox combo) where T : class => (combo.SelectedItem as Choice<T?>)?.Value;

    private static void ReplaceChoices<T>(ComboBox combo, IEnumerable<Choice<T?>> choices, T selected) where T : class
    {
        var items = choices.ToArray();
        combo.BeginUpdate();
        try
        {
            combo.Items.Clear();
            combo.Items.AddRange(items.Cast<object>().ToArray());
            combo.SelectedIndex = Array.FindIndex(items, item => EqualityComparer<T>.Default.Equals(item.Value, selected));
        }
        finally { combo.EndUpdate(); }
    }
}
