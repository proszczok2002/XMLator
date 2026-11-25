using System.ComponentModel;
using System.Windows.Forms.Automation;
using System.Xml.Linq;

namespace XMLator;

internal sealed partial class Wizard
{
    private UsbDisk? usbTarget;
    private UsbDisk[]? usbDisks;
    private Task<UsbDisk[]>? usbScan;
    private string usbIsoPath = "";

    private void ResetUsbOptions()
    {
        usbTarget = null;
        usbIsoPath = "";
    }

    private void UsbOptions(Fields f)
    {
        var noDrive = new Choice<UsbDisk?>(null, L("Wybierz pendrive", "Select a USB drive"));
        var drive = Explain(f.Select(L("P&endrive", "&USB drive"), usbTarget, [noDrive, .. UsbChoices()], value => usbTarget = value),
            "Wybierz urządzenie według numeru dysku, modelu i pojemności. Przygotowanie usunie z niego dane po osobnym potwierdzeniu.",
            "Choose a drive by disk number, model and size. Creating the media will erase the drive after separate confirmation.");
        var refresh = f.Button(L("&Odśwież listę pendrive’ów", "&Refresh USB drives"));
        var status = f.Note(usbDisks is { Length: 0 } ? L("Podłącz pendrive i odśwież listę.", "Connect a USB drive and refresh the list.") : "");
        bool scanningDrives = false;
        var sourceField = new Fields();
        var localChoice = C(IsoSource.Local, "Wybierz plik ISO", "Choose an ISO file");
        var microsoftChoice = C(IsoSource.Microsoft, "Pobierz ISO z Microsoftu", "Download an ISO from Microsoft");
        var siteChoice = C(IsoSource.Proszczok, "Pobierz ISO z proszczok.pl", "Download an ISO from proszczok.pl");
        var source = sourceField.Select(L("Ź&ródło obrazu ISO", "&ISO source"), IsoSource.Local, [localChoice]);
        sourceField.Table.Visible = false;
        f.Section(sourceField);
        var local = new Fields();
        f.Section(local);
        var iso = local.Entry(L("Ścieżka do o&brazu ISO Windows", "Windows ISO pa&th"), usbIsoPath, value => usbIsoPath = value);
        var browse = local.Button(L("Przeglądaj pliki &ISO…", "Bro&wse for ISO…"));
        string invalidIso = L("Wskaż istniejący plik ISO Windows.", "Choose an existing Windows ISO file.");
        var isoError = local.Note(invalidIso);
        isoError.Visible = false;
        var microsoft = new Fields();
        microsoft.Table.Visible = false;
        f.Section(microsoft);
        var proszczok = new Fields();
        proszczok.Table.Visible = false;
        f.Section(proszczok);
        var isoRefresh = f.Button(L("Odśwież źró&dła ISO", "Refresh IS&O sources"));
        var isoStatus = f.Note("");
        isoStatus.Visible = false;
        string bootMode = UsbMedia.RequiredFirmware(profile) == Firmware.Gpt ? "UEFI / GPT (FAT32)" : "Legacy BIOS / MBR (NTFS)";
        f.Note(LF($"Tryb rozruchu USB — {bootMode}", $"USB boot mode — {bootMode}"));
        var create = Explain(f.Button(L("U&twórz bootowalny pendrive", "&Create a bootable USB drive")),
            "Zapisuje pliki instalatora, bieżące ustawienia jako Autounattend.xml i dodatki bezpośrednio na pendrivie.",
            "Writes Windows Setup, your current settings as Autounattend.xml, and extra files directly to the USB drive.");
        string? checkedIso = null;
        int isoCheck = 0;
        var showMicrosoft = MicrosoftIsoOptions(microsoft, iso, UseDownloadedIso);
        var showProszczok = ProszczokIsoOptions(proszczok, iso, UseDownloadedIso);
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = IsoFilter(), CheckFileExists = true };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                if (iso.Text == dialog.FileName)
                    _ = CheckIso();
                else
                    iso.Text = dialog.FileName;
            }
        };
        create.Click += async (_, _) =>
        {
            if (checkedIso is not string path)
                return;
            if (!File.Exists(path))
            {
                checkedIso = null;
                isoError.Visible = true;
                UpdateCreate();
                iso.Select();
                iso.AccessibilityObject.RaiseAutomationNotification(AutomationNotificationKind.Other,
                    AutomationNotificationProcessing.MostRecent, invalidIso);
                return;
            }
            await PrepareUsb(path);
        };
        IsoProduct[] microsoftOffers = [];
        ProszczokIso[] siteOffers = [];
        CancellationTokenSource? readingIso = null;
        var target = profile.Target;
        string architecture = profile.Architecture;
        bool useMicrosoft = MicrosoftIsoPage.PublicArchitectures(target.Name).Contains(architecture) &&
            (profile.ImageMode != ImageMode.Edition || MicrosoftIsoCatalog.SupportsEdition(profile.SelectedEdition));
        string culture = profile.SetLocale ? profile.UiLocale : UiText.CultureName(UiLanguage);
        var isoLists = new[] { source }.Concat(microsoft.Table.Controls.OfType<ComboBox>())
            .Concat(proszczok.Table.Controls.OfType<ComboBox>()).ToArray();
        foreach (var list in isoLists)
            list.DropDownClosed += (_, _) =>
            {
                if (IsHandleCreated && !source.IsDisposed)
                    BeginInvoke(ApplyIsoAvailability);
            };
        source.SelectedIndexChanged += (_, _) => SwitchIsoSource();
        source.Disposed += (_, _) => readingIso?.Cancel();
        isoRefresh.Click += (_, _) => _ = RefreshIsoSources(announce: true);
        refresh.Click += async (_, _) => await Refresh();
        drive.SelectedIndexChanged += (_, _) => UpdateCreate();
        iso.TextChanged += (_, _) => _ = CheckIso();

        _ = CheckIso();
        if (usbDisks is null)
            _ = Refresh();
        _ = RefreshIsoSources();

        async Task CheckIso()
        {
            int check = ++isoCheck;
            checkedIso = null;
            isoError.Visible = false;
            UpdateCreate();
            string path = iso.Text.Trim().Trim('"');
            if (path.Length == 0)
                return;
            await Task.Delay(180);
            if (iso.IsDisposed || check != isoCheck)
                return;
            bool exists = Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase) &&
                await Task.Run(() => File.Exists(path));
            if (iso.IsDisposed || check != isoCheck)
                return;
            checkedIso = exists ? Path.GetFullPath(path) : null;
            isoError.Visible = !exists;
            UpdateCreate();
        }

        void UseDownloadedIso()
        {
            // The downloader just saved the file at this absolute path.
            isoCheck++;
            checkedIso = iso.Text;
            isoError.Visible = false;
            source.SelectedIndex = 0;
            UpdateCreate();
            if (create.Enabled)
                create.Select();
            else
                drive.Select();
        }

        void UpdateCreate() => create.Enabled = !scanningDrives && usbTarget is not null &&
            CurrentSource() == IsoSource.Local && checkedIso is not null;

        IsoSource CurrentSource() => source.SelectedItem is Choice<IsoSource> choice ? choice.Value : IsoSource.Local;

        void SwitchIsoSource()
        {
            var selected = CurrentSource();
            local.Table.Visible = selected == IsoSource.Local;
            microsoft.Table.Visible = selected == IsoSource.Microsoft;
            proszczok.Table.Visible = selected == IsoSource.Proszczok;
            UpdateCreate();
        }

        void ApplyIsoAvailability()
        {
            if (source.IsDisposed || isoLists.Any(list => list.DroppedDown))
                return;
            var choices = new List<Choice<IsoSource>> { localChoice };
            if (microsoftOffers.Length != 0)
                choices.Add(microsoftChoice);
            if (siteOffers.Length != 0)
                choices.Add(siteChoice);
            var selection = CurrentSource();
            bool notifyMicrosoft = source.Focused && !working && microsoftOffers.Length > 0 &&
                !source.Items.OfType<Choice<IsoSource>>().Any(choice => choice.Value == IsoSource.Microsoft);
            bool focusLocal = (source.ContainsFocus && choices.Count == 1 ||
                (microsoft.Table.ContainsFocus || proszczok.Table.ContainsFocus) && !choices.Any(choice => choice.Value == selection));
            showMicrosoft(microsoftOffers);
            showProszczok(siteOffers);
            if (!choices.SequenceEqual(source.Items.OfType<Choice<IsoSource>>()))
                Fields.UpdateChoices(source, selection, choices);
            sourceField.Table.Visible = choices.Count > 1;
            SwitchIsoSource();
            if (focusLocal)
                iso.Select();
            if (notifyMicrosoft)
                source.AccessibilityObject.RaiseAutomationNotification(AutomationNotificationKind.Other,
                    AutomationNotificationProcessing.MostRecent,
                    L("ISO z Microsoftu jest dostępne.", "Microsoft ISO download is available."));
        }

        async Task RefreshIsoSources(bool announce = false)
        {
            if (readingIso is not null || working)
                return;
            using var cancellation = new CancellationTokenSource();
            readingIso = cancellation;
            isoStatus.Text = L("Sprawdzanie dostępności obrazów ISO…", "Checking ISO availability…");
            isoStatus.Visible = true;
            try
            {
                await Task.WhenAll(
                    useMicrosoft
                        ? ReadOffers(
                            () => FindMicrosoftIsos(Handle, target.Name, architecture, culture, cancellation.Token, offers =>
                            {
                                if (microsoftOffers.Length == 0)
                                    Publish(offers, found => microsoftOffers = found);
                            }), offers => microsoftOffers = offers, retryIfEmpty: !announce)
                        : Task.CompletedTask,
                    ReadOffers(() => FindSiteIsos(target, architecture, cancellation.Token), offers => siteOffers = offers));
            }
            finally
            {
                readingIso = null;
                if (!isoStatus.IsDisposed)
                    isoStatus.Visible = false;
            }
            if (announce && !isoRefresh.IsDisposed && isoRefresh.Focused && !working)
                isoRefresh.AccessibilityObject.RaiseAutomationNotification(AutomationNotificationKind.ActionCompleted,
                    AutomationNotificationProcessing.MostRecent, microsoftOffers.Length + siteOffers.Length == 0
                        ? L("Wybierz plik ISO z dysku.", "Choose a local ISO file.")
                        : L("Odświeżono źródła ISO.", "ISO sources refreshed."));

            async Task ReadOffers<T>(Func<Task<T[]>> read, Action<T[]> store, bool retryIfEmpty = false)
            {
                var offers = await TryRead();
                if (retryIfEmpty && offers.Length == 0 && !cancellation.IsCancellationRequested)
                {
                    try { await Task.Delay(750, cancellation.Token); }
                    catch (OperationCanceledException) { return; }
                    if (!cancellation.IsCancellationRequested)
                        offers = await TryRead();
                }
                Publish(offers, store);

                async Task<T[]> TryRead()
                {
                    try { return await read(); }
                    catch (Exception error) when (error is System.Net.Http.HttpRequestException or IOException or SetupException or
                        UnauthorizedAccessException or OperationCanceledException or System.Text.RegularExpressions.RegexMatchTimeoutException)
                    {
                        return [];
                    }
                }
            }

            void Publish<T>(T[] offers, Action<T[]> store)
            {
                if (source.IsDisposed || cancellation.IsCancellationRequested)
                    return;
                store(offers);
                ApplyIsoAvailability();
            }
        }

        IEnumerable<Choice<UsbDisk?>> UsbChoices() => (usbDisks ?? []).Select(disk => new Choice<UsbDisk?>(disk, disk.Label(UiLanguage)));

        async Task Refresh()
        {
            if (working)
                return;
            scanningDrives = true;
            refresh.Enabled = false;
            UpdateCreate();
            status.Text = L("Odczytywanie dysków USB…", "Reading USB drives…");
            Task<UsbDisk[]>? scan = null;
            try
            {
                scan = usbScan ??= UsbMedia.ListDisks(UiLanguage);
                var disks = await scan;
                if (drive.IsDisposed)
                    return;
                usbDisks = disks;
                var items = UsbChoices().ToArray();
                int index = Array.FindIndex(items, i => i.Value == usbTarget);
                drive.Items.Clear();
                drive.Items.Add(noDrive);
                drive.Items.AddRange(items);
                drive.SelectedIndex = index + 1;
                usbTarget = ((Choice<UsbDisk?>)drive.SelectedItem!).Value;
                status.Text = items.Length == 0 ? L("Podłącz pendrive i odśwież listę.", "Connect a USB drive and refresh the list.") : "";
            }
            catch (Exception e)
            {
                if (!drive.IsDisposed)
                    status.Text = L("Nie udało się odczytać dysków USB. ", "Could not read USB drives. ") + ErrorMessage(e);
            }
            finally
            {
                if (usbScan == scan)
                    usbScan = null;
                if (!drive.IsDisposed)
                {
                    scanningDrives = false;
                    refresh.Enabled = true;
                    UpdateCreate();
                }
            }
        }
    }

    private async Task PrepareUsb(string iso)
    {
        if (working || usbTarget is not UsbDisk disk)
            return;
        DirectoryInfo? staging = null;
        string? feedback = null;
        XDocument? completed = null;
        string? completedIso = null;
        Exception? failure = null;
        try
        {
            Validate();
            var document = BuildInstallationAnswer();
            if (document is null)
                return;
            Firmware mode = UsbMedia.RequiredFirmware(profile);
            string efi = profile.Architecture switch { "x86" => "ia32", "arm64" => "aa64", "ia64" => "ia64", _ => "x64" };
            SetBusy(true);
            ShowUsbProgress(new(UsbStage.Prepare));
            staging = Directory.CreateTempSubdirectory("XMLator-usb-");
            string overlay = await Task.Run(() => MediaPackage.StageUsb(staging.CreateSubdirectory("answer").FullName, document, profile));
            string media = Path.Combine(staging.FullName, "installation");
            string operation = L("Wypakowywanie plików z ISO", "Extracting files from the ISO");
            string progressName = L("Postęp odczytu ISO", "ISO extraction progress");
            string check = L("Sprawdzanie wersji, architektury i edycji w ISO", "Checking the Windows version, architecture and edition in the ISO");
            ReportOperation(check, check);
            var extraction = new Progress<int>(percent =>
            {
                if (working && (operationCaption == check || operationCaption == operation))
                    ReportOperation(progressName, operation, percent);
            });
            string label = await Task.Run(() => OpticalImage.Extract(iso, media, disk.Size, ((IProgress<int>)extraction).Report, profile));
            ShowUsbProgress(new(UsbStage.Elevate));
            bool done = await UsbWorker.Prepare(new UsbJob(disk, iso, media, label, overlay, mode, efi, polish, UiLanguage), () =>
            {
                bool approve = ConfirmUsb(disk, mode, iso);
                if (approve)
                    ShowUsbProgress(new(UsbStage.Formatting));
                return Task.FromResult(approve);
            }, report: ShowUsbProgress);
            if (done)
            {
                MarkConfigurationSaved();
                usbTarget = null;
                usbDisks = null;
                completed = document;
                completedIso = iso;
                feedback = profile.HasMediaFiles ?
                    L("Pendrive jest gotowy. Zapisano i sprawdzono Autounattend.xml oraz dodatkowe pliki.",
                        "The USB drive is ready. Autounattend.xml and the extra files were copied and verified.") :
                    L("Pendrive jest gotowy. Zapisano na nim Autounattend.xml.", "The USB drive is ready and contains Autounattend.xml.");
            }
            else
                feedback = L("Anulowano przed formatowaniem pendrive’a.", "USB preparation was canceled before formatting.");
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            feedback = L("Anulowano przygotowanie pendrive’a.", "USB preparation was canceled.");
        }
        catch (Exception e) { failure = e; }
        finally
        {
            SetBusy(false);
            if (staging is not null)
                CleanupUsbStaging(staging);
            if (completed is not null)
                ShowStep(focus: false);
        }

        if (completed is not null)
            await CompleteUsb(completed, feedback!, completedIso!);
        else if (failure is not null)
            ShowMessage(this, ErrorMessage(failure), MessageBoxIcon.Error,
                title: L("Błąd tworzenia pendrive’a", "USB creation failed"));
        else if (feedback is not null)
            ShowMessage(this, feedback, MessageBoxIcon.Information, title: L("Anulowano tworzenie pendrive’a", "USB creation canceled"));
        if (feedback is not null)
            FocusStep();
    }

    private void ShowUsbProgress(UsbProgress update)
    {
        string caption = update.Stage switch
        {
            UsbStage.Prepare => L("Przygotowanie pliku odpowiedzi i dodatków", "Preparing the answer file and extra files"),
            UsbStage.Elevate => L("Oczekiwanie na zgodę administratora", "Waiting for administrator approval"),
            UsbStage.Checking => L("Sprawdzanie obrazu ISO i pojemności pendrive’a", "Checking the ISO image and USB capacity"),
            UsbStage.ExtractingBoot => L("Odczytywanie pliku rozruchowego UEFI", "Extracting the UEFI boot file"),
            UsbStage.Splitting => L("Dzielenie obrazu WIM na części dla FAT32", "Splitting the WIM for FAT32"),
            UsbStage.Formatting => L("Formatowanie pendrive’a", "Formatting the USB drive"),
            UsbStage.Copy => L("Kopiowanie plików Windows", "Copying Windows files"),
            UsbStage.Extras => L("Zapisywanie Autounattend.xml i plików dodatkowych", "Writing Autounattend.xml and extra files"),
            UsbStage.Verify => L("Sprawdzanie kopii XML i dodatków", "Verifying the XML and extra files"),
            UsbStage.Boot => L("Przygotowanie rozruchu pendrive’a", "Preparing USB boot"),
            _ => L("Kończenie zapisu pendrive’a", "Finishing the USB drive")
        };
        ReportOperation(L("Postęp tworzenia pendrive’a", "USB creation progress"), caption, update.Percent);
    }

    private (TaskDialogPage Page, TaskDialogButton Save, TaskDialogButton Close) UsbResultPage(string feedback, bool canDeleteIso)
    {
        var save = new TaskDialogButton(L("&Zapisz kopię XML…", "&Save an XML copy…"));
        var close = new TaskDialogButton(L("Zam&knij", "&Close"));
        var page = new TaskDialogPage { Caption = L("Pendrive jest gotowy", "The USB drive is ready"),
            Text = feedback, Icon = TaskDialogIcon.Information, AllowCancel = true, DefaultButton = close,
            Verification = canDeleteIso ? new TaskDialogVerificationCheckBox
            {
                Text = L("Usuń pobrany obraz ISO", "Delete the downloaded ISO")
            } : null,
            Buttons = { save, close } };
        return (page, save, close);
    }

    private async Task CompleteUsb(XDocument document, string feedback, string iso)
    {
        bool canDeleteIso = downloadedIsos.ContainsKey(iso) && File.Exists(iso);
        var (page, save, close) = UsbResultPage(feedback, canDeleteIso);
        var chosen = TaskDialog.ShowDialog(this, page);
        if (chosen == save)
            await SaveUsbAnswer(document);
        if ((chosen == save || chosen == close) && page.Verification?.Checked == true)
            DeleteDownloadedIso(iso);
    }

    private async Task SaveUsbAnswer(XDocument document)
    {
        using var save = AnswerSaveDialog();
        if (save.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            SetBusy(true, L("Zapisywanie kopii pliku odpowiedzi…", "Saving the answer file copy…"));
            await Task.Run(() => MediaPackage.Export(save.FileName, null, document, profile: null));
            lastOutput = Path.GetDirectoryName(save.FileName);
            ShowStep(focus: false);
            ShowMessage(this, save.FileName, MessageBoxIcon.Information, title: L("Zapisano kopię XML", "Saved the XML copy"));
        }
        catch (Exception error) { Error(error); }
        finally
        {
            SetBusy(false);
            FocusStep();
        }
    }

    private bool ConfirmUsb(UsbDisk disk, Firmware mode, string iso)
    {
        string title = L("Potwierdzenie formatowania pendrive’a", "Confirm USB formatting");
        HideOperationProgress();
        SetHeading(title);
        string description = $"{disk.Label(UiLanguage)}\r\n{(mode == Firmware.Gpt ? "UEFI / GPT" : "Legacy BIOS / MBR")}\r\n{Path.GetFileName(iso)}";
        string warning = L("Sformatować wybrany pendrive i usunąć z niego wszystkie pliki?",
            "Format the selected USB drive and erase all its files?");
        UseWaitCursor = false;
        try { return ShowMessage(this, $"{warning}\r\n\r\n{description}", MessageBoxIcon.Warning, confirm: true, title) == DialogResult.Yes; }
        finally { UseWaitCursor = true; }
    }

    private string IsoFilter() => L("Obraz ISO (*.iso)|*.iso", "ISO image (*.iso)|*.iso");
}
