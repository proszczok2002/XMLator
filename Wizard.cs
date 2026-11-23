using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms.Automation;

namespace XMLator;

internal sealed partial class Wizard : Form
{
    private static readonly Icon ApplicationIcon = new(typeof(Wizard), "ApplicationIcon.ico");
    internal static string ProductCaption { get; } = "XMLator " +
        typeof(Wizard).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2)[0];

    private static readonly (string Pl, string En)[] Steps =
    [
        ("System Windows", "Windows version"), ("Licencja", "License"), ("Edycja i klucz", "Edition and key"),
        ("Dysk i partycje", "Disk and partitions"), ("Język, region i komputer", "Language, region and computer"),
        ("Użytkownicy i pierwsze logowanie", "Users and first sign-in"), ("Ustawienia zaawansowane", "Advanced settings"),
        ("Sterowniki, pliki i skrypty OEM", "OEM drivers, files and scripts"), ("Podsumowanie i eksport", "Review and export")
    ];

    private readonly InterfacePreferences preferences;
    private string UiLanguage => preferences.EffectiveLanguage;
    private bool polish => UiLanguage == "pl";
    private Button? interfaceSettingsButton;
    private SetupProfile profile;
    private int step;
    private bool working;
    private readonly Panel content = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly MenuStrip menu = new();
    private readonly FlowLayoutPanel navigation = new()
    {
        FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true,
        Padding = new Padding(8, 5, 8, 8)
    };
    private readonly Label heading = new()
    {
        Dock = DockStyle.Fill, AutoSize = true, UseMnemonic = false,
        Margin = new Padding(13, 11, 13, 8)
    };
    private readonly ProgressBar operationProgress = new()
    {
        Dock = DockStyle.Fill, Height = 20, Margin = new Padding(13, 0, 13, 8), Visible = false, TabStop = false
    };
    private string? operationCaption;
    private Button back = null!;
    private Button? next;
    private string? lastOutput;
    private byte[]? savedConfiguration;

    internal Wizard(string? initialProfile, InterfacePreferences? preferences = null, UpdateService? updates = null,
        bool scheduleAutomaticUpdate = true)
    {
        this.preferences = preferences ?? new InterfacePreferences();
        this.updates = updates;
        profile = new SetupProfile();
        Text = ProductCaption;
        Icon = ApplicationIcon;
        Font = new Font("Segoe UI", 10);
        heading.Font = new Font(Font, FontStyle.Bold);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(650, 500);
        ClientSize = new Size(760, 680);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(menu, 0, 0);
        layout.Controls.Add(heading, 0, 1);
        layout.Controls.Add(operationProgress, 0, 2);
        layout.Controls.Add(content, 0, 3);
        layout.Controls.Add(navigation, 0, 4);
        Controls.Add(layout);
        MainMenuStrip = menu;
        FormClosing += ConfirmClose;
        optionHelp.Draw += DrawOptionHelp;
        Disposed += (_, _) => optionHelp.Dispose();
        SetupMenu();
        ShowStep(focus: false);
        MarkConfigurationSaved();
        Shown += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(initialProfile))
                Run(() => LoadProfile(initialProfile));
            FocusStep();
            if (scheduleAutomaticUpdate && updates is not null && this.preferences.ShouldCheckForUpdates(DateTimeOffset.UtcNow))
                _ = CheckAtStartupAsync();
        };
    }

    private string L(string pl, string en) => polish ? pl : UiText.Get(UiLanguage, en);
    private string LF(FormattableString pl, FormattableString en) =>
        polish ? pl.ToString(UiText.Culture("pl")) : UiText.Format(UiLanguage, en);
    private Choice<T> C<T>(T value, string pl, string en, string? helpPl = null, string? helpEn = null) =>
        new(value, L(pl, en), helpPl is not null && helpEn is not null ? L(helpPl, helpEn) : null);

    private void SetupMenu()
    {
        menu.Items.Clear();
        var file = new ToolStripMenuItem(L("&Plik", "&File"));
        AddMenu(file, L("&Zapisz plik odpowiedzi XML…", "&Save the XML answer file…"), Keys.Control | Keys.S, BeginExport);
        AddMenu(file, L("&Nowy profil", "&New profile"), Keys.Control | Keys.N, () =>
        {
            if (ShowMessage(this, L("Zastąpić bieżące ustawienia nowym profilem?", "Replace the current settings with a new profile?"),
                    MessageBoxIcon.Question, confirm: true) != DialogResult.Yes)
                return;
            profile = new();
            ResetUsbOptions();
            step = 0;
            ShowStep();
            MarkConfigurationSaved();
        });
        AddMenu(file, L("&Otwórz profil INI…", "&Open INI profile…"), Keys.Control | Keys.O, OpenProfile);
        AddMenu(file, L("Zapisz &profil INI…", "Save &INI profile…"), Keys.Control | Keys.Shift | Keys.S, SaveProfile);
        AddMenu(file, L("Przejdź do p&odsumowania", "Go to &review"), Keys.Control | Keys.End, () =>
        {
            Validate();
            step = Steps.Length - 1;
            ShowStep();
        });
        AddMenu(file, L("Za&mknij", "E&xit"), Keys.Alt | Keys.F4, Close);
        var language = new ToolStripMenuItem(L("&Język", "&Language"));
        foreach (var choice in LanguageChoices())
        {
            var item = AddMenu(language, choice.Label, Keys.None, () => ChangeLanguage(choice.Value));
            item.Checked = choice.Value == preferences.Language;
        }
        var view = new ToolStripMenuItem(L("&Widok", "&View"));
        var systemThemeItem = AddMenu(view, L("Motyw &systemowy", "&System theme"), Keys.None, () => ChangeTheme(AppTheme.System));
        var lightThemeItem = AddMenu(view, L("Motyw &jasny", "&Light theme"), Keys.None, () => ChangeTheme(AppTheme.Light));
        var darkThemeItem = AddMenu(view, L("Motyw &ciemny", "&Dark theme"), Keys.None, () => ChangeTheme(AppTheme.Dark));
        view.DropDownOpening += (_, _) =>
        {
            systemThemeItem.Checked = preferences.Theme == AppTheme.System;
            lightThemeItem.Checked = preferences.Theme == AppTheme.Light;
            darkThemeItem.Checked = preferences.Theme == AppTheme.Dark;
        };
        var help = new ToolStripMenuItem(L("Pomo&c", "&Help"));
        AddMenu(help, L("&Obsługa klawiaturą", "&Keyboard commands"), Keys.None, () => Info(KeyboardHelp()));
        AddMenu(help, L("&O programie", "&About"), Keys.None, ShowAbout);
        menu.Items.AddRange([file, language, view, help]);
    }

    private static string UpstreamLicense()
    {
        using var stream = typeof(Schneegans.Unattend.Configuration).Assembly
            .GetManifestResourceStream("Schneegans.Unattend.LICENSE.txt")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private ToolStripMenuItem AddMenu(ToolStripMenuItem parent, string label, Keys keys, Action action)
    {
        var item = new ToolStripMenuItem(label) { ShortcutKeys = keys };
        item.Click += (_, _) => { if (!working) Run(action); };
        parent.DropDownItems.Add(item);
        return item;
    }

    private IEnumerable<Choice<string>> LanguageChoices() =>
        [C("auto", "Zgodny z językiem Windows", "Follow Windows language"), .. UiText.Languages];

    private void ChangeLanguage(string language) => ChangeInterfacePreferences(language, preferences.Theme);

    private void ChangeInterfacePreferences(string language, AppTheme theme, bool focusStep = true)
    {
        language = UiText.PreferenceCode(language);
        bool languageChanged = preferences.Language != language;
        if (!languageChanged && preferences.Theme == theme)
            return;
        if (languageChanged)
            Validate();
        preferences.Language = language;
        preferences.Theme = theme;
        if (languageChanged)
        {
            SetupMenu();
            ShowStep(focusStep);
        }
        else
        {
            SetNativeTheme();
            ApplyTheme();
        }
        SaveInterfacePreferences();
    }

    private void OpenInterfaceSettings()
    {
        var fields = new Fields();
        string selectedLanguage = preferences.Language;
        AppTheme selectedTheme = preferences.Theme;
        var language = fields.Select(L("Język &interfejsu XMLatora", "XMLator &interface language"),
            selectedLanguage, LanguageChoices(), value => selectedLanguage = value);
        fields.Select(L("Motyw interfe&jsu XMLatora", "XMLator interface the&me"), selectedTheme,
            [C(AppTheme.System, "Systemowy", "System"), C(AppTheme.Light, "Jasny", "Light"), C(AppTheme.Dark, "Ciemny", "Dark")],
            value => selectedTheme = value);
        bool automatic = preferences.AutomaticUpdates;
        int interval = preferences.UpdateIntervalDays;
        ReleaseUpdate? acceptedUpdate = null;
        AddUpdateSettings(fields, value => automatic = value, value => interval = value, result => acceptedUpdate = result);
        if (Ask(L("Ustawienia", "Settings"), fields, first: language, compact: true, compactHeight: 340))
        {
            preferences.AutomaticUpdates = automatic;
            preferences.UpdateIntervalDays = interval;
            if (preferences.Language == selectedLanguage && preferences.Theme == selectedTheme)
                SaveInterfacePreferences();
            else
                ChangeInterfacePreferences(selectedLanguage, selectedTheme, focusStep: false);
        }
        interfaceSettingsButton?.Select();
        if (acceptedUpdate is not null)
            _ = InstallUpdateAsync(acceptedUpdate);
    }

    private string StepHeading() =>
        LF($"Krok {step + 1} z {Steps.Length}. {Steps[step].Pl}",
            $"Step {step + 1} of {Steps.Length}. {UiText.Get(UiLanguage, Steps[step].En)}");

    private void SetHeading(string message) => Text = $"{ProductCaption} — {message}";

    private void ShowStep(bool focus = true)
    {
        bool wasSaved = savedConfiguration is not null && !HasUnsavedConfiguration();
        SetNativeTheme();
        SuspendLayout();
        try
        {
            foreach (Control old in content.Controls.Cast<Control>().ToArray())
                old.Dispose();
            foreach (Control old in navigation.Controls.Cast<Control>().ToArray())
                old.Dispose();
            interfaceSettingsButton = null;
            heading.Text = StepHeading();
            SetHeading(heading.Text);
            var fields = new Fields();
            content.Controls.Add(fields.Table);
            switch (step)
            {
                case 0: SystemPage(fields); break;
                case 1: LicensePage(fields); break;
                case 2: EditionPage(fields); break;
                case 3: DiskPage(fields); break;
                case 4: RegionPage(fields); break;
                case 5: AccountsPage(fields); break;
                case 6: AdvancedPage(fields); break;
                case 7: MediaPage(fields); break;
                case 8: ReviewPage(fields); break;
            }
            back = Fields.ActionButton(L("&Wstecz", "&Back"), () => ChangeStep(-1));
            back.Enabled = step > 0;
            back.TabIndex = 0;
            if (step == Steps.Length - 1)
            {
                next = null;
                navigation.Controls.Add(back);
                AcceptButton = null;
            }
            else
            {
                next = Fields.ActionButton(L("&Dalej", "&Next"), () => ChangeStep(1));
                next.TabIndex = 1;
                navigation.Controls.AddRange([next, back]);
                AcceptButton = next;
            }
            content.AutoScrollPosition = Point.Empty;
            ApplyTheme();
        }
        finally
        {
            ResumeLayout(true);
        }
        if (focus)
            FocusStep();
        if (wasSaved)
            MarkConfigurationSaved();
    }

    private void FocusStep()
    {
        if (!content.SelectNextControl(null, forward: true, tabStopOnly: true, nested: true, wrap: false))
            (next ?? back).Select();
    }

    private void ChangeStep(int delta)
    {
        if (working)
            return;
        step = Math.Clamp(step + delta, 0, Steps.Length - 1);
        ShowStep();
    }

    private void SystemPage(Fields f)
    {
        ComboBox? architecture = null;
        var architectureField = new Fields();
        Label fixedArchitecture = null!;
        var additional = new Fields();
        IEnumerable<Choice<string>> ArchitectureChoices() => profile.Target.Architectures.Select(a => new Choice<string>(a, ArchitectureLabel(a)));
        void ShowArchitecture()
        {
            bool fixedValue = profile.Target.Architectures.Length == 1;
            architectureField.Table.Visible = !fixedValue;
            fixedArchitecture.Visible = fixedValue;
            string value = ArchitectureLabel(profile.Architecture);
            fixedArchitecture.Text = LF($"Architektura obrazu instalacyjnego — {value}", $"Installation image architecture — {value}");
        }
        void UpdateAdditional()
        {
            foreach (Control input in additional.Table.Controls.Cast<Control>().ToArray())
                input.Dispose();
            additional.Table.RowCount = 0;
            profile.ExtraArchitectures.RemoveAll(a => a == profile.Architecture || !profile.Target.Architectures.Contains(a));
            foreach (string a in profile.Target.Architectures.Where(a => a != profile.Architecture))
                additional.Check(LF($"Dodaj architekturę {ArchitectureLabel(a)} do tego samego XML", $"Also include {ArchitectureLabel(a)} in the same XML"),
                    profile.ExtraArchitectures.Contains(a), selected =>
                    {
                        if (selected)
                            profile.ExtraArchitectures.Add(a);
                        else
                            profile.ExtraArchitectures.Remove(a);
                    });
        }
        Explain(f.Select(L("&Który Windows chcesz zainstalować?", "&Which version of Windows do you want to install?"),
            profile.TargetName, Target.All.Select(t => new Choice<string>(t.Name, t.Name)), value =>
            {
                profile.TargetName = value;
                if (!profile.Target.Architectures.Contains(profile.Architecture))
                    profile.Architecture = profile.Target.Architectures[0];
                if (profile.ImageMode == ImageMode.Edition && profile.SelectedEdition is null)
                    profile.ImageMode = ImageMode.Interactive;
                if (!profile.Target.ModernClient && profile.KeyMode is KeyMode.Firmware or KeyMode.Ask or KeyMode.Generic)
                    profile.KeyMode = KeyMode.Default;
                if (!profile.Target.CanUseGpt(profile.Architecture))
                    profile.Firmware = Firmware.Mbr;
                else if (profile.Target.Windows11 || profile.Architecture is "arm64" or "ia64")
                    profile.Firmware = Firmware.Gpt;
                Fields.UpdateChoices(architecture!, profile.Architecture, ArchitectureChoices());
                ShowArchitecture();
                UpdateAdditional();
                ApplyTheme();
            }), "Wskaż wersję Windows, która ma trafić na docelowy komputer. Wybór określa dostępne edycje i ustawienia.",
            "Choose the Windows version to install on the destination computer. It determines the available editions and settings.");
        architecture = Explain(architectureField.Select(L("&Architektura obrazu instalacyjnego", "Installation image &architecture"),
            profile.Architecture, ArchitectureChoices(), a =>
            {
                if (a == profile.Architecture)
                    return;
                profile.Architecture = a;
                if (!profile.Target.CanUseGpt(a))
                    profile.Firmware = Firmware.Mbr;
                else if (profile.Target.Windows11 || a is "ia64" or "arm64")
                    profile.Firmware = Firmware.Gpt;
                if (profile.ImageMode == ImageMode.Edition && profile.SelectedEdition is null)
                    profile.ImageMode = ImageMode.Interactive;
                UpdateAdditional();
                ApplyTheme();
            }), "Wybierz architekturę instalatora. Dla typowych komputerów Intel i AMD wybierz x64.",
            "Choose the installer architecture. Most Intel and AMD computers use x64.");
        f.Section(architectureField);
        fixedArchitecture = f.Note("");
        ShowArchitecture();
        UpdateAdditional();
        f.Section(additional);
        f.Button(L("&Otwórz zapisany profil…", "&Open a saved profile…"), () => Run(OpenProfile));
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        interfaceSettingsButton = Fields.ActionButton(L("&Ustawienia…", "&Settings…"), () => Run(OpenInterfaceSettings));
        var support = Fields.ActionButton(L("Ws&parcie i kontakt…", "Suppor&t and contact…"), () => Run(ShowSupport));
        actions.Controls.AddRange([interfaceSettingsButton, support]);
        f.Add(actions);
    }

    private static string ArchitectureLabel(string value) => value switch
    {
        "amd64" => "x64 (AMD64)", "arm64" => "ARM64", "ia64" => "Itanium (IA-64)", _ => "x86 (32-bit)"
    };

    private void LicensePage(Fields f)
    {
        Explain(f.Select(L("Czy instalator ma &zaakceptować warunki licencji?", "Automatically &accept the license terms?"), profile.AcceptEula,
            [C(false, "Nie. Do zaakceptowania w instalatorze.", "No. Accept the terms in Setup."), C(true, "Tak. Akceptuję warunki licencji używanego nośnika.", "Yes. I accept the license terms for the installation media.")],
            value => profile.AcceptEula = value), "Wybierz Tak po zaakceptowaniu warunków licencji dla używanego nośnika. Instalator wykorzysta tę odpowiedź podczas instalacji.",
            "Choose Yes after accepting the terms that apply to your installation media. Setup will use that answer during installation.");
    }

    private void EditionPage(Fields f)
    {
        var name = new Fields();
        var index = new Fields();
        var hint = new Fields();
        Action refreshGenericKey = () => { };
        var choices = new List<Choice<string>>
        {
            C("Interactive", "Do wybrania w instalatorze", "Choose in Setup")
        };
        choices.AddRange(EditionCatalog.For(profile.Target, profile.Architecture).Select(e => new Choice<string>("edition:" + e.Id, e.Label)));
        choices.Add(C("Index", "Wpisz indeks obrazu…", "Enter an image index…"));
        choices.Add(C("ExactName", "Wpisz dokładną nazwę obrazu…", "Enter the exact image name…"));
        string selected = profile.ImageMode == ImageMode.Edition ? "edition:" + profile.PresetEdition : profile.ImageMode.ToString();
        Explain(f.Select(L("Którą &edycję Windows zainstalować?", "Which Windows &edition should be installed?"), selected, choices, value =>
        {
            if (value.StartsWith("edition:", StringComparison.Ordinal))
            {
                profile.ImageMode = ImageMode.Edition;
                profile.PresetEdition = value["edition:".Length..];
            }
            else
                profile.ImageMode = Enum.Parse<ImageMode>(value);
            name.Table.Visible = profile.ImageMode == ImageMode.ExactName;
            index.Table.Visible = profile.ImageMode == ImageMode.Index;
            hint.Table.Visible = profile.ImageMode == ImageMode.Edition;
            refreshGenericKey();
        }), "Dla standardowego nośnika wybierz edycję, na przykład Home lub Pro. Indeks i dokładna nazwa obrazu są przydatne w przypadku nośników zmodyfikowanych.",
            "Choose an edition such as Home or Pro for standard media. An image index or exact name is useful for customized media.");
        name.Entry(L("Dokładna &nazwa obrazu", "Exact image &name"), profile.ImageName, value => profile.ImageName = value);
        index.Number(L("&Indeks obrazu", "&Image index"), profile.ImageIndex, 1, 9999, value => profile.ImageIndex = value);
        hint.Note(L("Windows Setup wyszuka tę edycję na nośniku podczas instalacji.",
            "Windows Setup will look for this edition on the media during installation."));
        name.Table.Visible = profile.ImageMode == ImageMode.ExactName;
        index.Table.Visible = profile.ImageMode == ImageMode.Index;
        hint.Table.Visible = profile.ImageMode == ImageMode.Edition;
        f.Section(hint);
        f.Section(name);
        f.Section(index);

        var keys = new List<Choice<KeyMode>>
        {
            C(KeyMode.Default, "Do podania w instalatorze, jeśli zapyta", "Enter the key during Setup if prompted"),
            C(KeyMode.Skip, "Pomiń pytanie o klucz instalacyjny", "Skip the setup product key prompt"),
            C(KeyMode.Enter, "Wpisuję własny klucz instalacyjny", "Enter my setup product key")
        };
        if (profile.Target.ModernClient)
        {
            keys.Add(C(KeyMode.Ask, "Zawsze pokaż ekran wpisywania klucza", "Always show the product key screen"));
            keys.Add(C(KeyMode.Generic, "Wybierz edycję kluczem ogólnym do instalacji", "Select an edition with a generic setup key"));
        }
        var setup = new Fields();
        var generic = new Fields();
        var automaticGeneric = new Fields();
        var skip = new Fields();
        if (profile.KeyMode == KeyMode.Firmware)
            profile.KeyMode = KeyMode.Skip;
        Explain(f.Select(L("Czy podać &klucz produktu do instalacji?", "Specify a &product key for installation?"), profile.KeyMode, keys,
            mode => { profile.KeyMode = mode; setup.Table.Visible = mode == KeyMode.Enter; skip.Table.Visible = mode == KeyMode.Skip; refreshGenericKey(); }),
            "Klucz instalacyjny musi pasować do edycji. Ogólny klucz służy do wyboru edycji podczas instalacji. Aktywacja wymaga odpowiedniej licencji.",
            "The setup key must match the edition. A generic setup key selects an edition during installation. Activation requires the appropriate license.");
        setup.Entry(L("Klucz &instalacyjny", "Setup product &key"), profile.SetupKey, value => profile.SetupKey = value.Trim(), secret: true);
        setup.Check(L("Użyj tego klucza także do aktywacji, jeśli nie podam oddzielnego", "Also use this key for activation unless I enter a separate one"),
            profile.UseSetupKeyForActivation, value => profile.UseSetupKeyForActivation = value);
        setup.Table.Visible = profile.KeyMode == KeyMode.Enter;
        skip.Note(L("Plik odpowiedzi nie poda klucza instalacyjnego. Jeśli używany obraz wymaga pasującego klucza, instalator może przerwać pracę. Aktywacja jest oddzielna.",
            "The answer file supplies no setup product key. If the media requires a matching key, Setup may stop. Activation is separate."));
        skip.Table.Visible = profile.KeyMode == KeyMode.Skip;
        f.Section(skip);
        f.Section(setup);
        if (profile.Target.ModernClient)
        {
            var editions = ModernOptions.Engine.WindowsEditions.Values.OrderBy(e => e.DisplayName).ToArray();
            if (!editions.Any(e => e.Id == profile.GenericEdition))
                profile.GenericEdition = editions[0].Id;
            generic.Select(L("Edycja dla klucza ogólnego", "Edition for the generic key"), profile.GenericEdition,
                editions.Select(e => new Choice<string>(e.Id, e.DisplayName)), value => profile.GenericEdition = value);
            generic.Note(L("Klucz ogólny wybiera edycję dostępną na nośniku. Do aktywacji użyj własnej licencji.",
                "A generic key selects an edition available on the media. Use your own license for activation."));
            automaticGeneric.Note(L("Kreator dobierze klucz ogólny do wybranej edycji, jeśli jest dostępny. Do aktywacji użyj własnej licencji.",
                "The wizard selects a generic setup key for the chosen edition where available. Use your own license for activation."));
        }
        refreshGenericKey = () =>
        {
            generic.Table.Visible = profile.KeyMode == KeyMode.Generic && profile.ImageMode != ImageMode.Edition;
            automaticGeneric.Table.Visible = profile.KeyMode == KeyMode.Generic && profile.ImageMode == ImageMode.Edition;
        };
        refreshGenericKey();
        f.Section(generic);
        f.Section(automaticGeneric);

        var activation = new Fields();
        f.Check(L("Podaj oddzielny klucz &aktywacji", "Specify a separate &activation key"), profile.UseActivationKey,
            value => { profile.UseActivationKey = value; activation.Table.Visible = value; });
        activation.Entry(L("Klucz aktywacji", "Activation key"), profile.ActivationKey, value => profile.ActivationKey = value.Trim(), secret: true);
        activation.Table.Visible = profile.UseActivationKey;
        f.Section(activation);
    }

    private void DiskPage(Fields f)
    {
        var details = new Fields();
        var existing = new Fields();
        var partitionChoice = new Fields();
        var layout = new Fields();
        var bootChoice = new Fields();
        var efi = new Fields();
        var bios = new Fields();
        var automaticBootSize = new Fields();
        var recovery = new Fields();
        var data = new Fields();
        var sizes = new Fields();
        var custom = new Fields();
        var format = new Fields();
        var optionalBios = new Fields();
        var confirmation = new Fields();
        ListBox? preview = null;
        ComboBox disk = null!;
        ComboBox boot = null!;
        NumericUpDown automatic = null!;
        Choice<DiskMode>[] diskOptions = [];
        Choice<Firmware>[] bootOptions = [];
        Button standardOptions = null!;
        CheckBox? confirmed = null;
        void Refresh()
        {
            var method = InstallationMethod();
            var modes = bootOptions.Where(choice => profile.SupportsLayoutFirmware(choice.Value)).ToArray();
            var disks = diskOptions.Where(choice => choice.Value == DiskMode.Interactive ||
                method == "Normal" && (choice.Value == DiskMode.Single ? modes.Any(mode => mode.Value == Firmware.Mbr) :
                    modes.Length != 0 || choice.Value is DiskMode.Available or DiskMode.Existing or DiskMode.FormatExisting) ||
                method == "Generated" && !profile.Has("CustomDiskPart") && choice.Value == DiskMode.SystemAndWindows && modes.Length != 0).ToArray();
            bool automaticPartition = method == "Normal" && profile.DiskMode == DiskMode.Available;
            if (!automaticPartition && !disks.Any(choice => choice.Value == profile.DiskMode))
                profile.DiskMode = DiskMode.Interactive;
            if (profile.DiskMode != DiskMode.Single && modes.Length != 0 && !modes.Any(choice => choice.Value == profile.Firmware))
                profile.Firmware = modes[0].Value;
            if (!automaticPartition && !disks.SequenceEqual(disk.Items.OfType<Choice<DiskMode>>()))
                Fields.UpdateChoices(disk, profile.DiskMode, disks);
            else if (!automaticPartition)
                SelectValue(disk, profile.DiskMode);
            if (!modes.SequenceEqual(boot.Items.OfType<Choice<Firmware>>()))
                Fields.UpdateChoices(boot, profile.Firmware, modes);
            else
                SelectValue(boot, profile.Firmware);

            if (confirmed is not null)
            {
                confirmed.Text = DiskApprovalLabel();
                confirmed.AccessibleName = confirmed.Text.Replace("&", "");
            }
            if (confirmed is { Checked: true } && !profile.ConfirmWipe)
                confirmed.Checked = false;
            standardOptions.Visible = method == "Normal";
            partitionChoice.Table.Visible = !automaticPartition && (method == "Normal" || method == "Generated" && !profile.Has("CustomDiskPart") && disks.Length > 1);
            details.Table.Visible = profile.NeedsDiskNumber && (method == "Normal" || profile.Option("TargetDisk", "Fixed") == "Fixed" ||
                profile.Option("TargetDisk") == "Checked" && !profile.Has("DiskAnyIndex"));
            existing.Table.Visible = profile.DiskMode is DiskMode.Existing or DiskMode.FormatExisting;
            format.Table.Visible = profile.FormatsPartition;
            layout.Table.Visible = profile.WipesDisk;
            bool sizedLayout = profile.WipesDisk && profile.DiskMode != DiskMode.Single;
            sizes.Table.Visible = sizedLayout;
            bootChoice.Table.Visible = sizedLayout && modes.Length > 1 && !profile.DetectsDiskFirmware;
            automaticBootSize.Table.Visible = profile.DetectsDiskFirmware;
            if (profile.DetectsDiskFirmware)
                automatic.Value = profile.Firmware == Firmware.Gpt ? profile.EfiMiB : profile.BiosMiB;
            efi.Table.Visible = profile.Firmware == Firmware.Gpt && !profile.DetectsDiskFirmware;
            optionalBios.Table.Visible = profile.Firmware == Firmware.Mbr && profile.DiskMode == DiskMode.Custom;
            bios.Table.Visible = profile.Firmware == Firmware.Mbr && !profile.DetectsDiskFirmware &&
                (profile.DiskMode != DiskMode.Custom || profile.SeparateBiosBoot);
            recovery.Table.Visible = profile.Recovery;
            data.Table.Visible = profile.HasSeparateData;
            custom.Table.Visible = profile.DiskMode == DiskMode.Custom;
            confirmation.Table.Visible = profile.RequiresDiskConsent;
            if (preview is not null)
                Fields.UpdateList(preview, DiskSummary());
        }

        if (profile.Target.ModernClient)
        {
            Explain(f.Select(L("&Metoda instalacji", "Installation &method"), InstallationMethod(),
                InstallationMethods(), value => { profile.Advanced["PE"] = value; Refresh(); }),
                "Windows Setup to instalator Microsoftu. Skrypt DISM nakłada obraz bezpośrednio i ma własne opcje dysku.",
                "Windows Setup is the Microsoft installer. The DISM script applies the image directly and has its own disk options.");
            CategoryButton(f, L("Ustawienia wybranej metody…", "Settings for the selected method…"), PeOptions, Refresh);
        }
        standardOptions = f.Button(L("Zaawansowane ustawienia Windows Setup…", "Advanced Windows Setup settings…"), () =>
        {
            StandardSetupOptions();
            Refresh();
        });

        diskOptions =
            [C(DiskMode.Interactive, "Ręcznie w instalatorze", "Manually in Setup",
                "Instalator zapyta o dysk i partycję. Tam też zdecydujesz o formatowaniu.", "Setup asks you to select the disk and partition. You can also choose whether to format it there."),
             C(DiskMode.Existing, "Wskaż istniejącą partycję bez formatowania", "Use an existing partition without formatting",
                "Podaj numer dysku i numer partycji z instalatora. XML wskazuje cel instalacji i pozostawia układ partycji.",
                "Enter the disk and partition numbers shown in Setup. The XML selects the installation destination and preserves the partition layout."),
             C(DiskMode.FormatExisting, "Sformatuj tylko wskazaną partycję", "Format only the selected partition",
                "Instalator usunie dane ze wskazanej partycji i sformatuje ją jako NTFS. Pozostałe partycje zachowa.",
                "Setup erases the data on the selected partition and formats it as NTFS. It keeps the other partitions."),
             C(DiskMode.Single, "Cały dysk jako jedna aktywna partycja C (BIOS/MBR)", "Whole disk as one active C partition (BIOS/MBR)",
                "Instalator wyczyści cały wybrany dysk. Windows i pliki rozruchowe znajdą się na tej samej, aktywnej partycji C.",
                "Setup wipes the entire selected disk. The single active C partition holds both Windows and the boot files."),
             C(DiskMode.SystemAndWindows, "Jedna duża C i partycje pomocnicze", "One large C partition plus system partitions",
                "Instalator wyczyści cały wybrany dysk. W UEFI utworzy EFI i MSR, w BIOS osobną partycję rozruchową. Możesz dodać WinRE. C zajmie resztę miejsca.",
                "Setup wipes the entire selected disk. UEFI gets EFI and MSR partitions, while BIOS gets a separate boot partition. WinRE is optional. C uses the remaining space."),
             C(DiskMode.SystemWindowsData, "C, partycja danych i partycje pomocnicze", "C, a data partition, and system partitions",
                "Instalator wyczyści cały wybrany dysk. Podaj rozmiar C i wybierz literę partycji danych. Wpisz 0 w rozmiarze danych, aby wypełnić pozostałe miejsce.",
                "Setup wipes the entire selected disk. Set the size of C and choose a letter for the data partition. Enter 0 as the data size to use the remaining space."),
             C(DiskMode.Custom, "Własny układ z dodatkowymi partycjami danych", "Custom layout with multiple data partitions",
                "Instalator wyczyści cały wybrany dysk. Samodzielnie ustalisz rozmiary, kolejność, litery i nazwy partycji danych.",
                "Setup wipes the entire selected disk. Set the sizes, order, letters and labels of the data partitions yourself.")];
        disk = Explain(partitionChoice.Select(L("Jak wybrać dysk i part&ycje?", "How should the &disk and partitions be selected?"),
            profile.DiskMode, diskOptions, value => { profile.DiskMode = value; Refresh(); }),
            "Wybierz ręczny wybór, istniejącą partycję lub nowy układ. Utworzenie nowego układu usuwa partycje z wybranego dysku.",
            "Choose manual selection, an existing partition, or a new layout. Creating a new layout erases partitions on the selected disk.");
        f.Section(partitionChoice);
        Explain(details.Number(L("Numer dysku według Windows Setup", "Disk number in Windows Setup"), profile.DiskNumber, 0, 999,
            value => { profile.DiskNumber = value; Refresh(); }), "Sprawdź numer dysku w instalatorze. Dysk 0 nie zawsze jest dyskiem, na którym chcesz zainstalować system.",
            "Check the disk number in Setup. Disk 0 may refer to a different drive than the one where you want to install Windows.");
        Explain(existing.Number(L("Numer istniejącej partycji", "Existing partition number"), profile.PartitionNumber, 1, 999,
            value => { profile.PartitionNumber = value; Refresh(); }), "Użyj numeru podanego po wybraniu dysku w instalatorze. Numer partycji zaczyna się od 1.",
            "Use the number shown when you select the disk in Setup. Partition numbering starts at 1.");
        var formattedLabel = format.Entry(L("Nazwa formatowanej partycji", "Label for the formatted partition"), profile.WindowsLabel,
            value => { profile.WindowsLabel = value.Trim(); Refresh(); });
        existing.Section(format);
        details.Section(existing);
        f.Section(details);

        bootOptions = [C(Firmware.Gpt, "UEFI / GPT", "UEFI / GPT",
                "Uruchom nośnik w trybie UEFI. EFI przechowuje pliki startowe, a MSR to rezerwa systemowa Microsoftu.",
                "Start the media in UEFI mode. EFI holds the boot files, and MSR is reserved for use by Windows."),
             C(Firmware.Mbr, "BIOS / MBR", "BIOS / MBR",
                "Uruchom nośnik w trybie BIOS lub Legacy. Pliki startowe trafią na aktywną partycję.",
                "Start the media in BIOS or Legacy mode. The active partition holds the boot files.")];
        boot = Explain(bootChoice.Select(L("Tryb &rozruchu i układ partycji", "Boot &mode and partition layout"), profile.Firmware,
            bootOptions,
            value =>
            {
                if (profile.DiskMode != DiskMode.Single)
                {
                    profile.Firmware = value;
                    Refresh();
                }
            }), "Wybierz UEFI/GPT dla współczesnego komputera lub BIOS/MBR dla starszego trybu rozruchu. Uruchom pendrive w wybranym trybie.",
            "Select UEFI/GPT for modern firmware or BIOS/MBR for legacy boot. Start the USB installer using the selected mode.");
        layout.Section(bootChoice);
        PartitionSizeHint(sizes);
        var efiSize = PartitionSize(efi, L("Rozmiar EFI w MiB", "EFI size in MiB"), profile.EfiMiB, 1, 65536, value => { profile.EfiMiB = value; Refresh(); });
        var biosSize = PartitionSize(bios, L("Rozmiar aktywnej partycji systemowej BIOS w MiB", "Active BIOS system partition size in MiB"),
            profile.BiosMiB, 1, 65536, value => { profile.BiosMiB = value; Refresh(); });
        automatic = PartitionSize(automaticBootSize, L("Rozmiar partycji rozruchowej EFI lub BIOS w MiB", "EFI or BIOS boot partition size in MiB"),
            profile.Firmware == Firmware.Gpt ? profile.EfiMiB : profile.BiosMiB, 1, 65536, value =>
            {
                (profile.Firmware == Firmware.Gpt ? efiSize : biosSize).Value = value;
                Refresh();
            });
        optionalBios.Select(L("Gdzie umieścić pliki rozruchowe BIOS?", "Where should the BIOS boot files go?"), profile.SeparateBiosBoot,
            [C(true, "Oddzielna aktywna partycja systemowa", "Separate active system partition"), C(false, "Na aktywnej partycji Windows (C:)", "On the active Windows partition (C:)")],
            value => { profile.SeparateBiosBoot = value; Refresh(); });
        sizes.Section(efi);
        sizes.Section(optionalBios);
        sizes.Section(bios);
        sizes.Section(automaticBootSize);
        Explain(sizes.Check(L("Utwórz partycję &odzyskiwania (WinRE)", "Create a &recovery partition (WinRE)"),
            profile.Recovery, value => { profile.Recovery = value; Refresh(); }),
            "Rezerwuje osobne miejsce na narzędzia naprawy i odzyskiwania Windows.",
            "Reserves a separate partition for Windows repair and recovery tools.");
        PartitionSize(recovery, L("Rozmiar odzyskiwania w MiB", "Recovery size in MiB"), profile.RecoveryMiB, 1, 65536,
            value => { profile.RecoveryMiB = value; Refresh(); });
        sizes.Section(recovery);
        layout.Section(sizes);
        PartitionSize(data, L("Rozmiar C w MiB", "C partition size in MiB"), profile.WindowsMiB, 1, int.MaxValue,
            value => { profile.WindowsMiB = value; Refresh(); });
        PartitionSize(data, L("Rozmiar partycji danych w MiB (0 oznacza resztę dysku)", "Data partition size in MiB (0 uses the remaining space)"),
            profile.DataMiB, 0, int.MaxValue, value => { profile.DataMiB = value; Refresh(); },
            "Wpisz 0, aby użyć pozostałego miejsca.", "Enter 0 to use the remaining space.");
        data.Select(L("Litera partycji danych", "Data partition drive letter"), profile.DataLetter,
            DataLetters(),
            value => { profile.DataLetter = value; Refresh(); });
        layout.Section(data);
        var customLabel = custom.Entry(L("Nazwa partycji Windows (C:)", "Windows partition label (C:)"), profile.WindowsLabel,
            value => { profile.WindowsLabel = value.Trim(); Refresh(); });
        formattedLabel.Leave += (_, _) => customLabel.Text = profile.WindowsLabel;
        customLabel.Leave += (_, _) => formattedLabel.Text = profile.WindowsLabel;
        PartitionSize(custom, L("Rozmiar C w MiB (0 oznacza resztę dysku)", "C size in MiB (0 uses the remaining space)"),
            profile.CustomWindowsMiB, 0, int.MaxValue, value => { profile.CustomWindowsMiB = value; Refresh(); },
            "1 GiB to 1024 MiB. Jeżeli dodajesz osobne partycje danych, podaj stały rozmiar C, np. 131072 MiB dla 128 GiB.",
            "1 GiB is 1024 MiB. If you add separate data partitions, enter a fixed C size, for example 131072 MiB for 128 GiB.");
        custom.Button(L("&Dodaj, edytuj i ustaw kolejność partycji danych…", "&Add, edit and reorder data partitions…"), () =>
        {
            EditDataPartitions();
            Refresh();
        });
        layout.Section(custom);
        f.Section(layout);
        preview = f.List(L("Plan partycji w kolejności na dysku", "Partition plan in disk order"), DiskSummary());
        confirmed = DiskApproval(confirmation);
        f.Section(confirmation);
        Refresh();
    }

    private void RegionPage(Fields f)
    {
        RegionChoices(f);
        var name = new Fields();
        var computerScript = new Fields();
        if (profile.Target.ModernClient)
            f.Select(L("Jak ustawić &nazwę komputera?", "How should the computer &name be set?"), profile.ComputerNameMode,
                [C("default", "Wygeneruj losową nazwę komputera", "Generate a random computer name"), C("fixed", "Podaj nazwę", "Enter a fixed name"),
                    C("prompt", "Zapytaj podczas instalacji (Schneegans)", "Ask during installation (Schneegans)"),
                    C("script", "Skrypt PowerShell", "PowerShell script")],
                value => { profile.ComputerNameMode = value; name.Table.Visible = value == "fixed"; computerScript.Table.Visible = value == "script"; });
        name.Entry(L("Nazwa komputera (maksymalnie 15 znaków)", "Computer name (up to 15 characters)"),
            profile.ComputerName, value => profile.ComputerName = value.Trim());
        computerScript.Entry(L("Skrypt zwracający nazwę komputera", "Script returning a computer name"),
            profile.ComputerNameScript, value => profile.ComputerNameScript = value, multiline: true);
        name.Table.Visible = !profile.Target.ModernClient || profile.ComputerNameMode == "fixed";
        computerScript.Table.Visible = profile.Target.ModernClient && profile.ComputerNameMode == "script";
        f.Section(name);
        f.Section(computerScript);
        var registration = new Fields();
        bool registered = profile.RegisteredOwner.Length > 0 || profile.RegisteredOrganization.Length > 0 || profile.Workgroup.Length > 0;
        f.Check(L("Ustaw właściciela, organizację lub grupę roboczą", "Set a registered owner, organization or workgroup"),
            registered, value =>
            {
                registration.Table.Visible = value;
                if (!value)
                {
                    profile.RegisteredOwner = profile.RegisteredOrganization = profile.Workgroup = "";
                    foreach (var input in registration.Table.Controls.OfType<TextBox>())
                        input.Clear();
                }
            });
        registration.Entry(L("Zarejestrowany właściciel Windows", "Registered Windows owner"), profile.RegisteredOwner, value => profile.RegisteredOwner = value.Trim());
        registration.Entry(L("Zarejestrowana organizacja", "Registered organization"), profile.RegisteredOrganization, value => profile.RegisteredOrganization = value.Trim());
        Explain(registration.Entry(L("Nazwa grupy roboczej", "Workgroup name"), profile.Workgroup, value => profile.Workgroup = value.Trim()),
            "Ustaw nazwę grupy roboczej w sieci, np. WORKGROUP. Puste pole pozostawia wartość instalatora.",
            "Set the network workgroup name, for example WORKGROUP. Leave blank to use the Setup default.");
        registration.Table.Visible = registered;
        f.Section(registration);
    }

    private bool Ask(string title, Fields fields, Control? first = null, string? okText = null, Action? validate = null,
        bool compact = false, int compactHeight = 190)
    {
        using var dialog = new Form
        {
            Text = title, StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, Font = Font,
            MinimumSize = compact ? new Size(410, 220) : new Size(520, 395),
            ClientSize = compact ? new Size(510, compactHeight) : new Size(660, 490), AutoScaleMode = AutoScaleMode.Dpi,
            MinimizeBox = !compact, MaximizeBox = !compact
        };
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var cancel = Fields.ActionButton(L("Anuluj", "Cancel"), () => dialog.DialogResult = DialogResult.Cancel);
        var ok = Fields.ActionButton(okText ?? L("&OK", "&OK"), () =>
        {
            try { validate?.Invoke(); dialog.DialogResult = DialogResult.OK; }
            catch (Exception e) { Error(e, dialog); }
        });
        buttons.Controls.AddRange([ok, cancel]);
        scroll.Controls.Add(fields.Table);
        dialog.Controls.Add(scroll);
        dialog.Controls.Add(buttons);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        ApplyTheme(dialog);
        if (first is not null)
            dialog.Shown += (_, _) => first.Focus();
        return dialog.ShowDialog(this) == DialogResult.OK;
    }

    private void Info(string message) => ShowMessage(this, message, MessageBoxIcon.Information);

    private string ErrorMessage(Exception error) => error is SetupException setup
        ? setup.Translate(UiLanguage)
        : UiText.Get(UiLanguage, error.Message);

    private void Error(Exception e, Form? owner = null) => ShowMessage(owner ?? this, ErrorMessage(e), MessageBoxIcon.Error);

    private void SaveInterfacePreferences()
    {
        try { preferences.Save(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Error(new SetupException("Nie udało się zapisać ustawień w config.ini obok XMLator.exe.", "Could not save the settings in config.ini next to XMLator.exe."));
        }
    }

    private DialogResult ShowMessage(Form owner, string message, MessageBoxIcon icon, bool confirm = false, string? title = null)
    {
        if (!confirm)
            return MessageBox.Show(owner, message, title ?? "XMLator", MessageBoxButtons.OK, icon);
        var (page, yes) = QuestionPage(title ?? "XMLator", message, icon);
        return TaskDialog.ShowDialog(owner, page) == yes ? DialogResult.Yes : DialogResult.No;
    }

    private (TaskDialogPage Page, TaskDialogButton Yes) QuestionPage(string title, string message, MessageBoxIcon icon)
    {
        var yes = new TaskDialogButton(L("&Tak", "&Yes"));
        var no = new TaskDialogButton(L("&Nie", "&No"));
        return (new TaskDialogPage { Caption = title, Text = message, Icon = icon == MessageBoxIcon.Warning ? TaskDialogIcon.Warning : TaskDialogIcon.Information,
            AllowCancel = true, DefaultButton = no, Buttons = { yes, no } }, yes);
    }

    private void Run(Action operation)
    {
        try { operation(); }
        catch (Exception e) { Error(e); }
    }

    private void SetBusy(bool value, string? message = null)
    {
        working = value;
        menu.Enabled = !value;
        content.Enabled = !value;
        back.Enabled = !value && step > 0;
        if (next is not null)
            next.Enabled = !value;
        UseWaitCursor = value;
        if (!value)
            HideOperationProgress();
        if (value && message is not null)
            ReportOperation(L("Postęp operacji", "Operation progress"), message);
        else
            SetHeading(StepHeading());
    }

    private void ReportOperation(string name, string caption, int? percent = null)
    {
        bool entering = !operationProgress.Visible;
        bool changedPhase = caption != operationCaption;
        int? previous = operationProgress.Style == ProgressBarStyle.Continuous ? operationProgress.Value : null;
        if (operationProgress.AccessibleName != name)
            operationProgress.AccessibleName = name;
        operationProgress.Visible = true;
        heading.Visible = false;

        var style = percent is null ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        if (operationProgress.Style != style)
            operationProgress.Style = style;
        if (percent is int value)
            operationProgress.Value = value;

        if (entering || changedPhase || previous != percent)
            SetHeading(percent is int shown ? $"{caption} — {shown}%" : caption);
        if (changedPhase)
            operationProgress.AccessibilityObject.RaiseAutomationNotification(AutomationNotificationKind.Other,
                AutomationNotificationProcessing.CurrentThenMostRecent, caption);
        operationCaption = caption;
    }

    private void HideOperationProgress()
    {
        operationProgress.Visible = false;
        operationCaption = null;
        heading.Visible = true;
    }

    private void OpenProfile()
    {
        using var dialog = new OpenFileDialog { InitialDirectory = AppContext.BaseDirectory,
            Filter = L("Profile XMLatora (*.ini)|*.ini", "XMLator profiles (*.ini)|*.ini") };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            LoadProfile(dialog.FileName);
    }

    private void LoadProfile(string path)
    {
        profile = IniProfile.Load(path);
        ResetUsbOptions();
        step = 0;
        ShowStep();
        MarkConfigurationSaved();
    }

    private byte[] ConfigurationFingerprint()
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Profile = profile,
            profile.SetupKey,
            profile.ActivationKey,
            Passwords = profile.Users.Select(user => user.Password),
            SessionOptions = profile.SessionOptions.OrderBy(entry => entry.Key, StringComparer.Ordinal)
        });
        try { return SHA256.HashData(data); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    private bool HasUnsavedConfiguration() => !ConfigurationFingerprint().AsSpan().SequenceEqual(savedConfiguration);

    private void MarkConfigurationSaved() => savedConfiguration = ConfigurationFingerprint();

    private void ConfirmClose(object? sender, FormClosingEventArgs e)
    {
        if (working)
        {
            e.Cancel = true;
            return;
        }
        if (e.CloseReason is not (CloseReason.UserClosing or CloseReason.TaskManagerClosing) || e.Cancel)
            return;
        Validate();
        if (HasUnsavedConfiguration())
            e.Cancel = ShowMessage(this, L("Masz niezapisane zmiany. Czy chcesz zamknąć XMLator bez zapisywania?",
                "You have unsaved changes. Close XMLator without saving?"), MessageBoxIcon.Warning, confirm: true,
                title: L("Niezapisane zmiany", "Unsaved changes")) != DialogResult.Yes;
    }

    private SaveFileDialog ProfileSaveDialog() => new()
    {
        Title = L("Zapisz profil XMLatora", "Save XMLator profile"),
        Filter = L("Profile XMLatora (*.ini)|*.ini", "XMLator profiles (*.ini)|*.ini"),
        InitialDirectory = AppContext.BaseDirectory,
        FileName = Path.Combine(AppContext.BaseDirectory, "XMLator.ini"),
        DefaultExt = "ini"
    };

    private void SaveProfile()
    {
        using var dialog = ProfileSaveDialog();
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            if (preferences.IsConfigurationFile(dialog.FileName))
            {
                Error(new SetupException("Wybierz inną nazwę dla profilu instalacji. Plik config.ini przechowuje język i motyw XMLatora.",
                    "Choose a different name for the installation profile. The config.ini file stores XMLator's language and theme."));
                return;
            }
            IniProfile.Save(dialog.FileName, profile);
            MarkConfigurationSaved();
            Info(L("Zapisano profil. Klucze i hasła wpiszesz przy generowaniu kolejnej instalacji.",
                "Profile saved. Enter product keys and passwords when generating the next installation."));
        }
    }
}
