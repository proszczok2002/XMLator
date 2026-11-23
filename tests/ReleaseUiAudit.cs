using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows.Automation;
using XMLator;

internal static partial class SelectionAudit
{
    internal static void ReleaseSettings(string binary, string languages, string workDirectory)
    {
        string folder = Path.Combine(Path.GetFullPath(workDirectory), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string exe = Path.Combine(folder, "XMLator.exe");
        string config = Path.Combine(folder, "config.ini");
        File.Copy(binary, exe);
        try
        {
            foreach (var language in UiText.Languages.Where(item => languages == "all" || languages.Split(',').Contains(item.Value)))
            {
                string locale = language.Value;
                string Local(string pl, string en) => (locale == "pl" ? pl : UiText.Get(locale, en)).Replace("&", "");
                foreach (var theme in Enum.GetValues<AppTheme>())
                {
                    new InterfacePreferences(config)
                    {
                        Language = locale, Theme = theme, AutomaticUpdates = true, UpdateIntervalDays = 1,
                        LastUpdateCheck = DateTimeOffset.UtcNow
                    }.Save();
                    using var process = Process.Start(exe)!;
                    try
                    {
                        var window = WaitForWizard(process);
                        var settings = Named(window, ControlType.Button, Local("Ustawienia…", "&Settings…"));
                        settings.SetFocus();
                        if (!SpinWait.SpinUntil(() => settings.Current.HasKeyboardFocus, 2000))
                            throw new InvalidOperationException("Settings did not receive keyboard focus.");
                        PostKey(settings, Keys.Space);
                        var dialog = WaitForOwnedDialog(window, Local("Ustawienia", "Settings"));
                        var check = Named(dialog, ControlType.CheckBox, Local("Sprawdzaj automatycznie przy uruchamianiu", "Check &automatically at startup"));
                        var frequency = Named(dialog, ControlType.ComboBox, Local("Jak często sprawdzać aktualizacje?", "How &often should updates be checked?"));
                        var manual = Named(dialog, ControlType.Button, Local("Sprawdź aktualizacje teraz", "Check for updates &now"));
                        if (!manual.Current.IsEnabled || !manual.Current.IsKeyboardFocusable)
                            throw new InvalidOperationException("Manual checking is unavailable.");
                        FocusWhenReady(frequency);
                        VerifyChange(frequency, Local("Raz dziennie", "Daily"), Local("Raz w tygodniu", "Weekly"));
                        VerifyCheckBox(check, true);
                        if (!SpinWait.SpinUntil(() => !frequency.Current.IsEnabled, 2000))
                            throw new InvalidOperationException("The interval remains enabled after turning off automatic checking.");
                        var ok = Named(dialog, ControlType.Button, Local("OK", "&OK"));
                        FocusWhenReady(ok);
                        PostKey(ok, Keys.Space);
                        if (!SpinWait.SpinUntil(() => new InterfacePreferences(config) is { AutomaticUpdates: false, UpdateIntervalDays: 7 } &&
                            window.Current.IsEnabled, 6000))
                            throw new InvalidOperationException("The new update settings did not persist.");
                        if (languages != "all")
                            FocusWhenReady(settings);
                        else if (!SpinWait.SpinUntil(() => settings.Current.HasKeyboardFocus, 3000))
                            throw new InvalidOperationException("The settings button did not regain keyboard focus.");

                        var support = Named(window, ControlType.Button, Local("Wsparcie i kontakt…", "Suppor&t and contact…"));
                        PostKey(settings, Keys.Tab);
                        if (!SpinWait.SpinUntil(() => support.Current.HasKeyboardFocus, 2500))
                            throw new InvalidOperationException("Tab did not move from Settings to Support and contact.");
                        PostKey(support, Keys.Space);
                        var contact = WaitForOwnedDialog(window, Local("Wsparcie i kontakt", "Support and contact"));
                        string[] labels = [Local("Wesprzyj przez PayPal", "Support via &PayPal"),
                            Local("Wesprzyj przez Revolut", "Support via &Revolut"),
                            Local("Projekt na GitHubie", "Project on &GitHub"),
                            Local("Strona autora", "Author’s &website")];
                        var controls = contact.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().ToArray();
                        foreach (string label in labels)
                        {
                            var link = controls.FirstOrDefault(element => element.Current.ControlType == ControlType.Button &&
                                element.Current.IsKeyboardFocusable && element.Current.Name.StartsWith(label, StringComparison.Ordinal));
                            if (link is null)
                                throw new InvalidOperationException("Missing accessible command link for " + label + ". Observed: " +
                                    string.Join(" | ", controls.Select(control => $"{control.Current.ControlType.ProgrammaticName} {control.Current.Name}")));
                            if (languages != "all")
                            {
                                FocusWhenReady(link);
                                Thread.Sleep(280);
                            }
                        }
                        Invoke(Named(contact, ControlType.Button, Local("Zamknij", "&Close")));
                        Console.WriteLine($"{locale} {theme}: native settings checkbox, interval, manual button, focus and four support links.");
                    }
                    finally { CloseReleaseFixture(process); }
                }
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    internal static void PublicUpdate(string older, string reference, string workDirectory)
    {
        string folder = Path.Combine(Path.GetFullPath(workDirectory), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string exe = Path.Combine(folder, "XMLator.exe");
        string ini = Path.Combine(folder, "config.ini");
        var preferences = new InterfacePreferences(ini) { Language = "pl", Theme = AppTheme.Dark, UpdateIntervalDays = 7 };
        preferences.Save();
        File.Copy(older, exe);
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(reference)));
        using var installed = Process.Start(exe)!;
        Process? restarted = null;
        try
        {
            var window = WaitForWizard(installed);
            var automatic = WaitForOwnedDialog(window, "Aktualizacja XMLatora", 30000);
            AssertNotes(automatic);
            var no = Named(automatic, ControlType.Button, "Nie");
            if (!no.Current.IsKeyboardFocusable)
                throw new InvalidOperationException("The update offer must allow declining by keyboard.");
            Console.WriteLine("Automatic update offer displays the localized release notes.");
            PressNative(no);
            var settings = Named(window, ControlType.Button, "Ustawienia…");
            if (!SpinWait.SpinUntil(() => settings.Current.IsEnabled && window.Current.IsEnabled, 4000))
                throw new InvalidOperationException("The original wizard did not return after declining the automatic update.");
            PressNative(settings);
            var dialog = WaitForOwnedDialog(window, "Ustawienia");
            PressNative(Named(dialog, ControlType.Button, "Sprawdź aktualizacje teraz"));
            var manual = WaitForOwnedDialog(dialog, "Aktualizacja XMLatora", 30000);
            AssertNotes(manual);
            Console.WriteLine("Declining leaves the wizard open. The manual check offers the same update.");
            PressNative(Named(manual, ControlType.Button, "Tak"));
            if (!installed.WaitForExit(65000))
                throw new InvalidOperationException("The older app did not close to install the GitHub update.");
            if (!SpinWait.SpinUntil(() =>
            {
                restarted = FindOtherInstance(exe, installed.Id);
                return restarted is not null;
            }, 20000))
                throw new InvalidOperationException("XMLator did not restart after the GitHub update.");
            var updated = WaitForWizard(restarted!);
            if (!SpinWait.SpinUntil(() => !Directory.GetFiles(folder, ".xmlator-update-*").Any(), 5000) ||
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))) != hash ||
                new InterfacePreferences(ini) is not { EffectiveLanguage: "pl", Theme: AppTheme.Dark, UpdateIntervalDays: 7 })
                throw new InvalidOperationException("The public update did not leave the exact new binary and saved settings.");
            PressNative(Named(updated, ControlType.Button, "Ustawienia…"));
            var currentSettings = WaitForOwnedDialog(updated, "Ustawienia");
            PressNative(Named(currentSettings, ControlType.Button, "Sprawdź aktualizacje teraz"));
            var latest = WaitForOwnedDialog(currentSettings, "XMLator", 30000);
            Named(latest, ControlType.Text, "Masz aktualną wersję XMLatora.");
            PressNative(Named(latest, ControlType.Button, "OK"));
            Console.WriteLine($"Automatic offer and notes, No, manual check and Yes, real GitHub download, exact SHA-256 {hash}, restart, cleanup and latest-version result.");
        }
        finally
        {
            if (restarted is not null)
            {
                CloseReleaseFixture(restarted);
                restarted.Dispose();
            }
            CloseReleaseFixture(installed);
            Directory.Delete(folder, recursive: true);
        }

        static void AssertNotes(AutomationElement dialog)
        {
            var names = dialog.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
                .Select(item => item.Current.Name);
            if (!names.Any(name => name.Contains("Co się zmieniło", StringComparison.Ordinal) && name.Contains("proszczok.pl", StringComparison.Ordinal)))
                throw new InvalidOperationException("The public release notes are missing from the Polish update dialog.");
        }
    }

    private static Process? FindOtherInstance(string file, int originalId)
    {
        Process? found = null;
        foreach (var process in Process.GetProcessesByName("XMLator"))
        {
            try
            {
                if (found is null && process.Id != originalId && process.MainModule?.FileName == file && process.MainWindowHandle != IntPtr.Zero)
                {
                    found = process;
                    continue;
                }
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            process.Dispose();
        }
        return found;
    }

    private static AutomationElement WaitForOwnedDialog(AutomationElement owner, string title, int timeout = 8000)
    {
        var hwnd = new IntPtr(owner.Current.NativeWindowHandle);
        AutomationElement? result = null;
        if (!SpinWait.SpinUntil(() =>
        {
            var popup = GetWindow(hwnd, 6);
            if (popup == IntPtr.Zero || popup == hwnd)
                return false;
            try
            {
                result = AutomationElement.FromHandle(popup);
                return result.Current.Name == title;
            }
            catch (ElementNotAvailableException) { return false; }
        }, timeout))
            throw new InvalidOperationException("Did not open the dialog " + title);
        return result!;
    }

    private static AutomationElement Named(AutomationElement window, ControlType type, string name) => Find(window, type, name, name);

    private static void FocusWhenReady(AutomationElement element)
    {
        if (!SpinWait.SpinUntil(() =>
        {
            try
            {
                if (!element.Current.IsKeyboardFocusable || element.Current.IsOffscreen || !element.Current.IsEnabled)
                    return false;
                element.SetFocus();
                return element.Current.HasKeyboardFocus;
            }
            catch (InvalidOperationException) { return false; }
        }, 4000))
            throw new InvalidOperationException("Could not focus " + element.Current.Name);
    }

    private static void Invoke(AutomationElement button) => ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    private static void PressNative(AutomationElement button)
    {
        if (!PostMessage(new IntPtr(button.Current.NativeWindowHandle), 0x00F5, IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException("Could not press " + button.Current.Name);
    }

    private static void CloseReleaseFixture(Process app)
    {
        if (app.HasExited)
            return;
        app.Kill();
        app.WaitForExit();
    }

}
