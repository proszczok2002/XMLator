using System.Diagnostics;
using System.Runtime.InteropServices;
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
                        if (!window.Current.Name.StartsWith($"{Wizard.ProductCaption} — ", StringComparison.Ordinal))
                            throw new InvalidOperationException("The main window title does not include the display version.");
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
                        var contactHandle = new IntPtr(contact.Current.NativeWindowHandle);
                        if (theme != AppTheme.System && !SystemInformation.HighContrast)
                        {
                            Color background = ReadDialogBackground(contactHandle);
                            if (theme == AppTheme.Dark ? background.ToArgb() != Wizard.DarkSurface.ToArgb() : background.GetBrightness() < 0.65)
                                throw new InvalidOperationException($"Wrong support dialog background for {theme}: {background}.");
                        }
                        string[] labels = [Local("Wesprzyj przez PayPal", "Support via &PayPal"),
                            Local("Wesprzyj przez Revolut", "Support via &Revolut"),
                            Local("Projekt na GitHubie", "Project on &GitHub"),
                            Local("Strona autora", "Author’s &website"), Local("Zamknij", "&Close")];
                        var buttons = labels.Select(label => Named(contact, ControlType.Button, label)).ToArray();
                        if (buttons.Any(button => !button.Current.IsKeyboardFocusable || button.Current.NativeWindowHandle == 0 ||
                            button.Current.HelpText.Contains("https://", StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidOperationException("All support actions must be native, keyboard-focusable buttons without a URL in their help.");
                        var dialogText = contact.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>();
                        if (dialogText.Any(control => control.Current.Name.Contains("https://", StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidOperationException("A URL is exposed in the support dialog.");
                        for (int i = 0; i < buttons.Length; i++)
                        {
                            var button = buttons[i];
                            if (!SpinWait.SpinUntil(() => button.Current.HasKeyboardFocus, 2500))
                                throw new InvalidOperationException("Tab order or initial keyboard focus is wrong at " + labels[i]);
                            if (languages != "all")
                                Thread.Sleep(280);
                            PostKey(button, i + 1 < buttons.Length ? Keys.Tab : Keys.Escape);
                        }
                        if (!SpinWait.SpinUntil(() => !IsWindow(contactHandle) && support.Current.HasKeyboardFocus, 3500))
                            throw new InvalidOperationException("Escape did not close Support and return focus to its button.");
                        var manualButton = Named(window, ControlType.Button, Local("Podręcznik użytkowania", "User manual"));
                        PostKey(support, Keys.Tab);
                        if (!SpinWait.SpinUntil(() => manualButton.Current.HasKeyboardFocus, 2500))
                            throw new InvalidOperationException("Tab did not reach the user manual button.");
                        Console.WriteLine($"{locale} {theme}: versioned title, settings, support, Tab and Escape, user manual button.");
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
        var lastCheck = DateTimeOffset.UtcNow.AddMinutes(-1);
        var preferences = new InterfacePreferences(ini)
        {
            Language = "pl", Theme = AppTheme.Dark, UpdateIntervalDays = 0, LastUpdateCheck = lastCheck
        };
        preferences.Save();
        File.Copy(older, exe);
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(reference)));
        Process? installed = null;
        Process? restarted = null;
        try
        {
            using (var declined = Process.Start(exe)!)
            {
                try
                {
                    var initial = WaitForWizard(declined);
                    var offer = WaitForOwnedDialog(initial, "Aktualizacja XMLatora", 30000);
                    AssertNotes(offer);
                    var no = Named(offer, ControlType.Button, "Nie");
                    if (!no.Current.IsKeyboardFocusable)
                        throw new InvalidOperationException("The update offer must allow declining by keyboard.");
                    PressNative(no);
                    if (!SpinWait.SpinUntil(() => initial.Current.IsEnabled, 5000))
                        throw new InvalidOperationException("The original wizard did not return after declining the automatic update.");
                    if (new InterfacePreferences(ini).LastUpdateCheck is not DateTimeOffset checkedAt || checkedAt <= lastCheck)
                        throw new InvalidOperationException("The first startup did not save the time of the check.");
                    if (!declined.CloseMainWindow() || !declined.WaitForExit(5000))
                        throw new InvalidOperationException("Could not close XMLator after declining the update.");
                    Console.WriteLine("The automatic offer appears after a recent check and allows declining.");
                }
                finally { CloseReleaseFixture(declined); }
            }
            installed = Process.Start(exe)!;
            var window = WaitForWizard(installed);
            var automatic = WaitForOwnedDialog(window, "Aktualizacja XMLatora", 30000);
            AssertNotes(automatic);
            Console.WriteLine("Reopening the app immediately offers the update again without opening Settings.");
            var yes = Named(automatic, ControlType.Button, "Tak");
            if (!yes.Current.IsKeyboardFocusable)
                throw new InvalidOperationException("The update offer must allow accepting by keyboard.");
            PressNative(yes);
            if (!installed.WaitForExit(65000))
                throw new InvalidOperationException("The older app did not close to install the GitHub update.");
            if (!SpinWait.SpinUntil(() =>
            {
                restarted = FindOtherInstance(exe, installed.Id);
                return restarted is not null;
            }, 20000))
                throw new InvalidOperationException("XMLator did not restart after the GitHub update.");
            var updated = WaitForWizard(restarted!);
            string productVersion = FileVersionInfo.GetVersionInfo(reference).ProductVersion!.Split('+', 2)[0];
            if (!updated.Current.Name.StartsWith($"XMLator {productVersion} — ", StringComparison.Ordinal))
                throw new InvalidOperationException("The updated executable does not expose its version in the title.");
            if (!SpinWait.SpinUntil(() => !Directory.GetFiles(folder, ".xmlator-update-*").Any(), 5000) ||
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))) != hash ||
                new InterfacePreferences(ini) is not { EffectiveLanguage: "pl", Theme: AppTheme.Dark, AutomaticUpdates: true, UpdateIntervalDays: 0 })
                throw new InvalidOperationException("The public update did not leave the exact new binary and saved settings.");
            PressNative(Named(updated, ControlType.Button, "Ustawienia…"));
            var currentSettings = WaitForOwnedDialog(updated, "Ustawienia");
            PressNative(Named(currentSettings, ControlType.Button, "Sprawdź aktualizacje teraz"));
            var latest = WaitForOwnedDialog(currentSettings, "XMLator", 30000);
            Named(latest, ControlType.Text, "Masz aktualną wersję XMLatora.");
            PressNative(Named(latest, ControlType.Button, "OK"));
            Console.WriteLine($"Automatic Yes, real GitHub download, exact SHA-256 {hash}, restart, cleanup and latest-version result.");
        }
        finally
        {
            if (restarted is not null)
            {
                CloseReleaseFixture(restarted);
                restarted.Dispose();
            }
            if (installed is not null)
            {
                CloseReleaseFixture(installed);
                installed.Dispose();
            }
            Directory.Delete(folder, recursive: true);
        }

        static void AssertNotes(AutomationElement dialog)
        {
            var names = dialog.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
                .Select(item => item.Current.Name);
            if (!names.Any(name => name.Contains("Co się zmieniło", StringComparison.Ordinal) && name.Contains("• ", StringComparison.Ordinal)))
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

    private static Color ReadDialogBackground(IntPtr window)
    {
        using var bitmap = new Bitmap(8, 8);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Magenta);
        var dc = graphics.GetHdc();
        try
        {
            const uint clientOnly = 1;
            if (!PrintWindow(window, dc, clientOnly))
                throw new InvalidOperationException("Could not render the support dialog for the theme test.");
        }
        finally { graphics.ReleaseHdc(dc); }
        return bitmap.GetPixel(2, 2);
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);

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
