using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using XMLator;

internal static class NvdaAudit
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);

    internal static void RunLocalizedPhrases(string language)
    {
        using var wizard = new Wizard(null, new InterfacePreferences { Language = language, Theme = AppTheme.Dark })
        {
            FindMicrosoftIsos = (_, _, _, _, _, _) => Task.FromResult(Array.Empty<IsoProduct>()),
            FindSiteIsos = (_, _, _) => Task.FromResult(Array.Empty<ProszczokIso>())
        };
        const BindingFlags members = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(Wizard);
        var profile = (SetupProfile)type.GetField("profile", members)!.GetValue(wizard)!;
        type.GetField("usbDisks", members)!.SetValue(wizard, Array.Empty<UsbDisk>());
        Exception? failure = null;
        wizard.Shown += async (_, _) =>
        {
            try
            {
                profile.TimeZone = "Central Europe Standard Time";
                Show(4);
                var zone = Find<ComboBox>(wizard, Name("&Strefa czasowa", "&Time zone"));
                zone.Select();
                await Task.Delay(1400);
                await Key("{UP}");
                await Key("{DOWN}");
                if (!zone.Focused || zone.SelectedItem is not Choice<string> { Value: "Central Europe Standard Time" })
                    throw new InvalidOperationException("The local time zone selection did not receive focus.");
                string zoneReading = $"{zone.AccessibleName}, {zone.Text}";
                Show(8);
                await Task.Delay(1200);
                var refresh = Find<Button>(wizard, Name("&Odśwież listę pendrive’ów", "&Refresh USB drives"));
                refresh.Select();
                await Task.Delay(2000);
                if (!refresh.Focused)
                    throw new InvalidOperationException("The localized refresh button did not receive focus.");
                Console.WriteLine($"{language}: {zoneReading}, {refresh.AccessibleName}.");
            }
            catch (Exception error) { failure = error; }
            finally { wizard.Dispose(); }
        };
        Application.Run(wizard);
        if (failure is not null)
            throw new InvalidOperationException("The localized NVDA sequence failed.", failure);

        string Name(string polish, string english) => (language == "pl" ? polish : UiText.Get(language, english))
            .Replace("&", "", StringComparison.Ordinal);
        void Show(int step)
        {
            type.GetField("step", members)!.SetValue(wizard, step);
            type.GetMethod("ShowStep", members)!.Invoke(wizard, [true]);
        }
    }

    internal static void RunInitialSetup()
    {
        using var wizard = new Wizard(null, new InterfacePreferences { Language = "pl", Theme = AppTheme.Dark });
        const BindingFlags members = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(Wizard);
        var profile = (SetupProfile)type.GetField("profile", members)!.GetValue(wizard)!;
        Exception? failure = null;
        wizard.Shown += async (_, _) =>
        {
            try
            {
                Find<Button>(wizard, "Dalej").PerformClick();
                await Pause();
                await Key("{DOWN}");
                if (!profile.AcceptEula)
                    throw new InvalidOperationException("Could not change the license selection.");
                await Key("%{F4}");
                var title = new StringBuilder(256);
                GetWindowText(GetForegroundWindow(), title, title.Capacity);
                if (title.ToString() != "Niezapisane zmiany")
                    throw new InvalidOperationException("Alt+F4 did not open the unsaved changes dialog.");
                await Key("%n");
                if (!wizard.ContainsFocus || !profile.AcceptEula)
                    throw new InvalidOperationException("The No mnemonic did not return to the unchanged license step.");

                profile.CreateUsers = true;
                profile.Users.Add(new LocalUser { Name = "OobeTest" });
                type.GetField("step", members)!.SetValue(wizard, 5);
                type.GetMethod("ShowStep", members)!.Invoke(wizard, [true]);
                Find<ComboBox>(wizard, "Jak przeprowadzić pierwszą konfigurację Windows?").Select();
                await Pause();
                await Key("{END}");
                if (profile.SelectedFirstRun != FirstRunMode.Offline)
                    throw new InvalidOperationException("The offline local account choice was not selectable by keyboard.");
                await ToggleTwice(Find<CheckBox>(wizard, "Udostępnij ręczną konfigurację bez internetu (BypassNRO)"));
                Console.WriteLine("Alt+F4 opened the native dialog and Alt+N returned to editing. Keyboard selected offline setup and toggled both BypassNRO states.");
            }
            catch (Exception error) { failure = error; }
            finally { wizard.Dispose(); }
        };
        Application.Run(wizard);
        if (failure is not null)
            throw new InvalidOperationException("The initial setup keyboard audit failed.", failure);
    }

    internal static void RunDiskAndIso(string incompatibleIso)
    {
        using var wizard = new Wizard(null, new InterfacePreferences { Language = "pl", Theme = AppTheme.Dark })
        {
            FindMicrosoftIsos = (_, _, _, _, _, _) => Task.FromResult(Array.Empty<IsoProduct>()),
            FindSiteIsos = (_, _, _) => Task.FromResult(Array.Empty<ProszczokIso>())
        };
        const BindingFlags members = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(Wizard);
        var profile = (SetupProfile)type.GetField("profile", members)!.GetValue(wizard)!;
        var step = type.GetField("step", members)!;
        var show = type.GetMethod("ShowStep", members)!;
        type.GetField("usbDisks", members)!.SetValue(wizard, new[] { new UsbDisk(71, 32L << 30, "test-bus", "test-serial", "USB Test", "") });
        Exception? failure = null;
        wizard.Shown += async (_, _) =>
        {
            try
            {
                step.SetValue(wizard, 3);
                profile.TargetName = "Windows 10";
                show.Invoke(wizard, [true]);
                var boot = Find<ComboBox>(wizard, "Tryb rozruchu instalatora");
                var partition = Find<ComboBox>(wizard, "Układ partycji");
                await SelectLegacyLayout(wizard, DiskMode.Single);
                if (!Find<ListBox>(wizard, "Plan partycji w kolejności na dysku").Items.Cast<string>()
                    .Any(line => line.Contains("Windows i rozruch (C:)")))
                    throw new InvalidOperationException("The disk plan does not describe the single active C.");
                boot.Select();
                await Key("{UP}");
                if (!boot.Focused || profile.DiskMode != DiskMode.Interactive ||
                    partition.SelectedItem is not Choice<DiskMode> { Value: DiskMode.Interactive })
                    throw new InvalidOperationException("Selecting UEFI did not keep focus and restore manual partition selection.");
                Console.WriteLine("The boot mode comes first. Legacy BIOS exposes a single active C, and returning to UEFI restores manual selection.");

                profile.TargetName = "Windows 11";
                profile.ImageMode = ImageMode.Edition;
                profile.PresetEdition = "Core";
                step.SetValue(wizard, 8);
                show.Invoke(wizard, [true]);
                var drive = Find<ComboBox>(wizard, "Pendrive");
                drive.Select();
                await Key("{DOWN}");
                Find<TextBox>(wizard, "Ścieżka do obrazu ISO Windows").Text = incompatibleIso;
                await Pause();
                var create = Find<Button>(wizard, "Utwórz bootowalny pendrive");
                if (!create.Enabled)
                    throw new InvalidOperationException("USB creation did not recognize the local ISO.");
                create.Select();
                await Key(" ");
                var title = new StringBuilder(256);
                GetWindowText(GetForegroundWindow(), title, title.Capacity);
                if (title.ToString() != "Błąd tworzenia pendrive’a")
                    throw new InvalidOperationException("Expected the ISO mismatch before preparing or formatting the USB drive, got: " + title);
                await Task.Delay(2500);
                await Key("{ENTER}");
                if (!create.Enabled || !Find<Button>(wizard, "Zapisz XML…").Enabled)
                    throw new InvalidOperationException("The wizard did not restore the file and USB actions after the mismatch.");
                Console.WriteLine("Windows 11 Enterprise Evaluation was rejected with a native message before elevation for the Home profile.");
            }
            catch (Exception error) { failure = error; }
            finally { wizard.Dispose(); }
        };
        Application.Run(wizard);
        if (failure is not null)
            throw new InvalidOperationException("The disk and ISO keyboard audit failed.", failure);
    }

    internal static void RunFirstSignIn()
    {
        using var wizard = new Wizard(null, new InterfacePreferences { Language = "pl", Theme = AppTheme.Dark })
        {
            FindMicrosoftIsos = (_, _, _, _, _, _) => Task.FromResult(Array.Empty<IsoProduct>()),
            FindSiteIsos = (_, _, _) => Task.FromResult(Array.Empty<ProszczokIso>())
        };
        const BindingFlags members = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(Wizard).GetField("usbDisks", members)!.SetValue(wizard, Array.Empty<UsbDisk>());
        Exception? failure = null;
        wizard.Shown += async (_, _) =>
        {
            try
            {
                typeof(Wizard).GetField("step", members)!.SetValue(wizard, 5);
                typeof(Wizard).GetMethod("ShowStep", members)!.Invoke(wizard, [true]);
                await Pause();
                var create = Find<ComboBox>(wizard, "Czy utworzyć konta lokalne podczas instalacji?");
                if (!create.Focused)
                    throw new InvalidOperationException("The accounts page did not focus the account creation choice.");
                await Key("{DOWN}");
                var add = Find<Button>(wizard, "Dodaj konto…");
                add.Select();
                await Pause();
                await Key(" ");
                var accountDialog = Application.OpenForms.Cast<Form>().Single(form => form.Modal && form.Text == "Konto lokalne");
                if (!Find<TextBox>(accountDialog, "Nazwa konta").Focused)
                    throw new InvalidOperationException("The account editor did not focus the account name.");
                await Key("ProbaLogowania");
                await Key("{ENTER}");
                var signIn = Find<ComboBox>(wizard, "Pierwsze logowanie");
                signIn.Select();
                await Pause();
                if (signIn.Items.Count != 2 || !signIn.Focused)
                    throw new InvalidOperationException("Adding a local administrator did not enable automatic sign-in.");
                await Key("{DOWN}");
                if (signIn.SelectedItem is not Choice<LogonMode> { Value: LogonMode.FirstAdministrator })
                    throw new InvalidOperationException("Arrow Down did not select one-time sign-in.");
                await Key("^{END}");
                var review = Find<ListBox>(wizard, "Podsumowanie ustawień");
                if (!review.Items.Contains("Jednorazowe logowanie — ProbaLogowania."))
                    throw new InvalidOperationException("The review did not name the automatic sign-in account.");
                var preview = Find<Button>(wizard, "Podejrzyj plik XML…");
                preview.Select();
                await Pause();
                await Key(" ");
                var xml = Application.OpenForms.Cast<Form>().Single(form => form.Modal && form.Text == AnswerFile.DefaultName);
                var lines = Find<ListBox>(xml, "Wiersze pliku Autounattend.xml");
                if (!lines.Focused || !lines.Items.Cast<string>().Any(line => line.Contains("<AutoLogon>")))
                    throw new InvalidOperationException("The XML preview did not contain the one-time sign-in settings.");
                await Key("{ESC}");
                Console.WriteLine("NVDA keyboard audit created a local administrator with a blank password, selected one-time sign-in, and opened the XML preview.");
            }
            catch (Exception error) { failure = error; }
            finally { wizard.Dispose(); }
        };
        Application.Run(wizard);
        if (failure is not null)
            throw new InvalidOperationException("The NVDA sign-in sequence failed.", failure);
    }

    internal static void RunInterface()
    {
        var preferences = new InterfacePreferences(windowsCulture: System.Globalization.CultureInfo.GetCultureInfo("pl-PL"));
        using var wizard = new Wizard(null, preferences);
        Exception? failure = null;
        wizard.Shown += async (_, _) =>
        {
            try
            {
                await Pause();
                if (!Find<ComboBox>(wizard, "Który Windows chcesz zainstalować?").Focused)
                    throw new InvalidOperationException("Keyboard focus must start at the Windows version.");
                var settings = Find<Button>(wizard, "Ustawienia…");
                for (int i = 0; i < 8 && !settings.Focused; i++)
                    await Key("{TAB}");
                if (!settings.Focused)
                    throw new InvalidOperationException("Tab did not reach Settings.");
                await Key(" ");
                var dialog = Application.OpenForms.Cast<Form>().Single(form => form.Modal && form.Text == "Ustawienia");
                var selector = Find<ComboBox>(dialog, "Język interfejsu XMLatora");
                if (!selector.Focused || selector.Text != "Zgodny z językiem Windows")
                    throw new InvalidOperationException("Settings did not focus the language selector.");
                await Key("{DOWN}");
                await Key("{DOWN}");
                await Key("{DOWN}");
                if (preferences.EffectiveLanguage != "pl" || selector.Text != "English")
                    throw new InvalidOperationException("The language choice was not pending until OK.");
                await Key("{TAB}");
                var themes = Find<ComboBox>(dialog, "Motyw interfejsu XMLatora");
                if (!themes.Focused)
                    throw new InvalidOperationException("Tab did not reach the theme list.");
                await Key("{DOWN}");
                await Key("{DOWN}");
                if (preferences.Theme != AppTheme.System || themes.Text != "Ciemny")
                    throw new InvalidOperationException("The theme choice was not pending until OK.");
                await Key("{ENTER}");
                if (preferences.EffectiveLanguage != "en" || preferences.Theme != AppTheme.Dark ||
                    !wizard.Text.Contains("Windows version") || !Find<Button>(wizard, "Settings…").Focused)
                    throw new InvalidOperationException("OK did not apply the choices and return to Settings.");
                await Key(" ");
                dialog = Application.OpenForms.Cast<Form>().Single(form => form.Modal && form.Text == "Settings");
                if (!Find<ComboBox>(dialog, "XMLator interface language").Focused ||
                    Find<ComboBox>(dialog, "XMLator interface theme").Text != "Dark")
                    throw new InvalidOperationException("Settings did not reopen with the selected theme and language focus.");
                await Key("{UP}");
                await Key("{ESC}");
                if (preferences.EffectiveLanguage != "en" || !Find<Button>(wizard, "Settings…").Focused)
                    throw new InvalidOperationException("Escape did not cancel and restore focus.");
                Console.WriteLine("Keyboard opened Settings, applied English and Dark, reopened, and canceled with Escape. Focus returned to Settings after both closings.");
            }
            catch (Exception error) { failure = error; }
            finally { wizard.Dispose(); }
        };
        Application.Run(wizard);
        if (failure is not null)
            throw new InvalidOperationException("The scripted NVDA language and theme sequence failed.", failure);
    }

    internal static void RunIsoSources()
    {
        var microsoft = new TaskCompletionSource<IsoProduct[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var wizard = new Wizard(null, new InterfacePreferences { Language = "pl" })
        {
            FindMicrosoftIsos = (_, _, _, _, cancel, _) => microsoft.Task.WaitAsync(cancel),
            FindSiteIsos = (_, _, _) => Task.FromResult(ProszczokIsoCatalog.Parse("<a href='Windows%2011%2026H2.iso'>ISO</a>"))
        };
        typeof(Wizard).GetField("usbDisks", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(wizard, Array.Empty<UsbDisk>());
        Exception? failure = null;
        wizard.Shown += async (_, _) =>
        {
            try
            {
                await Pause();
                await Key("^{END}");
                var source = Find<ComboBox>(wizard, "Źródło obrazu ISO");
                if (!source.Focus() || source.Items.Count != 2)
                    throw new InvalidOperationException("The site and local options should be accessible during the Microsoft check.");
                await Pause();
                microsoft.SetResult([MicrosoftIsoCatalog.Products.Single(product => product.Version == "Windows 11")]);
                await Task.Delay(1500);
                if (!source.Focused || source.SelectedItem is not Choice<IsoSource> { Value: IsoSource.Local } || source.Items.Count != 3)
                    throw new InvalidOperationException("Microsoft should become available with the original focus and selection.");
                await Key("{DOWN}");
                if (source.SelectedItem is not Choice<IsoSource> { Value: IsoSource.Microsoft })
                    throw new InvalidOperationException("Down did not select the new Microsoft option.");
                await Key("{TAB}");
                if (!Find<ComboBox>(wizard, "Wydanie ISO (Home / Pro / Education)").Focused)
                    throw new InvalidOperationException("Tab did not reach the Microsoft ISO release.");
                Console.WriteLine("The delayed Microsoft source preserved focus, was selectable by keyboard and exposed the release field.");
            }
            catch (Exception error) { failure = error; }
            finally { wizard.Dispose(); }
        };
        Application.Run(wizard);
        if (failure is not null)
            throw new InvalidOperationException("The NVDA ISO source sequence failed.", failure);
    }

    internal static void RunProszczok()
    {
        using var wizard = new Wizard(null);
        typeof(Wizard).GetMethod("ChangeLanguage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(wizard, ["pl"]);
        Exception? failure = null;
        wizard.Shown += async (_, _) =>
        {
            try
            {
                await Pause();
                var images = await OpenCatalog();
                await WaitFor(() => images.Items.Count >= 2 && images.Text.EndsWith(".iso", StringComparison.OrdinalIgnoreCase));
                string first = images.Text;
                await Key("{DOWN}");
                if (!images.Focused || images.Text == first ||
                    images.Items.Cast<object>().Any(item => item is not Choice<ProszczokIso?> choice || choice.Value?.Target.Windows11 != true))
                    throw new InvalidOperationException("The ISO list did not offer and select a second Windows 11 file.");
                Console.WriteLine($"Windows 11 ISO changed by keyboard: {first} -> {images.Text}");
                await Key("{TAB}");
                if (!Find<Button>(wizard, "Pobierz i użyj ISO…").Focused)
                    throw new InvalidOperationException("Tab did not reach the download button.");
                await Key("{TAB}");
                if (!Find<Button>(wizard, "Odśwież źródła ISO").Focused)
                    throw new InvalidOperationException("Tab did not reach the refresh button.");
                string selected = images.Text;
                await Key(" ");
                await WaitForScan();
                if (images.Text != selected || !Find<Button>(wizard, "Odśwież źródła ISO").Focused)
                    throw new InvalidOperationException("Refreshing changed the selected image or moved keyboard focus.");

                typeof(Wizard).GetMethod("ChangeStep", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(wizard, [-8]);
                var windows = Find<ComboBox>(wizard, "Który Windows chcesz zainstalować?");
                windows.Select();
                await Pause();
                await Key("{DOWN}");
                await Key("{DOWN}");
                if (windows.Text != "Windows 8.1")
                    throw new InvalidOperationException("Could not select Windows 8.1.");
                await Key("^{END}");
                await WaitForScan();
                if (FindControls(wizard).OfType<ComboBox>().Any(combo => combo.Visible && combo.AccessibleName == "Źródło obrazu ISO"))
                    throw new InvalidOperationException("Windows 8.1 with empty catalogs must only offer a local path.");
                var local = Find<TextBox>(wizard, "Ścieżka do obrazu ISO Windows");
                local.Select();
                await Pause();
                await Key("{TAB}");
                if (!Find<Button>(wizard, "Przeglądaj pliki ISO…").Focused)
                    throw new InvalidOperationException("Tab did not reach the local ISO browse button.");
                Console.WriteLine("Windows 8.1 only offered the local ISO path and browse button.");

                var changeStep = typeof(Wizard).GetMethod("ChangeStep", BindingFlags.Instance | BindingFlags.NonPublic)!;
                changeStep.Invoke(wizard, [-8]);
                Find<ComboBox>(wizard, "Który Windows chcesz zainstalować?").Select();
                await Pause();
                await Key("{UP}");
                if (Find<ComboBox>(wizard, "Który Windows chcesz zainstalować?").Text != "Windows 10")
                    throw new InvalidOperationException("Could not select Windows 10.");
                changeStep.Invoke(wizard, [3]);
                await SelectLegacyLayout(wizard, DiskMode.Single);
                await Key("{TAB}");
                if (wizard.ActiveControl is not NumericUpDown && !FindControls(wizard).OfType<NumericUpDown>().Any(field => field.ContainsFocus))
                    throw new InvalidOperationException("Tab did not reach the disk number directly.");
                Console.WriteLine("Keyboard selected single active C and skipped the redundant firmware selector.");
            }
            catch (Exception error) { failure = error; }
            finally { wizard.Dispose(); }
        };
        Application.Run(wizard);
        if (failure is not null)
            throw new InvalidOperationException("The scripted proszczok.pl NVDA sequence failed.", failure);

        async Task<ComboBox> OpenCatalog()
        {
            await Key("^{END}");
            await WaitForScan();
            var source = Find<ComboBox>(wizard, "Źródło obrazu ISO");
            source.Select();
            await Pause();
            if (source.Items.Count > 2)
                await Key("{DOWN}");
            await Key("{DOWN}");
            if (!source.Focused || source.Text != "Pobierz ISO z proszczok.pl")
                throw new InvalidOperationException("Could not select the proszczok.pl source.");
            await Key("{TAB}");
            var images = Find<ComboBox>(wizard, "Plik ISO z proszczok.pl");
            if (!images.Focused)
                throw new InvalidOperationException("Tab did not reach the ISO file list.");
            return images;
        }

        static async Task WaitFor(Func<bool> completed)
        {
            for (int i = 0; i < 165 && !completed(); i++)
                await Task.Delay(200);
            if (!completed())
                throw new InvalidOperationException("The proszczok.pl catalog did not show the expected files or status.");
        }

        Task WaitForScan() => WaitFor(() => !FindControls(wizard).OfType<Label>()
            .Any(label => label.Visible && label.Text == "Sprawdzanie dostępności obrazów ISO…"));
    }

    internal static void RunIso()
    {
        using var temporaryIso = new TemporaryIso();
        using var wizard = new Wizard(null);
        const BindingFlags members = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(Wizard).GetMethod("ChangeLanguage", members)!.Invoke(wizard, ["pl"]);
        typeof(Wizard).GetField("usbDisks", members)!.SetValue(wizard,
            new[] { new UsbDisk(999, 32L << 30, "XMLator test fixture", "XMLator test ID", "Test USB", "") });
        Exception? failure = null;
        wizard.Shown += async (_, _) =>
        {
            try
            {
                await Pause();
                await Key("^{END}");
                for (int retry = 0; retry < 160 && !FindControls(wizard).OfType<ComboBox>()
                    .Any(combo => combo.Visible && combo.Items.Cast<object>().Any(item => item.ToString() == "Pobierz ISO z Microsoftu")); retry++)
                    await Task.Delay(200);
                var source = Find<ComboBox>(wizard, "Źródło obrazu ISO");
                source.Select();
                await Pause();
                await Key("{DOWN}");
                Verify(source, "Pobierz ISO z Microsoftu");
                await Key("{TAB}");
                var release = Find<ComboBox>(wizard, "Wydanie ISO (Home / Pro / Education)");
                if (!release.Focused || !release.Text.EndsWith("H2"))
                    throw new InvalidOperationException("Tab did not reach the verified ISO release.");
                await Key("{TAB}");
                var language = Find<ComboBox>(wizard, "Język obrazu ISO");
                Verify(language, "Polski (Polska)");
                await Key("{DOWN}");
                Verify(language, "Angielski (Stany Zjednoczone)");
                Console.WriteLine("Keyboard navigation reached the ISO source, release and language.");
                var saveXml = Find<Button>(wizard, "Zapisz XML…");
                var saveProfile = Find<Button>(wizard, "Zapisz profil INI…");
                var createUsb = Find<Button>(wizard, "Utwórz bootowalny pendrive");
                var drive = Find<ComboBox>(wizard, "Pendrive");
                saveXml.Select();
                await Pause();
                await Key("{TAB}");
                if (!saveProfile.Focused)
                    throw new InvalidOperationException("Tab did not move from Save XML to Save INI profile.");
                await Key("{TAB}");
                if (!drive.Focused || createUsb.Enabled)
                    throw new InvalidOperationException("Tab did not move to the USB drive list with the create button disabled.");
                await Key("{DOWN}");
                if (drive.SelectedIndex != 1 || createUsb.Enabled)
                    throw new InvalidOperationException("A drive selection alone must leave USB creation disabled.");
                source.Select();
                await Key("{HOME}");
                await Key("{TAB}");
                var path = Find<TextBox>(wizard, "Ścieżka do obrazu ISO Windows");
                if (!path.Focused)
                    throw new InvalidOperationException("Tab did not move from ISO source to local ISO path.");
                path.Text = temporaryIso.Path;
                for (int i = 0; i < 20 && !createUsb.Enabled; i++)
                    await Task.Delay(100);
                if (!createUsb.Enabled)
                    throw new InvalidOperationException("USB creation was unavailable with both inputs selected.");
                for (int i = 0; i < 6 && !createUsb.Focused; i++)
                    await Key("{TAB}");
                if (!createUsb.Focused)
                    throw new InvalidOperationException("Tab did not reach the enabled USB create button.");
                Console.WriteLine("Keyboard navigation reached both file actions, the USB drive, the ISO path and the create button in order.");
            }
            catch (Exception error) { failure = error; }
            finally { wizard.Dispose(); }
        };
        Application.Run(wizard);
        if (failure is not null)
            throw new InvalidOperationException("The scripted NVDA ISO sequence failed.", failure);

        static void Verify(ComboBox field, string value)
        {
            if (!field.Focused || field.Text != value)
                throw new InvalidOperationException($"Expected {value} with focus on {field.AccessibleName}. Got {field.Text}.");
        }
    }

    internal static void Run()
    {
        using var wizard = new Wizard(null);
        typeof(Wizard).GetMethod("ChangeLanguage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(wizard, ["pl"]);
        Exception? failure = null;
        wizard.Shown += async (_, _) =>
        {
            try
            {
                await Task.Delay(1500);
                foreach (string menu in new[] { "%p", "%j", "%w", "%c" })
                {
                    await Key(menu);
                    await Key("{DOWN}");
                    await Key("{ESC}");
                    await Key("{ESC}");
                }
                var windows = Find<ComboBox>(wizard, "Który Windows chcesz zainstalować?");
                windows.Select();
                await Pause();
                await Key("{DOWN}");
                if (windows.Text != "Windows 10")
                    throw new InvalidOperationException("The Windows list did not change to Windows 10.");
                await Key("{TAB}");
                var architecture = Find<ComboBox>(wizard, "Architektura obrazu instalacyjnego");
                if (!architecture.Focused || architecture.Text != "x64 (AMD64)")
                    throw new InvalidOperationException("Tab did not reach the current x64 architecture.");
                await Key("{DOWN}");
                if (architecture.Text != "x86 (32-bit)")
                    throw new InvalidOperationException("The architecture did not change to x86.");
                await Key("+{TAB}");
                await Key("{TAB}");
                if (!architecture.Focused || architecture.Text != "x86 (32-bit)")
                    throw new InvalidOperationException("Returning to the architecture list did not retain x86.");
                await Key("{UP}");
                if (architecture.Text != "x64 (AMD64)")
                    throw new InvalidOperationException("The architecture did not change back to x64.");
                await ToggleTwice(Find<CheckBox>(wizard, "Dodaj architekturę x86 (32-bit) do tego samego XML"));
                for (int step = 1; step < 5; step++)
                {
                    Find<Button>(wizard, "Dalej").PerformClick();
                    await Pause();
                }
                var owner = Find<CheckBox>(wizard, "Ustaw właściciela, organizację lub grupę roboczą");
                await ToggleTwice(owner);
                for (int step = 5; step < 7; step++)
                {
                    Find<Button>(wizard, "Dalej").PerformClick();
                    await Pause();
                }
                foreach (string caption in new[]
                {
                    "Eksplorator, Start i pasek zadań…", "System, zabezpieczenia i aktualizacje…", "Sieć Wi-Fi i maszyny wirtualne…",
                    "Personalizacja i ułatwienia dostępu…", "Zasady haseł i blokady konta…",
                    "Własne składniki XML…", "Aplikacje i funkcje do usunięcia…"
                })
                    await VisitDialog(caption);
                await Key("^{END}");
                await VisitDialog("Podejrzyj plik XML…");
                Console.WriteLine("NVDA keyboard sequence checked all four menus, Windows and architecture lists, three checkbox states and the advanced dialogs.");
            }
            catch (Exception error) { failure = error; }
            finally { wizard.Dispose(); }
        };
        Application.Run(wizard);
        if (failure is not null)
            throw new InvalidOperationException("The scripted NVDA focus and keyboard sequence failed.", failure);

        async Task VisitDialog(string caption)
        {
            var inspected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            wizard.BeginInvoke(async () =>
            {
                await Pause();
                var dialog = Application.OpenForms.Cast<Form>().SingleOrDefault(form => form != wizard);
                try
                {
                    if (dialog is null || !dialog.ContainsFocus)
                        throw new InvalidOperationException("No focused dialog for " + caption);
                    if (caption == "Aplikacje i funkcje do usunięcia…")
                        await ToggleTwice(Find<CheckBox>(dialog, "Microsoft Store — zalecamy zachować"));
                    else
                        await Key("{TAB}");
                    inspected.TrySetResult();
                }
                catch (Exception error) { inspected.TrySetException(error); }
                finally { dialog?.Close(); }
            });
            var button = Find<Button>(wizard, caption);
            button.Select();
            button.PerformClick();
            await inspected.Task;
            await Pause();
        }
    }

    private static async Task ToggleTwice(CheckBox box)
    {
        if (!box.Focus() || box.Checked)
            throw new InvalidOperationException($"Could not focus the unchecked control {box.AccessibleName}.");
        await Pause();
        await Key(" ");
        if (!box.Checked)
            throw new InvalidOperationException($"Space did not check {box.AccessibleName}.");
        await Key(" ");
        if (box.Checked)
            throw new InvalidOperationException($"Space did not uncheck {box.AccessibleName}.");
    }

    private static async Task Key(string key)
    {
        SendKeys.Send(key);
        await Pause();
    }

    private static async Task SelectLegacyLayout(Form wizard, DiskMode layout)
    {
        var boot = Find<ComboBox>(wizard, "Tryb rozruchu instalatora");
        var partition = Find<ComboBox>(wizard, "Układ partycji");
        if (!boot.Focused || partition.Items.OfType<Choice<DiskMode>>().Any(choice => choice.Value == layout))
            throw new InvalidOperationException("UEFI must start focused and hide the BIOS-only layout.");
        await Pause();
        await Key("{DOWN}");
        if (!boot.Focused || boot.SelectedItem is not Choice<Firmware> { Value: Firmware.Mbr })
            throw new InvalidOperationException("The first field did not select Legacy BIOS.");
        await Key("{TAB}");
        if (!partition.Focused)
            throw new InvalidOperationException("Tab did not reach the partition list.");
        int index = partition.Items.OfType<Choice<DiskMode>>().ToList().FindIndex(choice => choice.Value == layout);
        if (index < 0)
            throw new InvalidOperationException("The Legacy-only disk layout did not appear.");
        for (int i = 0; i < index; i++)
            await Key("{DOWN}");
        if (partition.SelectedItem is not Choice<DiskMode> selected || selected.Value != layout)
            throw new InvalidOperationException("The partition list did not select the Legacy layout.");
    }

    private static Task Pause() => Task.Delay(675);

    private static T Find<T>(Control root, string name) where T : Control => FindControls(root).OfType<T>()
        .Single(control => control.Visible && control.AccessibleName == name);

    private static IEnumerable<Control> FindControls(Control root) => root.Controls.Cast<Control>()
        .SelectMany(control => new[] { control }.Concat(FindControls(control)));
}
