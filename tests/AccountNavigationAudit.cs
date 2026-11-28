using System.Diagnostics;
using System.Windows.Automation;
using XMLator;

internal static partial class SelectionAudit
{
    internal static void AccountNavigation(string binary, string languages, string workDirectory)
    {
        string folder = Path.Combine(Path.GetFullPath(workDirectory), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string executable = Path.Combine(folder, "XMLator.exe");
        string ini = Path.Combine(folder, "AccountTest.ini");
        File.Copy(binary, executable);
        IniProfile.Save(ini, new SetupProfile { CreateUsers = true });
        try
        {
            foreach (var language in UiText.Languages.Where(item => languages == "all" || item.Value == languages))
            {
                string Local(string polish, string english) =>
                    (language.Value == "pl" ? polish : UiText.Get(language.Value, english)).Replace("&", "");
                new InterfacePreferences(Path.Combine(folder, "config.ini"))
                {
                    Language = language.Value, Theme = AppTheme.Dark, AutomaticUpdates = false
                }.Save();
                var start = new ProcessStartInfo(executable);
                start.ArgumentList.Add(ini);
                using var process = Process.Start(start)!;
                try
                {
                    var window = WaitForWizard(process);
                    string nextLabel = Local("Dalej", "&Next");
                    for (int step = 0; step < 5; step++)
                    {
                        string previous = window.Current.Name;
                        PressNative(Named(window, ControlType.Button, nextLabel));
                        if (!SpinWait.SpinUntil(() => window.Current.Name != previous, 3500))
                            throw new InvalidOperationException($"Could not enter step {step + 2}.");
                    }
                    string accountStep = window.Current.Name;
                    if (!accountStep.EndsWith(Local("Użytkownicy i pierwsze logowanie", "Users and first sign-in"), StringComparison.Ordinal))
                        throw new InvalidOperationException("Could not reach the account step.");
                    var mode = Named(window, ControlType.ComboBox,
                        Local("Czy utworzyć konta lokalne podczas instalacji?", "&Create local accounts during installation?"));
                    var add = Named(window, ControlType.Button, Local("Dodaj konto…", "&Add account…"));
                    string yes = Local("Tak. Dodaj konta w kreatorze.", "Yes. Add accounts in the wizard.");
                    string no = Local("Nie. Skonfiguruj konto w instalatorze.", "No. Set up the account in Setup.");
                    if (ReadValue(mode) != yes)
                        throw new InvalidOperationException("The test profile did not select local account setup.");
                    PressNative(Named(window, ControlType.Button, nextLabel));
                    var dialog = WaitForOwnedDialog(window, "XMLator");
                    Named(dialog, ControlType.Text, Local("Dodaj co najmniej jedno konto lokalne.", "Add at least one local account."));
                    PressNative(Named(dialog, ControlType.Button, "OK"));
                    if (!SpinWait.SpinUntil(() => window.Current.Name == accountStep && add.Current.HasKeyboardFocus, 3500))
                        throw new InvalidOperationException("The wizard did not return focus to Add account.");

                    VerifyChange(mode, yes, no, Keys.Up);
                    PressNative(Named(window, ControlType.Button, nextLabel));
                    string advanced = Local("Ustawienia zaawansowane", "Advanced settings");
                    if (!SpinWait.SpinUntil(() => window.Current.Name.EndsWith(advanced, StringComparison.Ordinal), 3500))
                        throw new InvalidOperationException("Choosing account setup in Windows did not allow continuing.");
                    Console.WriteLine($"{language.Value}: localized error, focus on Add account, and manual setup continues.");
                }
                finally { CloseReleaseFixture(process); }
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
