namespace XMLator;

internal sealed partial class Wizard
{
    private string NoCommonBootMode() => L("Wybrane architektury wymagają różnych trybów rozruchu. Wybierz dysk i partycję w instalatorze.",
        "The selected architectures require different boot modes. Choose the disk and partition in Setup.");

    private string AvailablePartitionRequirement() => L("Użyj przygotowanego dysku MBR z aktywną partycją rozruchową i miejscem na Windows.",
        "Use a prepared MBR disk with an active boot partition and space for Windows.");

    private Choice<string>[] InstallationMethods()
    {
        var microsoft = C("Normal", "Microsoft Windows Setup (zalecany)", "Microsoft Windows Setup (recommended)",
            "Standardowy instalator odczyta XML, przygotuje wybrany układ i wykona kolejne etapy. W razie problemu może poprosić o odpowiedź.",
            "The standard installer reads the XML, prepares the selected layout and runs the installation stages. It can ask for input if there is a problem.");
        return profile.Target.ModernClient
            ? [microsoft, C("Generated", "Skrypt DISM ze strony schneegans.de", "DISM script from schneegans.de",
                "Skrypt sam nakłada obraz Windows i tworzy pliki rozruchowe. Użyj jednej dużej C z partycjami pomocniczymi albo skonfiguruj DiskPart.",
                "The script applies the Windows image and creates boot files. Use one large C with system partitions or configure DiskPart."),
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
        if (profile.WipesDisk || profile.FormatsPartition)
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
        var lines = new List<string> { InstallationMethods().Single(choice => choice.Value == method).Label };
        if (profile.SupportsLayoutFirmware(profile.Firmware))
        {
            string boot = profile.UsesAutomaticFirmware ? L("wykrycie UEFI lub BIOS w Windows PE", "detect UEFI or BIOS in Windows PE") :
                profile.Firmware == Firmware.Gpt ? "UEFI / GPT" : "Legacy BIOS / MBR";
            lines.Add(LF($"Tryb rozruchu instalatora — {boot}.", $"Installer boot mode — {boot}."));
        }
        else
            lines.Add(NoCommonBootMode());
        if (method == "Custom")
        {
            lines.Add(L("Dysk, partycje i nakładanie obrazu określa Twój skrypt CMD.", "Your CMD script controls the disk, partitions and image application."));
            return lines;
        }
        if (method == "Generated")
        {
            if (profile.Has("CustomDiskPart"))
                lines.Add(L("Twój skrypt DiskPart wybiera i przygotowuje partycje. DISM nakłada obraz Windows.", "Your DiskPart script selects and prepares the partitions. DISM applies the Windows image."));
            else if (profile.DiskMode == DiskMode.Interactive)
                lines.Add(L("Skrypt zatrzyma się, aby można było przygotować partycje ręcznie przez DiskPart.", "The script pauses for you to prepare partitions manually using DiskPart."));
            else if (profile.DiskMode == DiskMode.SystemAndWindows)
            {
                lines.Add(profile.Option("TargetDisk", "Fixed") switch
                {
                    "Interactive" => L("Skrypt zapyta o numer dysku do wyczyszczenia.", "The script asks for the number of the disk to erase."),
                    "Checked" => L("Skrypt wyszuka dokładnie jeden dysk spełniający ustawione warunki i go wyczyści.", "The script erases the one disk that matches the configured requirements."),
                    "Script" => L("Dysk do wyczyszczenia wskazuje Twój skrypt wyboru dysku.", "Your disk selection script identifies the disk to erase."),
                    _ => LF($"Skrypt wyczyści dysk {profile.DiskNumber}.", $"The script erases disk {profile.DiskNumber}.")
                });
                lines.Add(LF($"Partycja rozruchowa — {profile.BootPartitionMiB} MiB.", $"Boot partition — {profile.BootPartitionMiB} MiB."));
                lines.Add(profile.Recovery ? LF($"Duża C, a po niej partycja odzyskiwania — {profile.RecoveryMiB} MiB.",
                    $"Large C, followed by a {profile.RecoveryMiB} MiB recovery partition.") :
                    L("Duża C zajmie pozostałe miejsce. Skrypt pominie WinRE.", "Large C uses the remaining space. The script skips WinRE."));
            }
            else
                lines.Add(L("Ten układ obsługuje Windows Setup. Skrypt DISM może utworzyć układ z dużą C lub zatrzymać się do ręcznego partycjonowania.",
                    "Windows Setup supports this layout. The DISM script can create a large C layout or stop for manual partitioning."));
            return lines;
        }

        lines.Add(profile.DiskMode switch
        {
            DiskMode.Interactive => L("Dysk i partycja do wybrania w instalatorze.", "Choose the disk and partition in Setup."),
            DiskMode.Available => L("Windows Setup przeszuka wszystkie dyski. Wybierze pierwszą partycję z odpowiednią ilością miejsca i bez instalacji Windows.",
                "Windows Setup searches all disks. It uses the first partition with enough space and no Windows installation."),
            DiskMode.Existing => LF($"Instalacja na partycji {profile.PartitionNumber} dysku {profile.DiskNumber}. Kreator pomija automatyczne formatowanie.",
                $"Install to partition {profile.PartitionNumber} on disk {profile.DiskNumber}. The wizard skips automatic formatting."),
            DiskMode.FormatExisting => LF($"Formatowanie NTFS partycji {profile.PartitionNumber} na dysku {profile.DiskNumber}. Windows zostanie zainstalowany na tej partycji.",
                $"Format partition {profile.PartitionNumber} on disk {profile.DiskNumber} as NTFS and install Windows there."),
            _ => LF($"Usunięcie wszystkich partycji z dysku {profile.DiskNumber} i utworzenie nowego układu.",
                $"Erase all partitions on disk {profile.DiskNumber} and create the new layout.")
        });
        if (profile.DiskMode == DiskMode.Available)
            lines.Add(AvailablePartitionRequirement());
        if (!profile.WipesDisk)
            return lines;

        try
        {
            var partitions = XMLator.Layout.Plan(profile);
            for (int i = 0; i < partitions.Count; i++)
            {
                var partition = partitions[i];
                string title = partition.Windows ? L("Windows (C:)", "Windows (C:)") : partition.Type switch
                {
                    "EFI" => L("Rozruch EFI", "EFI boot"),
                    "MSR" => L("Rezerwa Microsoft (MSR)", "Microsoft Reserved (MSR)"),
                    "Extended" => L("Kontener partycji logicznych", "Container for logical partitions"),
                    _ when partition.TypeId is not null => L("Odzyskiwanie (WinRE)", "Recovery (WinRE)"),
                    _ when partition.Active => L("Aktywna partycja rozruchowa BIOS", "Active BIOS boot partition"),
                    _ => partition.Label ?? L("Dane", "Data")
                };
                string letter = partition.Letter is not null ? $" ({partition.Letter}:)" : "";
                string size = partition.Size == 0 ? L("pozostałe miejsce", "remaining space") : $"{partition.Size} MiB";
                lines.Add($"{i + 1}. {title}{letter} — {size}{(partition.Format is null ? "" : $", {partition.Format}")}");
            }
        }
        catch (SetupException error)
        {
            lines.Add(error.Translate(UiLanguage));
        }
        return lines;
    }

    private IEnumerable<Choice<string>> DataLetters() => new[] { C("", "Automatycznie w Windows", "Assign automatically in Windows") }
        .Concat("DEFGHIJKLMNOPQRSTUVWYZ".Select(letter => new Choice<string>(letter.ToString(), letter + ":")));

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

    private void EditDataPartitions()
    {
        var items = profile.DataPartitions.ToList();
        var fields = new Fields();
        fields.Note(L("Ustaw kolejność od początku do końca dysku. Tylko ostatnia partycja może zająć pozostałe miejsce.",
            "Set the order from the beginning to the end of the disk. Only the last partition can take the remaining space."));
        EditableList(fields, L("Partycje danych", "Data partitions"), items,
            item => $"{item.Label} {item.Letter} — {item.Format}, {(item.SizeMiB == 0 ? L("reszta dysku", "remaining space") : $"{item.SizeMiB} MiB")}",
            EditDataPartition, allowReorder: true);
        if (Ask(L("Własne partycje danych", "Custom data partitions"), fields))
            profile.DataPartitions = items;
    }

    private DataPartition? EditDataPartition(DataPartition? original)
    {
        var fields = new Fields();
        string name = original?.Label ?? L("Dane", "Data");
        string letter = original?.Letter ?? "";
        string format = original?.Format ?? "NTFS";
        int size = original?.SizeMiB ?? 0;
        var label = fields.Entry(L("&Nazwa partycji", "Partition &label"), name, value => name = value.Trim());
        fields.Select(L("&Litera dysku", "Drive &letter"), letter, DataLetters(), value => letter = value);
        fields.Select(L("&System plików", "&File system"), format,
            [C("NTFS", "NTFS", "NTFS"), C("FAT32", "FAT32", "FAT32")], value => format = value);
        PartitionSizeHint(fields);
        PartitionSize(fields, L("Rozmiar w MiB (0 oznacza resztę dysku)", "Size in MiB (0 uses the remaining space)"),
            size, 0, int.MaxValue, value => size = value, "1 GiB to 1024 MiB. Resztę wolnego miejsca może zająć ostatnia partycja.",
            "1 GiB is 1024 MiB. The last partition can use the remaining space.");
        return Ask(L("Partycja danych", "Data partition"), fields, label, validate: () => XMLator.Layout.ValidateLabel(name, format))
            ? new DataPartition(name, letter, size, format) : null;
    }
}
