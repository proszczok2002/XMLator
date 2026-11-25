using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Automation;
using System.Xml.Linq;
using Accessibility;
using XMLator;

internal static partial class SelectionAudit
{
    internal static void VerifyClose(string executable)
    {
        var folder = Directory.CreateTempSubdirectory("XMLator-close-audit-");
        string binary = Path.Combine(folder.FullName, "XMLator.exe");
        string config = Path.Combine(folder.FullName, "config.ini");
        File.Copy(executable, binary);
        try
        {
            foreach (var language in UiText.Languages)
            {
                File.WriteAllLines(config, ["[Interface]", $"Language={language.Value}", "Theme=dark"]);
                using var app = Process.Start(binary)!;
                try
                {
                    var window = WaitForWizard(app);
                    var owner = new IntPtr(window.Current.NativeWindowHandle);
                    string next = Local("Dalej", "&Next");
                    ((InvokePattern)Find(window, ControlType.Button, next, next).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    string label = Local("Czy instalator ma zaakceptować warunki licencji?", "Automatically &accept the license terms?");
                    var license = Find(window, ControlType.ComboBox, label, label);
                    SendArrow(license);
                    string selected = Local("Tak. Akceptuję warunki licencji używanego nośnika.", "Yes. I accept the license terms for the installation media.");
                    if (!SpinWait.SpinUntil(() => ReadValue(license) == selected, 2500))
                        throw new InvalidOperationException("The license selection did not change.");

                    var first = RequestClose(viaWindow: true);
                    string no = Local("Nie", "&No");
                    var decline = Find(first, ControlType.Button, no, no);
                    if (!decline.Current.HasKeyboardFocus)
                        throw new InvalidOperationException("The close dialog must default to keeping the work open.");
                    ((InvokePattern)decline.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    if (!SpinWait.SpinUntil(() => GetLastActivePopup(owner) == owner && window.Current.IsEnabled, 3000) || ReadValue(license) != selected)
                        throw new InvalidOperationException("Canceling closure did not preserve the current step and selection.");

                    var second = RequestClose(viaWindow: false);
                    string yes = Local("Tak", "&Yes");
                    ((InvokePattern)Find(second, ControlType.Button, yes, yes).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    if (!app.WaitForExit(4000))
                        throw new InvalidOperationException("Confirming closure did not exit the wizard.");

                    AutomationElement RequestClose(bool viaWindow)
                    {
                        const uint sysCommand = 0x0112;
                        const int sysClose = 0xF060;
                        if (!(viaWindow ? app.CloseMainWindow() : PostMessage(owner, sysCommand, new IntPtr(sysClose), IntPtr.Zero)))
                            throw new InvalidOperationException("Could not send the close request.");
                        string title = Local("Niezapisane zmiany", "Unsaved changes");
                        IntPtr popup = IntPtr.Zero;
                        if (!SpinWait.SpinUntil(() =>
                        {
                            const uint enabledPopup = 6;
                            popup = GetWindow(owner, enabledPopup);
                            return popup != owner && popup != IntPtr.Zero && AutomationElement.FromHandle(popup).Current.Name == title;
                        }, 5000))
                            throw new InvalidOperationException($"The localized unsaved-changes dialog did not appear ({language.Value}, exited: {app.HasExited}).");
                        var dialog = AutomationElement.FromHandle(popup);
                        string message = Local("Masz niezapisane zmiany. Czy chcesz zamknąć XMLator bez zapisywania?",
                            "You have unsaved changes. Close XMLator without saving?");
                        Find(dialog, ControlType.Text, message, message);
                        return dialog;
                    }
                    Console.WriteLine($"{language.Value}: native close dialog, translated text, safe default, keep editing and confirm exit.");
                }
                finally
                {
                    if (!app.HasExited)
                    {
                        app.Kill();
                        app.WaitForExit();
                    }
                }

                using var untouched = Process.Start(binary)!;
                WaitForWizard(untouched);
                if (!untouched.CloseMainWindow() || !untouched.WaitForExit(3500))
                {
                    untouched.Kill();
                    untouched.WaitForExit();
                    throw new InvalidOperationException("The untouched wizard should close without a warning.");
                }

                if (language.Value is "pl" or "en")
                    VerifySavedClose(profile: language.Value == "en");

                string Local(string pl, string en) => (language.Value == "pl" ? pl : UiText.Get(language.Value, en)).Replace("&", "");

                void VerifySavedClose(bool profile)
                {
                    using var saved = Process.Start(binary)!;
                    try
                    {
                        var window = WaitForWizard(saved);
                        var owner = new IntPtr(window.Current.NativeWindowHandle);
                        for (int step = 0; step < 8; step++)
                        {
                            if (step == 1)
                            {
                                string label = Local("Czy instalator ma zaakceptować warunki licencji?", "Automatically &accept the license terms?");
                                SendArrow(Find(window, ControlType.ComboBox, label, label));
                            }
                            string next = Local("Dalej", "&Next");
                            ((InvokePattern)Find(window, ControlType.Button, next, next).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                        }
                        string name = profile ? "XMLator.ini" : AnswerFile.DefaultName;
                        string path = Path.Combine(folder.FullName, name);
                        string button = profile ? Local("Zapisz profil INI…", "Save INI &profile…") : Local("Zapisz XML…", "&Save XML…");
                        string title = profile ? Local("Zapisz profil XMLatora", "Save XMLator profile") : Local("Zapisz plik odpowiedzi XML", "Save the XML answer file");
                        SaveThroughDialog(owner, Find(window, ControlType.Button, button, button), title, name, path);
                        if (!SpinWait.SpinUntil(() => File.Exists(path) && GetLastActivePopup(owner) != owner, 5000))
                            throw new InvalidOperationException("The save did not complete.");
                        IntPtr popup = IntPtr.Zero;
                        if (!SpinWait.SpinUntil(() =>
                        {
                            popup = GetLastActivePopup(owner);
                            if (popup == owner || popup == IntPtr.Zero)
                                return false;
                            try { return AutomationElement.FromHandle(popup).Current.Name == "XMLator"; }
                            catch (ElementNotAvailableException) { return false; }
                        }, 5000))
                            throw new InvalidOperationException("The successful save dialog did not appear.");
                        ((InvokePattern)Find(AutomationElement.FromHandle(popup), ControlType.Button, "OK", "OK")
                            .GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                        if (!SpinWait.SpinUntil(() => window.Current.IsEnabled, 3000) || !saved.CloseMainWindow() || !saved.WaitForExit(4000))
                            throw new InvalidOperationException("The wizard should close without a warning after saving.");
                        Console.WriteLine($"{language.Value}: editing and saving {name} cleared the close warning.");
                    }
                    finally
                    {
                        if (!saved.HasExited)
                        {
                            saved.Kill();
                            saved.WaitForExit();
                        }
                    }
                }
            }
        }
        finally { folder.Delete(recursive: true); }
    }

    internal static void IsoSources(string executable, string version, string architecture)
    {
        using var app = Process.Start(Path.GetFullPath(executable)) ?? throw new InvalidOperationException("Could not launch XMLator.");
        try
        {
            var window = WaitForWizard(app);
            OpenIsoReview(window, version, architecture);
            var timer = Stopwatch.StartNew();
            Find(window, ControlType.Edit, "Ścieżka do obrazu ISO Windows", "Windows ISO path");
            Console.WriteLine($"{version}, {architecture}: local ISO field is available.");
            var source = WaitForSource(window, "Pobierz ISO z Microsoftu", "Download an ISO from Microsoft");
            var release = Find(window, ControlType.ComboBox, "Wydanie ISO (Home / Pro / Education)", "ISO release (Home / Pro / Education)");
            if (string.IsNullOrEmpty(ReadValue(release)))
                throw new InvalidOperationException("Microsoft was listed without a release.");
            Console.WriteLine($"Microsoft {ReadValue(release)} appeared after {timer.Elapsed.TotalSeconds:F1}s without refreshing.");
            if (architecture == "x64 (AMD64)")
            {
                WaitForSource(window, "Pobierz ISO z proszczok.pl", "Download an ISO from proszczok.pl");
                Console.WriteLine("The proszczok.pl source is available alongside Microsoft.");
            }
        }
        finally
        {
            if (!app.HasExited && !(app.CloseMainWindow() && app.WaitForExit(2500)))
                app.Kill();
        }
    }

    internal static void ExportVmAnswer(string executable, string profile, string output)
    {
        var launch = new ProcessStartInfo(Path.GetFullPath(executable));
        launch.ArgumentList.Add(Path.GetFullPath(profile));
        using var app = Process.Start(launch) ?? throw new InvalidOperationException("Could not launch XMLator.");
        try
        {
            var window = WaitForWizard(app);
            for (int i = 0; i < 8; i++)
                ((InvokePattern)Find(window, ControlType.Button, "Dalej", "Next").GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            var approval = Find(window, ControlType.CheckBox, "Potwierdzam usunięcie wszystkich partycji z wybranego dysku",
                "Confirm deleting all partitions on the selected disk");
            VerifyCheckBox(approval, false);
            var save = Find(window, ControlType.Button, "Zapisz XML…", "Save XML…");
            bool polish = save.Current.Name.StartsWith("Zapisz", StringComparison.Ordinal);
            SaveThroughDialog(app.MainWindowHandle, save, polish ? "Zapisz plik odpowiedzi XML" : "Save the XML answer file",
                AnswerFile.DefaultName, output);
            if (!SpinWait.SpinUntil(() => File.Exists(output), 7000))
                throw new InvalidOperationException("The EXE did not save the VM answer file.");
            Console.WriteLine("The packaged EXE exported the VM answer through the native Save XML dialog.");
        }
        finally
        {
            if (!app.HasExited && !(app.CloseMainWindow() && app.WaitForExit(2500)))
                app.Kill();
        }
    }

    internal static void BootMode(string executable, string output)
    {
        output = Directory.CreateDirectory(output).FullName;
        string fixture = Directory.CreateDirectory(Path.Combine(output, "portable")).FullName;
        string binary = Path.Combine(fixture, "XMLator.exe");
        string ini = Path.Combine(output, "Windows11.ini");
        File.Copy(executable, binary);
        new InterfacePreferences(Path.Combine(fixture, "config.ini"))
        {
            Language = "pl", Theme = AppTheme.Dark, AutomaticUpdates = false
        }.Save();
        var profile = new SetupProfile
        {
            TargetName = "Windows 11", AcceptEula = true, ImageMode = ImageMode.Index, WindowsMiB = 65536, KeyMode = KeyMode.Skip,
            FirstRun = FirstRunMode.SkipSupported, CreateUsers = true, Logon = LogonMode.FirstAdministrator,
            Users = [new LocalUser { Name = "XMLatorLab" }]
        };
        LocaleCatalog.ApplyLanguage(profile, "en-US");
        IniProfile.Save(ini, profile);
        var launch = new ProcessStartInfo(binary);
        launch.ArgumentList.Add(ini);
        using var app = Process.Start(launch)!;
        try
        {
            var window = WaitForWizard(app);
            Navigate(1, 4);
            var boot = Find(window, ControlType.ComboBox, "Tryb rozruchu instalatora", "Installer boot mode");
            if (!SpinWait.SpinUntil(() => boot.Current.HasKeyboardFocus && ReadValue(boot) == "UEFI / GPT", 3000))
                throw new InvalidOperationException("The first focus on Disk and partitions is not the UEFI boot list.");
            Thread.Sleep(800);
            const string legacyMode = "Legacy BIOS / MBR (poza wymaganiami Windows 11)";
            VerifyChange(boot, "UEFI / GPT", legacyMode);
            Thread.Sleep(800);
            Find(window, ControlType.Text, "Windows 11 w trybie Legacy to instalacja poza wymaganiami Microsoftu. Działanie zależy od obrazu i sprzętu.",
                "Windows 11 in Legacy mode is outside Microsoft's system requirements. It depends on the image and hardware.");
            var layout = Find(window, ControlType.ComboBox, "Układ partycji", "Partition layout");
            PostKey(boot, Keys.Tab);
            if (!SpinWait.SpinUntil(() => layout.Current.HasKeyboardFocus, 2000))
                throw new InvalidOperationException("Tab did not reach the partition layout.");
            VerifyChange(layout, "Ręcznie w instalatorze", "Jedna aktywna partycja C na całym dysku");
            Thread.Sleep(900);
            Navigate(4, 9);
            Find(window, ControlType.Text, "Tryb rozruchu USB — Legacy BIOS / MBR (NTFS)", "USB boot mode — Legacy BIOS / MBR (NTFS)");
            ApproveNewLayout();
            var legacy = Save("Legacy");
            if (!legacy.ToString().Contains("BypassTPMCheck", StringComparison.Ordinal) ||
                (bool?)legacy.Descendants(AnswerFile.U + "Active").Single() != true ||
                legacy.Descendants(AnswerFile.U + "CreatePartition").Count() != 1 ||
                (int?)legacy.Descendants(AnswerFile.U + "InstallTo").Single().Element(AnswerFile.U + "PartitionID") != 1)
                throw new InvalidOperationException("Exported BIOS XML does not create and use a single active C partition.");

            Navigate(9, 4);
            boot = Find(window, ControlType.ComboBox, "Tryb rozruchu instalatora", "Installer boot mode");
            VerifyChange(boot, legacyMode, "UEFI / GPT", Keys.Up);
            Thread.Sleep(900);
            layout = Find(window, ControlType.ComboBox, "Układ partycji", "Partition layout");
            if (!boot.Current.HasKeyboardFocus || ReadValue(layout) != "Ręcznie w instalatorze")
                throw new InvalidOperationException("Switching back to UEFI did not restore manual selection while preserving focus.");
            FocusWhenReady(layout);
            Thread.Sleep(850);
            VerifyChange(layout, "Ręcznie w instalatorze", "Partycje pomocnicze i jedna duża C");
            Thread.Sleep(850);
            Navigate(4, 9);
            Find(window, ControlType.Text, "Tryb rozruchu USB — UEFI / GPT (FAT32)", "USB boot mode — UEFI / GPT (FAT32)");
            ApproveNewLayout();
            var gpt = Save("UEFI");
            if (gpt.Descendants(AnswerFile.U + "InstallToAvailablePartition").Any() ||
                (int?)gpt.Descendants(AnswerFile.U + "InstallTo").Single().Element(AnswerFile.U + "PartitionID") != 3)
                throw new InvalidOperationException("Exported UEFI XML does not select the Windows partition in the GPT layout.");
            Navigate(9, 4);
            layout = Find(window, ControlType.ComboBox, "Układ partycji", "Partition layout");
            FocusWhenReady(layout);
            VerifyChange(layout, "Partycje pomocnicze i jedna duża C", "Partycje pomocnicze oraz C i D");
            Navigate(4, 9);
            ApproveNewLayout();
            var split = Save("UEFI-C-D");
            if ((string?)split.Descendants(AnswerFile.U + "Letter").Single() != "D" ||
                (int?)split.Descendants(AnswerFile.U + "CreatePartition").ElementAt(2).Element(AnswerFile.U + "Size") != 65536)
                throw new InvalidOperationException("Exported UEFI XML does not create a fixed size C and a D with the remaining space.");
            Console.WriteLine("The portable EXE kept focus, filtered the layouts, matched USB modes, and exported the three automatic layouts.");

            void ApproveNewLayout() => VerifyCheckBox(Find(window, ControlType.CheckBox,
                "Potwierdzam usunięcie wszystkich partycji z wybranego dysku",
                "Confirm deleting all partitions on the selected disk"), false);

            void Navigate(int from, int to)
            {
                bool forward = to > from;
                for (int index = from; index != to;)
                {
                    var button = Find(window, ControlType.Button, forward ? "Dalej" : "Wstecz", forward ? "Next" : "Back");
                    ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    index += forward ? 1 : -1;
                    if (!SpinWait.SpinUntil(() => window.Current.Name.Contains($"Krok {index} z 9.", StringComparison.Ordinal), 3000))
                        throw new InvalidOperationException("The wizard did not navigate to step " + index);
                }
            }

            XDocument Save(string name)
            {
                string path = Path.Combine(Directory.CreateDirectory(Path.Combine(output, name)).FullName, AnswerFile.DefaultName);
                var save = Find(window, ControlType.Button, "Zapisz XML…", "Save XML…");
                var main = app.MainWindowHandle;
                SaveThroughDialog(main, save, "Zapisz plik odpowiedzi XML", AnswerFile.DefaultName, path);
                var result = WaitForOwnedDialog(window, "XMLator");
                if (!File.Exists(path))
                    throw new InvalidOperationException("The XML file was not saved.");
                var ok = Find(result, ControlType.Button, "OK", "OK");
                ((InvokePattern)ok.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                if (!SpinWait.SpinUntil(() => window.Current.IsEnabled && GetLastActivePopup(main) == main, 3000))
                    throw new InvalidOperationException("The save result did not return to the wizard.");
                return XDocument.Load(path);
            }
        }
        finally { CloseReleaseFixture(app); }
    }

    internal static void SignIn(string executable)
    {
        var folder = Directory.CreateTempSubdirectory("XMLator-sign-in-ui-");
        try
        {
            string binary = Path.Combine(folder.FullName, "XMLator.exe");
            string config = Path.Combine(folder.FullName, "config.ini");
            string ini = Path.Combine(folder.FullName, "installation.ini");
            var profile = new SetupProfile { CreateUsers = true, Logon = LogonMode.FirstAdministrator };
            profile.Users.Add(new LocalUser { Name = "NVDA-Test" });
            File.Copy(executable, binary);
            IniProfile.Save(ini, profile);
            foreach (var language in UiText.Languages)
            foreach (AppTheme theme in Enum.GetValues<AppTheme>())
            {
                string exported = Path.Combine(folder.FullName, $"answer-{language.Value}-{theme}.xml");
                File.WriteAllLines(config, ["[Interface]", $"Language={language.Value}", $"Theme={theme.ToString().ToLowerInvariant()}"]);
                var start = new ProcessStartInfo(binary);
                start.ArgumentList.Add(ini);
                using var app = Process.Start(start) ?? throw new InvalidOperationException("Could not launch XMLator.");
                try
                {
                    var window = WaitForWizard(app);
                    bool polish = language.Value == "pl";
                    string next = polish ? "Dalej" : UiText.Get(language.Value, "&Next").Replace("&", "");
                    var nextButton = Find(window, ControlType.Button, next, next);
                    for (int i = 0; i < 8; i++)
                    {
                        if (i > 0)
                            nextButton = Find(window, ControlType.Button, next, next);
                        ((InvokePattern)nextButton.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    }
                    string saveName = polish ? "Zapisz XML…" : UiText.Get(language.Value, "&Save XML…").Replace("&", "");
                    var save = Find(window, ControlType.Button, saveName, saveName);
                    SaveThroughDialog(app.MainWindowHandle, save, polish ? "Zapisz plik odpowiedzi XML" :
                        UiText.Get(language.Value, "Save the XML answer file"), AnswerFile.DefaultName, exported);
                    XDocument? answer = null;
                    if (!SpinWait.SpinUntil(() =>
                    {
                        try
                        {
                            using var file = File.OpenRead(exported);
                            answer = XDocument.Load(file);
                            return true;
                        }
                        catch (FileNotFoundException) { return false; }
                        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return false; }
                    }, 8000))
                        throw new InvalidOperationException("The Save XML button did not write the answer file.");
                    XNamespace u = AnswerFile.U;
                    var logon = answer!.Descendants(u + "AutoLogon").Single();
                    var user = answer.Descendants(u + "LocalAccount").Single();
                    if (logon.Element(u + "Username")?.Value != "NVDA-Test" ||
                        (int?)logon.Element(u + "LogonCount") != 1 || logon.Element(u + "Password")?.Element(u + "Value")?.Value != "" ||
                        !XNode.DeepEquals(logon.Element(u + "Password"), user.Element(u + "Password")) ||
                        answer.Descendants(u + "FirstLogonCommands").Single().Descendants(u + "CommandLine").Single().Value != AnswerFile.FirstLogonCommand())
                        throw new InvalidOperationException($"The installed credentials differ in {language.Value}/{theme}.");
                    Console.WriteLine($"{language.Value}/{theme}: the portable EXE saved one-time sign-in with a blank password.");
                }
                finally
                {
                    if (!app.HasExited && !(app.CloseMainWindow() && app.WaitForExit(1000)))
                    {
                        app.Kill();
                        app.WaitForExit();
                    }
                }
            }
        }
        finally { folder.Delete(recursive: true); }
    }

    internal static void Preferences(string executable)
    {
        var folder = Directory.CreateTempSubdirectory("XMLator-preferences-ui-");
        string copy = Path.Combine(folder.FullName, "XMLator.exe");
        string config = Path.Combine(folder.FullName, "config.ini");
        File.Copy(executable, copy);
        try
        {
            using (var app = Launch())
            {
                try
                {
                    var window = AutomationElement.FromHandle(app.MainWindowHandle);
                    if (!File.ReadAllText(config).Contains("Language=auto") || !File.ReadAllText(config).Contains("Theme=system"))
                        throw new InvalidOperationException("First launch did not create the portable configuration.");
                    string locale = UiText.FromWindows(System.Globalization.CultureInfo.CurrentUICulture.Name);
                    var target = Find(window, ControlType.ComboBox, "Który Windows chcesz zainstalować?",
                        UiText.Get(locale, "&Which version of Windows do you want to install?").Replace("&", ""));
                    if (!SpinWait.SpinUntil(() => target.Current.HasKeyboardFocus, 3000))
                        throw new InvalidOperationException("Keyboard focus did not start at the target Windows.");
                    SendArrow(target);
                    if (ReadValue(target) != "Windows 10")
                        throw new InvalidOperationException("Could not select Windows 10 before applying preferences.");
                    var (settings, selector, theme) = OpenSettings(window, locale);
                    string initial = File.ReadAllText(config);
                    SendArrow(selector);
                    theme.SetFocus();
                    SendArrow(theme);
                    Dismiss(window, settings, "Anuluj", UiText.Get(locale, "Cancel"));
                    if (!SpinWait.SpinUntil(() => SettingsHasFocus(window, locale), 3000) || File.ReadAllText(config) != initial)
                        throw new InvalidOperationException("Cancel changed configuration or lost keyboard focus.");

                    (settings, selector, theme) = OpenSettings(window, locale);
                    for (int i = 0; i < 3; i++)
                        SendArrow(selector);
                    theme.SetFocus();
                    SendArrow(theme);
                    SendArrow(theme);
                    if (ReadValue(selector) != "English" || ReadValue(theme) != (locale == "pl" ? "Ciemny" : UiText.Get(locale, "Dark")) ||
                        File.ReadAllText(config) != initial)
                        throw new InvalidOperationException("The choices should be accessible and pending until OK.");
                    Dismiss(window, settings, "OK", UiText.Get(locale, "&OK").Replace("&", ""));
                    if (!SpinWait.SpinUntil(() => window.Current.Name == $"{Wizard.ProductCaption} — Step 1 of 9. Windows version" &&
                        File.ReadAllText(config).Contains("Language=en") && File.ReadAllText(config).Contains("Theme=dark") &&
                        SettingsHasFocus(window, "en"), 6000))
                        throw new InvalidOperationException("OK did not apply both settings or return focus to Settings.");
                    if (ReadValue(Find(window, ControlType.ComboBox, "Which version of Windows do you want to install?", "Which version of Windows do you want to install?")) != "Windows 10")
                        throw new InvalidOperationException("Applying preferences changed the selected Windows.");
                    Console.WriteLine("Cancel kept the configuration. OK applied English and dark mode, preserved the target, and returned focus to Settings.");
                }
                finally { Close(app); }
            }
            using (var app = Launch())
            {
                try
                {
                    var window = AutomationElement.FromHandle(app.MainWindowHandle);
                    var (settings, selector, theme) = OpenSettings(window, "en");
                    if (window.Current.Name != $"{Wizard.ProductCaption} — Step 1 of 9. Windows version" ||
                        ReadValue(selector) != "English" || ReadValue(theme) != "Dark")
                        throw new InvalidOperationException("A fresh process did not restore both preferences.");
                    Dismiss(window, settings, "Cancel", "Cancel");
                    Console.WriteLine("The portable EXE restored English and dark mode on restart.");
                }
                finally { Close(app); }
            }
        }
        finally { folder.Delete(recursive: true); }

        static AutomationElement? SettingsButton(AutomationElement window, string locale) =>
            FindOptional(window, ControlType.Button, "Ustawienia…", UiText.Get(locale, "&Settings…").Replace("&", ""));

        static bool SettingsHasFocus(AutomationElement window, string locale) => SettingsButton(window, locale)?.Current.HasKeyboardFocus == true;

        static (AutomationElement Dialog, AutomationElement Language, AutomationElement Theme) OpenSettings(AutomationElement window, string locale)
        {
            string languageName = UiText.Get(locale, "XMLator &interface language").Replace("&", "");
            string themeName = UiText.Get(locale, "XMLator interface the&me").Replace("&", "");
            if (FindOptional(window, ControlType.ComboBox, "Język interfejsu XMLatora", languageName) is not null ||
                FindOptional(window, ControlType.ComboBox, "Motyw interfejsu XMLatora", themeName) is not null)
                throw new InvalidOperationException("The preferences are displayed on the first screen.");
            var button = SettingsButton(window, locale) ?? throw new InvalidOperationException("The Settings button is missing.");
            button.SetFocus();
            Click(button);
            var owner = new IntPtr(window.Current.NativeWindowHandle);
            const uint enabledPopup = 6;
            IntPtr popup = IntPtr.Zero;
            if (!SpinWait.SpinUntil(() => (popup = GetWindow(owner, enabledPopup)) != owner && popup != IntPtr.Zero, 6000))
                throw new InvalidOperationException("Settings did not open as a modal dialog.");
            var dialog = AutomationElement.FromHandle(popup);
            var language = Find(dialog, ControlType.ComboBox, "Język interfejsu XMLatora", languageName);
            var theme = Find(dialog, ControlType.ComboBox, "Motyw interfejsu XMLatora", themeName);
            if (dialog.Current.Name != (locale == "pl" ? "Ustawienia" : UiText.Get(locale, "Settings")) ||
                !SpinWait.SpinUntil(() => language.Current.HasKeyboardFocus, 3000))
                throw new InvalidOperationException("Unexpected settings dialog title or initial keyboard focus.");
            return (dialog, language, theme);
        }

        static void Dismiss(AutomationElement window, AutomationElement dialog, string pl, string en)
        {
            var button = Find(dialog, ControlType.Button, pl, en);
            button.SetFocus();
            var handle = new IntPtr(dialog.Current.NativeWindowHandle);
            Click(button);
            if (!SpinWait.SpinUntil(() => !IsWindow(handle) && window.Current.IsEnabled, 6000))
                throw new InvalidOperationException("Settings did not close after pressing " + en);
        }

        static void Click(AutomationElement button)
        {
            const uint buttonClick = 0x00F5;
            if (!PostMessage(new IntPtr(button.Current.NativeWindowHandle), buttonClick, IntPtr.Zero, IntPtr.Zero))
                throw new InvalidOperationException("Could not click " + button.Current.Name);
        }

        Process Launch()
        {
            var app = Process.Start(new ProcessStartInfo(copy)) ?? throw new InvalidOperationException("Could not launch XMLator.");
            if (SpinWait.SpinUntil(() =>
            {
                app.Refresh();
                return app.MainWindowHandle != IntPtr.Zero;
            }, 10000))
                return app;
            Close(app);
            app.Dispose();
            throw new InvalidOperationException("XMLator did not open its window.");
        }

        static void Close(Process app)
        {
            if (!app.HasExited && !(app.CloseMainWindow() && app.WaitForExit(2500)))
            {
                app.Kill();
                app.WaitForExit();
            }
        }
    }

    internal static void Run(string executable, string? expectedIsoHash = null, string? profile = null,
        string isoVersion = "Windows 11", string isoRelease = "25H2", string isoArchitecture = "x64 (AMD64)",
        string? proszczokImage = null)
    {
        var launch = new ProcessStartInfo(Path.GetFullPath(executable));
        if (profile is not null)
            launch.ArgumentList.Add(Path.GetFullPath(profile));
        using var app = Process.Start(launch) ?? throw new InvalidOperationException("Could not launch XMLator.");
        try
        {
            var window = WaitForWizard(app);
            if (proszczokImage is not null)
            {
                VerifyProszczok(app, window, Path.GetDirectoryName(Path.GetFullPath(executable))!, proszczokImage, expectedIsoHash);
                return;
            }
            if (expectedIsoHash is not null)
            {
                VerifyMicrosoftDownload(app, window, Path.GetDirectoryName(Path.GetFullPath(executable))!, expectedIsoHash,
                    isoVersion, isoRelease, isoArchitecture);
                return;
            }
            var target = Find(window, ControlType.ComboBox, "Który Windows chcesz zainstalować?", "Which version of Windows do you want to install?");
            VerifyChange(target, "Windows 11", "Windows 10");
            var architecture = Find(window, ControlType.ComboBox, "Architektura obrazu instalacyjnego", "Installation image architecture");
            VerifyChange(architecture, "x64 (AMD64)", "x86 (32-bit)");
            var addX64 = Find(window, ControlType.CheckBox, "Dodaj architekturę x64 (AMD64) do tego samego XML", "Also include x64 (AMD64) in the same XML");
            VerifyCheckBox(addX64, false);
            VerifyCheckBox(addX64, true);
            architecture.SetFocus();
            if (ReadValue(architecture) != "x86 (32-bit)")
                throw new InvalidOperationException("Returning to architecture did not expose the selected x86 value.");
            target.SetFocus();
            if (!SpinWait.SpinUntil(() => target.Current.HasKeyboardFocus && ReadValue(target) == "Windows 10", 1500))
                throw new InvalidOperationException("The selection was unavailable after returning focus to the Windows list.");

            for (int i = 0; i < 2; i++)
                ((InvokePattern)Find(window, ControlType.Button, "Dalej", "Next").GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            var edition = Find(window, ControlType.ComboBox, "Którą edycję Windows zainstalować?", "Which Windows edition should be installed?");
            string initialEdition = edition.Current.Name.StartsWith("Którą", StringComparison.Ordinal)
                ? "Do wybrania w instalatorze" : "Choose in Setup";
            VerifyChange(edition, initialEdition, "Home");
            var keyMode = Find(window, ControlType.ComboBox, "Czy podać klucz produktu do instalacji?", "Specify a product key for installation?");
            bool keyPolish = keyMode.Current.Name.StartsWith("Czy", StringComparison.Ordinal);
            VerifyChange(keyMode, keyPolish ? "Do podania w instalatorze, jeśli zapyta" : "Enter the key during Setup if prompted",
                keyPolish ? "Pomiń pytanie o klucz instalacyjny" : "Skip the setup product key prompt");

            for (int i = 0; i < 2; i++)
                ((InvokePattern)Find(window, ControlType.Button, "Dalej", "Next").GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            var owner = Find(window, ControlType.CheckBox, "Ustaw właściciela, organizację lub grupę roboczą",
                "Set a registered owner, organization or workgroup");
            VerifyCheckBox(owner, false);
            Find(window, ControlType.Edit, "Zarejestrowany właściciel Windows", "Registered Windows owner");
            VerifyCheckBox(owner, true);
            for (int i = 0; i < 2; i++)
                ((InvokePattern)Find(window, ControlType.Button, "Dalej", "Next").GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            VerifyAppOptions(app.MainWindowHandle, window);
            for (int i = 0; i < 2; i++)
                ((InvokePattern)Find(window, ControlType.Button, "Dalej", "Next").GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            var drive = Find(window, ControlType.ComboBox, "Pendrive", "USB drive");
            Find(window, ControlType.Text, "Tryb rozruchu USB — UEFI / GPT (FAT32)", "USB boot mode — UEFI / GPT (FAT32)");
            Find(window, ControlType.Edit, "Ścieżka do obrazu ISO Windows", "Windows ISO path");
            bool polish = keyPolish;
            Find(window, ControlType.Button, "Odśwież źródła ISO", "Refresh ISO sources");
            var create = Find(window, ControlType.Button, "Utwórz bootowalny pendrive", "Create a bootable USB drive");
            var save = Find(window, ControlType.Button, "Zapisz XML…", "Save XML…");
            var saveIni = Find(window, ControlType.Button, "Zapisz profil INI…", "Save INI profile…");
            var filesGroup = Find(window, ControlType.Group, "Zapis plików", "Save files");
            var usbGroup = Find(window, ControlType.Group, "Pendrive instalacyjny", "Installation USB drive");
            if (new[] { save, saveIni }.Any(button => !button.Current.IsEnabled || !button.Current.IsKeyboardFocusable ||
                button.Current.NativeWindowHandle == 0 || !InGroup(button, filesGroup)) || create.Current.IsEnabled ||
                create.Current.NativeWindowHandle == 0 || !InGroup(create, usbGroup) || !InGroup(drive, usbGroup))
                throw new InvalidOperationException("Expected named native file and USB groups, with creation disabled before selecting a drive and ISO.");
            save.SetFocus();
            PostKey(save, Keys.Tab);
            if (!SpinWait.SpinUntil(() => saveIni.Current.HasKeyboardFocus, 2000))
                throw new InvalidOperationException("Tab did not move from Save XML to Save INI profile.");
            PostKey(saveIni, Keys.Tab);
            if (!SpinWait.SpinUntil(() => drive.Current.HasKeyboardFocus, 2000))
                throw new InvalidOperationException("Tab did not move from Save INI profile to the USB drive selector.");
            Console.WriteLine("UI Automation read both groups; Tab moved from Save XML to Save INI and then to the USB drive list.");
            VerifyProfileSave(app.MainWindowHandle, saveIni, polish);
            VerifySave(app.MainWindowHandle, save, polish);
        }
        finally
        {
            if (!app.HasExited && !(app.CloseMainWindow() && app.WaitForExit(2500)))
                app.Kill();
        }

        static bool InGroup(AutomationElement control, AutomationElement group)
        {
            for (var parent = TreeWalker.ControlViewWalker.GetParent(control); parent is not null;
                parent = TreeWalker.ControlViewWalker.GetParent(parent))
                if (parent.Equals(group))
                    return true;
            return false;
        }
    }

    private static AutomationElement WaitForWizard(Process app)
    {
        AutomationElement window = null!;
        if (!SpinWait.SpinUntil(() =>
        {
            app.Refresh();
            if (app.HasExited || app.MainWindowHandle == IntPtr.Zero)
                return false;
            try
            {
                window = AutomationElement.FromHandle(app.MainWindowHandle);
                return window.Current.Name.StartsWith("XMLator ", StringComparison.Ordinal) &&
                    window.Current.Name.Contains("— ", StringComparison.Ordinal);
            }
            catch (ElementNotAvailableException) { return false; }
        }, 10000))
            throw new InvalidOperationException("XMLator did not open a window with the step in its title.");
        return window;
    }

    private static void VerifyAppOptions(IntPtr main, AutomationElement window)
    {
        var removal = Find(window, ControlType.Button, "Aplikacje i funkcje do usunięcia…", "Apps and features to remove…");
        removal.SetFocus();
        PostKey(removal, Keys.Space);
        IntPtr popup = IntPtr.Zero;
        if (!SpinWait.SpinUntil(() => (popup = GetLastActivePopup(main)) != main && popup != IntPtr.Zero, 5000))
            throw new InvalidOperationException("Could not open App Removal with the keyboard.");
        var dialog = AutomationElement.FromHandle(popup);
        var entries = dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox))
            .Cast<AutomationElement>().ToArray();
        var preset = Find(dialog, ControlType.ComboBox, "Zestaw aplikacji do usunięcia", "App removal preset");
        if (entries.Length < 30 || entries.Any(entry => entry.Current.NativeWindowHandle == 0 ||
            string.IsNullOrWhiteSpace(entry.Current.Name) || !entry.Current.IsKeyboardFocusable ||
            !entry.TryGetCurrentPattern(TogglePattern.Pattern, out _)))
            throw new InvalidOperationException("One or more apps lack a named, keyboard-focusable Windows checkbox.");
        preset.SetFocus();
        PostKey(preset, Keys.Tab);
        var visited = new HashSet<int>();
        for (int i = 0; i < entries.Length; i++)
        {
            AutomationElement? current = null;
            if (!SpinWait.SpinUntil(() =>
            {
                var active = AutomationElement.FocusedElement;
                current = entries.FirstOrDefault(entry => entry.Equals(active) && !visited.Contains(entry.Current.NativeWindowHandle));
                return current is not null;
            }, 2000))
                throw new InvalidOperationException($"Tab did not reach app checkbox {i + 1} of {entries.Length}.");
            visited.Add(current!.Current.NativeWindowHandle);
            if (i + 1 < entries.Length)
                PostKey(current, Keys.Tab);
        }
        var store = Find(dialog, ControlType.CheckBox, "Microsoft Store — zalecamy zachować", "Microsoft Store — recommended to keep");
        VerifyCheckBox(store, false);
        VerifyCheckBox(store, true);
        PostKey(store, Keys.Escape);
        if (!SpinWait.SpinUntil(() => GetLastActivePopup(main) == main, 4000))
            throw new InvalidOperationException("Escape did not close the App Removal dialog.");
        Console.WriteLine($"Tab reached all {entries.Length} individual Windows app checkboxes. Escape closed the dialog.");
    }

    private static void VerifyCheckBox(AutomationElement box, bool wasChecked)
    {
        box.SetFocus();
        var toggle = (TogglePattern)box.GetCurrentPattern(TogglePattern.Pattern);
        var expected = wasChecked ? ToggleState.Off : ToggleState.On;
        if (!SpinWait.SpinUntil(() => box.Current.HasKeyboardFocus, 2000) || toggle.Current.ToggleState == expected)
            throw new InvalidOperationException($"Invalid keyboard focus or initial state for {box.Current.Name}.");
        VerifyMsaaCheckState(box, wasChecked);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AutomationPropertyChangedEventHandler onToggle = (_, args) =>
        {
            if (Equals(args.NewValue, expected) || Equals(args.NewValue, (int)expected))
                changed.TrySetResult();
        };
        Automation.AddAutomationPropertyChangedEventHandler(box, TreeScope.Element, onToggle, TogglePattern.ToggleStateProperty);
        try
        {
            PostKey(box, Keys.Space);
            if (!SpinWait.SpinUntil(() => toggle.Current.ToggleState == expected, 2000))
                throw new InvalidOperationException($"Space did not toggle {box.Current.Name}.");
            if (!changed.Task.Wait(2000))
                throw new InvalidOperationException($"UI Automation did not receive the new check state for {box.Current.Name}.");
            VerifyMsaaCheckState(box, !wasChecked);
            Console.WriteLine($"Space, UIA event and MSAA state verified for {box.Current.Name}: {expected}.");
        }
        finally
        {
            Automation.RemoveAutomationPropertyChangedEventHandler(box, onToggle);
        }
    }

    private static void VerifyMsaaCheckState(AutomationElement box, bool expected)
    {
        var iid = typeof(IAccessible).GUID;
        if (AccessibleObjectFromWindow(new IntPtr(box.Current.NativeWindowHandle), unchecked((uint)-4), in iid, out var accessible) != 0)
            throw new InvalidOperationException($"Could not read MSAA for {box.Current.Name}.");
        try
        {
            if (accessible.get_accName(0) != box.Current.Name || (int)accessible.get_accRole(0) != (int)AccessibleRole.CheckButton ||
                (((int)accessible.get_accState(0) & (int)AccessibleStates.Checked) != 0) != expected)
                throw new InvalidOperationException($"Invalid MSAA name, role or check state for {box.Current.Name}.");
        }
        finally { Marshal.ReleaseComObject(accessible); }
    }

    private static void PostKey(AutomationElement target, Keys key)
    {
        var handle = new IntPtr(target.Current.NativeWindowHandle);
        if (!PostMessage(handle, 0x0100, new IntPtr((int)key), new IntPtr(1)) ||
            !PostMessage(handle, 0x0101, new IntPtr((int)key), new IntPtr(unchecked((int)0xC0000001))))
            throw new InvalidOperationException($"Could not send {key} to {target.Current.Name}.");
    }

    private static void VerifyMicrosoftDownload(Process app, AutomationElement window, string directory, string expectedHash,
        string isoVersion, string isoRelease, string isoArchitecture)
    {
        OpenIsoReview(window, isoVersion, isoArchitecture);
        var source = WaitForSource(window, "Pobierz ISO z Microsoftu", "Download an ISO from Microsoft");
        bool polish = source.Current.Name == "Źródło obrazu ISO";
        var edition = Find(window, ControlType.ComboBox, "Wydanie ISO (Home / Pro / Education)", "ISO release (Home / Pro / Education)");
        if (ReadValue(edition) != isoRelease)
            ChooseNext(edition, isoRelease);
        var language = Find(window, ControlType.ComboBox, "Język obrazu ISO", "ISO language");
        if (ReadValue(language) != "Polski (Polska)")
            throw new InvalidOperationException("The ISO dialog did not select Polish.");
        VerifyDownloadResult(app, window, directory, expectedHash, polish);
    }

    private static void OpenIsoReview(AutomationElement window, string isoVersion, string isoArchitecture)
    {
        var target = Find(window, ControlType.ComboBox, "Który Windows chcesz zainstalować?", "Which version of Windows do you want to install?");
        if (ReadValue(target) != isoVersion)
            ChooseNext(target, isoVersion);
        var architecture = Find(window, ControlType.ComboBox, "Architektura obrazu instalacyjnego", "Installation image architecture");
        if (ReadValue(architecture) != isoArchitecture)
            ChooseNext(architecture, isoArchitecture);
        for (int i = 0; i < 8; i++)
            ((InvokePattern)Find(window, ControlType.Button, "Dalej", "Next").GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }

    private static void VerifyProszczok(Process app, AutomationElement window, string directory, string fileName, string? expectedHash)
    {
        for (int i = 0; i < 8; i++)
            ((InvokePattern)Find(window, ControlType.Button, "Dalej", "Next").GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        var source = WaitForSource(window, "Pobierz ISO z proszczok.pl", "Download an ISO from proszczok.pl");
        bool polish = source.Current.Name == "Źródło obrazu ISO";
        var files = Find(window, ControlType.ComboBox, "Plik ISO z proszczok.pl", "ISO file from proszczok.pl");
        if (!SpinWait.SpinUntil(() => ReadValue(files)?.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) == true, 20000))
            throw new InvalidOperationException("The proszczok.pl ISO list did not load: " + ReadValue(files));
        if (!files.Current.IsKeyboardFocusable || files.Current.NativeWindowHandle == 0 ||
            FindOptional(window, ControlType.ComboBox, "Język obrazu ISO", "ISO language") is { Current.IsOffscreen: false })
            throw new InvalidOperationException("The proszczok.pl source should present one native ISO file list with no edition or language fields.");
        source.SetFocus();
        PostKey(source, Keys.Tab);
        if (!SpinWait.SpinUntil(() => files.Current.HasKeyboardFocus, 2000))
            throw new InvalidOperationException("Tab did not reach the ISO file list.");
        if (ReadValue(files) != fileName)
            VerifyChange(files, ReadValue(files)!, fileName);
        Console.WriteLine("Keyboard and UI Automation selected " + fileName);
        var refresh = Find(window, ControlType.Button, "Odśwież źródła ISO", "Refresh ISO sources");
        ((InvokePattern)refresh.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        if (!SpinWait.SpinUntil(() => refresh.Current.IsEnabled && ReadValue(files) == fileName, 20000))
            throw new InvalidOperationException("Refreshing the ISO catalog did not keep the selected file.");
        Console.WriteLine("The catalog refreshed and retained the selected file.");
        if (expectedHash is not null)
            VerifyDownloadResult(app, window, directory, expectedHash, polish, fileName);
    }

    private static void VerifyDownloadResult(Process app, AutomationElement window, string directory, string expectedHash, bool polish,
        string? fileName = null)
    {
        var before = Directory.GetFiles(directory, "*.iso").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var start = Find(window, ControlType.Button, "Pobierz i użyj ISO…", "Download and use ISO…");
        ((InvokePattern)start.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        var timeout = Stopwatch.StartNew();
        string? saved;
        bool sawPercent = false;
        while ((saved = Directory.GetFiles(directory, "*.iso").Except(before, StringComparer.OrdinalIgnoreCase).SingleOrDefault()) is null &&
            timeout.Elapsed < TimeSpan.FromMinutes(30) && !app.HasExited)
        {
            IntPtr active = GetLastActivePopup(app.MainWindowHandle);
            if (active != IntPtr.Zero && active != app.MainWindowHandle)
            {
                var popup = AutomationElement.FromHandle(active);
                if (popup.Current.Name is "XMLator" or "Anulowano pobieranie" or "Download canceled")
                    throw new InvalidOperationException("ISO download dialog: " + popup.Current.Name + " " + string.Join(" ",
                        popup.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
                            .Select(item => item.Current.Name).Where(name => !string.IsNullOrWhiteSpace(name))));
            }
            if (!sawPercent)
            {
                try
                {
                    var bar = Find(window, ControlType.ProgressBar, "Postęp pobierania ISO", "ISO download progress");
                    if (bar.TryGetCurrentPattern(RangeValuePattern.Pattern, out var pattern) &&
                        ((RangeValuePattern)pattern).Current.Value is > 0 and < 100)
                    {
                        sawPercent = true;
                        Console.WriteLine($"UI Automation read ISO download progress: {((RangeValuePattern)pattern).Current.Value}%.");
                    }
                }
                catch (InvalidOperationException) { }
            }
            Thread.Sleep(400);
        }
        if (saved is null || !sawPercent)
            throw new InvalidOperationException("The wizard did not complete the ISO download with an accessible progress percentage.");
        if (fileName is not null && !string.Equals(Path.GetFileName(saved), fileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The wizard did not keep the downloaded image's file name. " + saved);
        var mainHandle = new IntPtr(window.Current.NativeWindowHandle);
        string title = polish ? "Pobrano obraz ISO" : "The ISO was downloaded";
        AutomationElement? result = null;
        if (!SpinWait.SpinUntil(() =>
        {
            IntPtr popup = GetLastActivePopup(mainHandle);
            if (popup == IntPtr.Zero || popup == mainHandle)
                return false;
            result = AutomationElement.FromHandle(popup);
            return result.Current.Name == title;
        }, 5000))
            throw new InvalidOperationException($"The wizard did not show the download success dialog. Active window: {result?.Current.Name}.");
        var ok = Find(result!, ControlType.Button, "OK", "OK");
        ((InvokePattern)ok.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        var path = Find(window, ControlType.Edit, "Ścieżka do obrazu ISO Windows", "Windows ISO path");
        var create = Find(window, ControlType.Button, "Utwórz bootowalny pendrive", "Create a bootable USB drive");
        var drive = Find(window, ControlType.ComboBox, "Pendrive", "USB drive");
        if (!SpinWait.SpinUntil(() => ReadValue(path) == saved &&
            (create.Current.IsEnabled ? create.Current.HasKeyboardFocus : drive.Current.HasKeyboardFocus), 3000))
            throw new InvalidOperationException("The downloaded ISO was not selected or focus did not move to the next USB control.");
        Console.WriteLine("The native result dialog closed, the downloaded ISO was selected and focus moved to the next USB control.");

        FileStream? isoStream = null;
        if (!SpinWait.SpinUntil(() =>
        {
            try { isoStream = File.OpenRead(saved); return true; }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return false; }
        }, 10000))
            throw new InvalidOperationException("The saved ISO remained locked after the download.");
        using var image = isoStream!;
        string actualHash = Convert.ToHexString(SHA256.HashData(image));
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"ISO checksum mismatch: {actualHash}.");
        Console.WriteLine($"The wizard saved {image.Length} bytes next to its EXE. SHA-256: {actualHash}.");
    }

    private static void ChooseNext(AutomationElement choice, string expected)
    {
        SendArrow(choice);
        if (!SpinWait.SpinUntil(() => ReadValue(choice) == expected, 4000))
            throw new InvalidOperationException($"Could not select {expected}. Current value: {ReadValue(choice)}.");
    }

    private static AutomationElement WaitForSource(AutomationElement window, string pl, string en)
    {
        const uint findExactComboItem = 0x0158;
        AutomationElement? source = null;
        string expected = en;
        int index = -1;
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(60))
        {
            source = FindOptional(window, ControlType.ComboBox, "Źródło obrazu ISO", "ISO source");
            if (source is not null && source.Current.NativeWindowHandle != 0)
            {
                if (!source.Current.HasKeyboardFocus)
                    source.SetFocus();
                expected = source.Current.Name == "Źródło obrazu ISO" ? pl : en;
                if (SendText(new IntPtr(source.Current.NativeWindowHandle), findExactComboItem, new IntPtr(-1), expected,
                        2, 4000, out var match) != IntPtr.Zero && (index = match.ToInt32()) >= 0)
                    break;
            }
            Thread.Sleep(100);
        }
        if (source is null || index < 0)
            throw new InvalidOperationException($"No confirmed source appeared for {en}.");
        SendArrow(source, Keys.Home);
        for (int i = 0; i < index; i++)
            SendArrow(source);
        if (ReadValue(source) != expected)
            throw new InvalidOperationException($"Could not select {expected}. Current value: {ReadValue(source)}.");
        Console.WriteLine($"Keyboard selected {expected} from the confirmed sources.");
        return source;
    }

    private static void VerifySave(IntPtr mainWindow, AutomationElement save, bool polish)
    {
        var folder = Directory.CreateTempSubdirectory("XMLator-save-audit-");
        string path = Path.Combine(folder.FullName, AnswerFile.DefaultName);
        try
        {
            SaveThroughDialog(mainWindow, save, polish ? "Zapisz plik odpowiedzi XML" : "Save the XML answer file", AnswerFile.DefaultName, path);
            XDocument? answer = null;
            if (!SpinWait.SpinUntil(() =>
            {
                try
                {
                    using var stream = File.OpenRead(path);
                    if (stream.Length == 0)
                        return false;
                    answer = XDocument.Load(stream);
                    return true;
                }
                catch (FileNotFoundException) { return false; }
                catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return false; }
            }, 8000))
                throw new InvalidOperationException("Could not save a real Windows answer file from the wizard.");
            XNamespace xml = AnswerFile.U;
            var key = answer!.Descendants(xml + "ProductKey").Single();
            if (answer.Root?.Name.NamespaceName != AnswerFile.U || key.Element(xml + "Key") is not null || key.Element(xml + "WillShowUI")?.Value != "Never" ||
                answer.Descendants(xml + "component").Any(component => (string?)component.Attribute("processorArchitecture") != "x86"))
                throw new InvalidOperationException("The XML export did not match the selected architecture and no-key option.");
            Console.WriteLine("The wizard saved Autounattend.xml without USB or ISO selection.");
        }
        finally
        {
            File.Delete(path);
            folder.Delete();
        }
    }

    private static void VerifyProfileSave(IntPtr mainWindow, AutomationElement save, bool polish)
    {
        var folder = Directory.CreateTempSubdirectory("XMLator-profile-audit-");
        string path = Path.Combine(folder.FullName, "Customer Profile.ini");
        try
        {
            SaveThroughDialog(mainWindow, save, polish ? "Zapisz profil XMLatora" : "Save XMLator profile", "XMLator.ini", path);
            if (!SpinWait.SpinUntil(() => File.Exists(path), 7000))
                throw new InvalidOperationException("The wizard did not write an INI profile.");
            var profile = IniProfile.Load(path);
            if (profile.TargetName != "Windows 10" || profile.Architecture != "x86" ||
                profile.SelectedEdition?.Label != "Home" || profile.KeyMode != KeyMode.Skip)
                throw new InvalidOperationException("The INI profile did not preserve the wizard selections.");

            IntPtr popup = IntPtr.Zero;
            if (!SpinWait.SpinUntil(() =>
            {
                popup = GetLastActivePopup(mainWindow);
                return popup != mainWindow && popup != IntPtr.Zero && AutomationElement.FromHandle(popup).Current.Name == "XMLator";
            }, 5000))
                throw new InvalidOperationException("The wizard did not show the profile saved message.");
            var ok = Find(AutomationElement.FromHandle(popup), ControlType.Button, "OK", "OK");
            ((InvokePattern)ok.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            if (!SpinWait.SpinUntil(() => GetLastActivePopup(mainWindow) == mainWindow && save.Current.HasKeyboardFocus, 3000))
                throw new InvalidOperationException("Focus did not return to the Save INI profile button.");
            Console.WriteLine("Space opened the native INI save dialog, the saved profile retained its settings, and focus returned to Save INI profile.");
        }
        finally
        {
            File.Delete(path);
            folder.Delete();
        }
    }

    private static void SaveThroughDialog(IntPtr mainWindow, AutomationElement save, string title, string proposedName, string path)
    {
        save.SetFocus();
        PostKey(save, Keys.Space);
        IntPtr popup = IntPtr.Zero;
        if (!SpinWait.SpinUntil(() => (popup = GetLastActivePopup(mainWindow)) != mainWindow && popup != IntPtr.Zero, 7000))
            throw new InvalidOperationException("Could not find the native Save As dialog.");
        var dialog = AutomationElement.FromHandle(popup);
        if (dialog.Current.Name != title)
            throw new InvalidOperationException("Unexpected Save As dialog: " + dialog.Current.Name);
        var edit = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
        AutomationElement? name = null;
        if (!SpinWait.SpinUntil(() =>
        {
            name = dialog.FindAll(TreeScope.Descendants, edit).Cast<AutomationElement>()
                .FirstOrDefault(item => item.Current.NativeWindowHandle != 0 &&
                    item.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern value &&
                    !value.Current.IsReadOnly && Path.GetFileName(value.Current.Value) == proposedName);
            return name is not null;
        }, 5000))
            throw new InvalidOperationException("The Save As dialog did not propose " + proposedName + ".");
        const uint setText = 0x000C;
        if (SendText(new IntPtr(name!.Current.NativeWindowHandle), setText, IntPtr.Zero, path, 2, 4000, out _) == IntPtr.Zero)
            throw new InvalidOperationException("Could not enter the test path into Save As.");
        IntPtr confirm = GetDlgItem(popup, 1);
        const uint click = 0x00F5;
        if (confirm == IntPtr.Zero || !PostMessage(confirm, click, IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException("Could not find the native Save As confirmation button.");
    }

    private static AutomationElement Find(AutomationElement window, ControlType type, string pl, string en) =>
        FindOptional(window, type, pl, en) ?? throw new InvalidOperationException($"Missing accessible control {en}.");

    private static AutomationElement? FindOptional(AutomationElement window, ControlType type, string pl, string en) =>
        window.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, type),
            new OrCondition(new PropertyCondition(AutomationElement.NameProperty, pl),
                new PropertyCondition(AutomationElement.NameProperty, en))));

    private static string? ReadValue(AutomationElement box) => box.GetCurrentPropertyValue(ValuePattern.ValueProperty, true) as string;

    private static void VerifyChange(AutomationElement box, string initial, string selected, Keys arrow = Keys.Down)
    {
        box.SetFocus();
        if (!SpinWait.SpinUntil(() => box.Current.HasKeyboardFocus && ReadValue(box) == initial, 1500))
            throw new InvalidOperationException($"Expected {initial} on focus. Read {ReadValue(box)} with keyboard focus {box.Current.HasKeyboardFocus}.");
        VerifyNoAutomaticHelp(box);

        var changed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        AutomationPropertyChangedEventHandler onValue = (_, args) =>
        {
            if (args.NewValue is string text)
                changed.TrySetResult(text);
        };
        Automation.AddAutomationPropertyChangedEventHandler(box, TreeScope.Element, onValue, ValuePattern.ValueProperty);
        try
        {
            SendArrow(box, arrow);
            if (!changed.Task.Wait(2000) || changed.Task.Result != selected || ReadValue(box) != selected)
                throw new InvalidOperationException($"UI Automation did not receive the selection change {initial} -> {selected}.");
            VerifyNoAutomaticHelp(box);
            Console.WriteLine($"UIA value verified without automatic field help: {initial} -> {selected}.");
        }
        finally
        {
            Automation.RemoveAutomationPropertyChangedEventHandler(box, onValue);
        }
    }

    private static void SendArrow(AutomationElement box, Keys arrow = Keys.Down)
    {
        const uint keyDown = 0x0100;
        if (SendMessageTimeout(new IntPtr(box.Current.NativeWindowHandle), keyDown, new IntPtr((int)arrow),
                new IntPtr(1), 2, 4000, out _) == IntPtr.Zero)
            throw new InvalidOperationException($"Could not send {arrow} to the test window.");
    }

    private static void VerifyNoAutomaticHelp(AutomationElement box)
    {
        var iid = typeof(IAccessible).GUID;
        int result = AccessibleObjectFromWindow(new IntPtr(box.Current.NativeWindowHandle), unchecked((uint)-4), in iid, out var accessible);
        if (result != 0)
            throw new InvalidOperationException($"Could not read the MSAA description of {box.Current.Name} (0x{result:X8}).");
        try
        {
            if (!string.IsNullOrEmpty(box.Current.HelpText) || !string.IsNullOrEmpty(accessible.get_accDescription(0)))
                throw new InvalidOperationException($"Unexpected automatic help on {box.Current.Name}.");
        }
        finally
        {
            Marshal.ReleaseComObject(accessible);
        }
    }

    [DllImport("oleacc.dll", ExactSpelling = true)]
    private static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId, in Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible accessible);

    [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendText(IntPtr window, uint message, IntPtr wParam, string text,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetDlgItem(IntPtr dialog, int item);

    [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetLastActivePopup(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);
}
