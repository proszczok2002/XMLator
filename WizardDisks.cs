namespace XMLator;

internal sealed partial class Wizard
{
    private string NoCommonBootMode() => L("Wybrane architektury wymagają różnych trybów rozruchu. Wybierz dysk i partycję w instalatorze.",
        "The selected architectures require different boot modes. Choose the disk and partition in Setup.");

    private string ManualDiskSelectionNotice() => profile.InstallationMethod == "Generated" ? ManualDiskPartNote()
        : L("Wybór partycji zmieniono na ręczny w instalatorze.", "Partition selection changed to manual in Setup.");

    private string LegacyWindows11Note() => L(
        "Windows 11 w trybie Legacy to instalacja poza wymaganiami Microsoftu. Działanie zależy od obrazu i sprzętu.",
        "Microsoft requires UEFI for Windows 11. Installation in Legacy BIOS mode depends on the installation image and hardware.");

    private string ManualDiskPartNote() => L(
        "W trybie DISM przygotujesz dysk ręcznie w konsoli DiskPart.",
        "In DISM mode, prepare the disk manually in the DiskPart console.");

    private string DismInstallationDescription() => L(
        "Skrypt ze strony schneegans.de nakłada obraz przez DISM i tworzy pliki rozruchowe zgodnie z wybranym układem i trybem rozruchu.",
        "The script from schneegans.de applies the image with DISM and creates boot files for the selected layout and boot mode.");

    private Choice<string>[] InstallationMethods()
    {
        var microsoft = C("Normal", "Microsoft Windows Setup (zalecany)", "Microsoft Windows Setup (recommended)",
            "Standardowy instalator odczyta XML, przygotuje wybrany układ i wykona kolejne etapy. W razie problemu może poprosić o odpowiedź.",
            "The standard installer reads the XML, prepares the selected layout and runs the installation stages. It can ask for input if there is a problem.");
        return profile.Target.ModernClient
            ? [microsoft, new("Generated", L("Skrypt DISM ze strony schneegans.de", "DISM script from schneegans.de"),
                DismInstallationDescription()),
                C("Custom", "Mój skrypt CMD w Windows PE", "My CMD script in Windows PE",
                "Twój skrypt CMD odpowiada za wybór dysku, partycje i zastosowanie obrazu. To opcja dla własnych procedur wdrażania.",
                "Your CMD script selects the disk, partitions it and applies the image. Use this for your own deployment process.")]
            : [microsoft];
    }

    private void StandardSetupOptions()
    {
        var fields = new Fields();
        var options = new[]
        {
            C(SetupPrompt.OnError, "Tylko w razie błędu (domyślnie)", "On error only (default)"),
            C(SetupPrompt.Always, "Zawsze", "Always"), C(SetupPrompt.Never, "Pomiń ekran instalatora", "Skip the Setup screen")
        };
        SetupPrompt disk = profile.DiskPrompt, image = profile.EffectiveImagePrompt;
        bool configurationSet = profile.Has("UseConfigurationSet");
        if (profile.WipesDisk)
            Explain(fields.Select(L("Kiedy pokazać ekran partycjonowania?", "When should Setup show its partitioning screen?"), disk, options, value => disk = value),
                "Windows Setup przetwarza ustawienia formatowania i tworzenia partycji przed pokazaniem ekranu.",
                "Windows Setup processes formatting and partition creation settings before showing the screen.");
        if (profile.DiskMode != DiskMode.Interactive || profile.ImageMode != ImageMode.Interactive)
            Explain(fields.Select(L("Kiedy pokazać ekran wyboru obrazu lub celu instalacji?", "When should Setup show image or destination selection?"), image,
                    profile.DiskMode == DiskMode.Interactive ? options[..2] : options, value => image = value),
                    "Pominięcie ekranu może nadal pozostawić komunikaty o błędach krytycznych.",
                    "Critical errors can still appear when the screen is skipped.");
        Explain(fields.Check(L("Użyj przygotowanego zestawu konfiguracji Windows SIM", "Use a prepared Windows SIM configuration set"), configurationSet, value => configurationSet = value),
            "Zaznacz, kiedy dysponujesz zestawem konfiguracji Windows SIM z dołączonymi plikami. Instalator poszuka takiego zestawu.",
            "Enable this if you have a Windows SIM configuration set with the referenced files. Setup will look for that set.");
        if (Ask(L("Zaawansowane ustawienia Windows Setup", "Advanced Windows Setup settings"), fields))
        {
            profile.DiskPrompt = disk;
            profile.ImagePrompt = image;
            profile.Advanced["UseConfigurationSet"] = configurationSet ? "1" : "0";
        }
    }

    private List<string> DiskSummary()
    {
        string method = profile.InstallationMethod;
        var lines = new List<string>();
        if (profile.SupportsLayoutFirmware(profile.Firmware))
        {
            string boot = profile.Firmware == Firmware.Gpt ? "UEFI / GPT" : "Legacy BIOS / MBR";
            lines.Add(LF($"Tryb rozruchu instalatora — {boot}.", $"Installer boot mode — {boot}."));
        }
        else
            lines.Add(NoCommonBootMode());
        if (profile.UsesLegacyWindows11)
            lines.Add(LegacyWindows11Note());
        lines.Add(InstallationMethods().Single(choice => choice.Value == method).Label);

        if (method == "Custom")
        {
            lines.Add(L("Dysk, partycje i nakładanie obrazu określa Twój skrypt CMD.", "Your CMD script controls the disk, partitions and image application."));
            return lines;
        }
        if (method == "Generated" && profile.Has("CustomDiskPart"))
        {
            lines.Add(L("Twój skrypt DiskPart wybiera i przygotowuje partycje. DISM nakłada obraz Windows.",
                "Your DiskPart script selects and prepares the partitions. DISM applies the Windows image."));
            return lines;
        }
        if (!profile.WipesDisk)
        {
            lines.Add(method == "Generated" ? ManualDiskPartNote() :
                L("Dysk i partycja do wybrania w instalatorze.", "Choose the disk and partition in Setup."));
            return lines;
        }

        lines.Add((method == "Generated" ? profile.Option("TargetDisk") : "") switch
        {
            "Interactive" => L("Skrypt zapyta o numer dysku do wyczyszczenia.", "The script asks for the number of the disk to erase."),
            "Checked" => L("Skrypt wyszuka dokładnie jeden dysk spełniający ustawione warunki i go wyczyści.",
                "The script erases the one disk that matches the configured requirements."),
            "Script" => L("Dysk do wyczyszczenia wskazuje Twój skrypt wyboru dysku.", "Your disk selection script identifies the disk to erase."),
            _ => LF($"Usunięcie wszystkich partycji z dysku {profile.DiskNumber} i utworzenie nowego układu.",
                $"Erase all partitions on disk {profile.DiskNumber} and create the new layout.")
        });
        try
        {
            var partitions = XMLator.Layout.Plan(profile);
            for (int i = 0; i < partitions.Count; i++)
            {
                var partition = partitions[i];
                string title = partition.Type switch
                {
                    "EFI" => L("Rozruch EFI", "EFI boot"),
                    "MSR" => L("Rezerwa Microsoft (MSR)", "Microsoft Reserved (MSR)"),
                    _ when partition.TypeId is not null => L("Odzyskiwanie (WinRE)", "Recovery (WinRE)"),
                    _ when partition.Active => L("Windows i rozruch (C:)", "Windows and boot files (C:)"),
                    _ when partition.Windows => L("Windows (C:)", "Windows (C:)"),
                    _ => L("Dane (D:)", "Data (D:)")
                };
                string size = partition.Size == 0 ? L("pozostałe miejsce", "remaining space") : $"{partition.Size} MiB";
                lines.Add($"{i + 1}. {title} — {size}{(partition.Format is null ? "" : $", {partition.Format}")}");
            }
        }
        catch (SetupException error)
        {
            lines.Add(error.Translate(UiLanguage));
        }
        return lines;
    }

    private void PartitionSizeHint(Fields fields) => fields.Note(L(
        "Rozmiar w MiB wpisz z klawiatury lub zmień strzałkami w górę i w dół.",
        "Type the size in MiB or adjust it with the Up and Down arrow keys."));

    private NumericUpDown PartitionSize(Fields fields, string label, int size, int minimum, int maximum, Action<int> changed,
        string extraPolish = "", string extraEnglish = "")
    {
        var input = fields.Number(label, size, minimum, maximum, changed);
        input.Enter += (_, _) => input.Select(0, input.Text.Length);
        input.KeyDown += (_, e) =>
        {
            if (e.KeyData == (Keys.Control | Keys.A))
            {
                input.Select(0, input.Text.Length);
                e.SuppressKeyPress = true;
            }
        };
        input.Validating += (_, _) => _ = input.Value;
        string instruction = L(
            "Pole liczbowe ze strzałkami. Wpisz rozmiar w MiB. Użyj Ctrl+A, aby zastąpić wpisaną liczbę. " +
            "Tab zatwierdza rozmiar i przechodzi do następnego pola. Strzałki w górę i w dół zmieniają wartość o 1 MiB.",
            "Numeric field with arrow buttons. Type the size in MiB. Press Ctrl+A to replace the current number. " +
            "Tab applies the size and moves to the next field. The Up and Down arrow keys change it by 1 MiB.");
        string description = $"{instruction} {L(extraPolish, extraEnglish)}".TrimEnd();
        Explain(input, description, description);
        return input;
    }
}
