using System.Reflection;
using System.Text.Json;
using XMLator;

internal static class WizardDialogAudit
{
    private const BindingFlags Members = BindingFlags.NonPublic | BindingFlags.Instance;

    internal static void VerifySignInPrompts()
    {
        var folder = Directory.CreateTempSubdirectory("XMLator-password-prompt-");
        string iso = Path.Combine(folder.FullName, "fixture.iso");
        File.WriteAllBytes(iso, []);
        try
        {
            using var wizard = new Wizard(null)
            {
                FindMicrosoftIsos = (_, _, _, _, _, _) => Task.FromResult(Array.Empty<IsoProduct>()),
                FindSiteIsos = (_, _, _) => Task.FromResult(Array.Empty<ProszczokIso>())
            };
            var type = typeof(Wizard);
            var profile = (SetupProfile)type.GetField("profile", Members)!.GetValue(wizard)!;
            profile.TargetName = "Windows Server 2008";
            profile.CreateUsers = true;
            profile.Logon = LogonMode.FirstAdministrator;
            profile.Users.Add(new LocalUser { Name = "PasswordRequired" });
            var disk = new UsbDisk(999, 32L << 30, "XMLator test fixture", "XMLator test ID", "Test USB", "");
            type.GetField("usbDisks", Members)!.SetValue(wizard, new[] { disk });
            type.GetField("usbTarget", Members)!.SetValue(wizard, disk);
            type.GetField("usbIsoPath", Members)!.SetValue(wizard, iso);
            type.GetField("step", Members)!.SetValue(wizard, 8);
            wizard.Show();
            int prompts = 0;
            foreach (var language in UiText.Languages)
            {
                type.GetMethod("ChangeLanguage", Members)!.Invoke(wizard, [language.Value]);
                type.GetMethod("ShowStep", Members)!.Invoke(wizard, [true]);
                bool polish = language.Value == "pl";
                string caption = polish ? "Dane tej instalacji" : UiText.Get(language.Value, "Details for this installation");
                string password = polish ? "Hasło konta 1" : UiText.Format(language.Value, $"Account {1} password");
                string[] actions = polish ? ["Zapisz XML…", "Utwórz bootowalny pendrive"] :
                    [UiText.Get(language.Value, "&Save XML…").Replace("&", ""), UiText.Get(language.Value, "&Create a bootable USB drive").Replace("&", "")];
                foreach (var action in actions)
                {
                    bool correctPrompt = false;
                    var button = Controls(wizard).OfType<Button>().Single(control => control.Visible && control.AccessibleName == action);
                    if (!TemporaryIso.PumpUntil(() => button.Enabled))
                        throw new InvalidOperationException($"Export did not become available for {action} ({language.Value}).");
                    using var timer = new System.Windows.Forms.Timer { Interval = 60 };
                    timer.Tick += (_, _) =>
                    {
                        var dialog = Application.OpenForms.Cast<Form>().SingleOrDefault(form => form.Modal && form.Text == caption);
                        if (dialog is null)
                            return;
                        timer.Stop();
                        correctPrompt = Controls(dialog).OfType<TextBox>().Any(box => box.Focused &&
                            box.AccessibleName == password && box.UseSystemPasswordChar && box.Text == "");
                        dialog.CancelButton!.PerformClick();
                    };
                    button.Select();
                    timer.Start();
                    button.PerformClick();
                    if (!correctPrompt || profile.Users.Single().Password != "")
                        throw new InvalidOperationException($"Missing or invalid password prompt for {action} ({language.Value}).");
                    prompts++;
                }
            }
            Console.WriteLine($"{prompts} matching, masked Windows Server 2008 password prompts on Save XML and Create USB, across eight languages.");
            wizard.Dispose();
        }
        finally { folder.Delete(recursive: true); }
    }

    internal static void Run(AppTheme theme = AppTheme.System, bool diskSettingsOnly = false, bool methodsOnly = false)
    {
        using var wizard = new Wizard(null, new InterfacePreferences { Theme = theme })
        {
            FindMicrosoftIsos = (_, _, _, _, _, _) => Task.FromResult(Array.Empty<IsoProduct>()),
            FindSiteIsos = (_, _, _) => Task.FromResult(Array.Empty<ProszczokIso>())
        };
        var type = typeof(Wizard);
        type.GetField("usbDisks", Members)!.SetValue(wizard, Array.Empty<UsbDisk>());
        var profile = (SetupProfile)type.GetField("profile", Members)!.GetValue(wizard)!;
        var step = type.GetField("step", Members)!;
        var show = type.GetMethod("ShowStep", Members)!;
        wizard.Show();
        Application.DoEvents();
        int screens = 0;
        int dialogues = 0;
        foreach (var language in UiText.Languages)
        {
            bool polish = language.Value == "pl";
            using var resource = typeof(UiText).Assembly.GetManifestResourceStream($"XMLator.Translations.{language.Value}.json");
            var translations = resource is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(resource) ?? [];
            string Lang(string pl, string en) => polish ? pl : translations.FirstOrDefault(pair =>
                pair.Key.Replace("&", "", StringComparison.Ordinal) == en).Value?.Replace("&", "", StringComparison.Ordinal) ?? en;
            type.GetMethod("ChangeLanguage", Members)!.Invoke(wizard, [language.Value]);
            if (methodsOnly)
            {
                CheckMethodSettings();
                screens += 3;
                continue;
            }
            if (diskSettingsOnly)
            {
                foreach (var mode in new[] { DiskMode.Interactive, DiskMode.Available })
                {
                    profile.DiskMode = mode;
                    Show(3);
                    CheckFields(wizard);
                    screens++;
                    CheckDiskSettings(mode == DiskMode.Available);
                }
                continue;
            }
            foreach (var menu in wizard.MainMenuStrip!.Items.OfType<ToolStripMenuItem>())
            {
                if (string.IsNullOrWhiteSpace(menu.AccessibilityObject.Name) ||
                    menu.DropDownItems.OfType<ToolStripMenuItem>().Any(item => string.IsNullOrWhiteSpace(item.AccessibilityObject.Name)))
                    throw new InvalidOperationException("Unnamed menu item: " + menu.Text);
                Console.WriteLine($"Menu {menu.Text!.Replace("&", "")}: {menu.DropDownItems.Count} named items.");
            }

            for (int i = 0; i < 9; i++)
            {
                Show(i);
                CheckFields(wizard);
                screens++;
            }

            Show(0);
            Open(wizard, polish ? "Ustawienia…" : UiText.Get(language.Value, "&Settings…").Replace("&", ""), dialog =>
            {
                var lists = Controls(dialog).OfType<ComboBox>().ToArray();
                if (dialog.Text != (polish ? "Ustawienia" : UiText.Get(language.Value, "Settings")) || lists.Length != 2 ||
                    !lists[0].Focused || lists[0].SelectedItem is not Choice<string> item || item.Value != language.Value ||
                    lists[0].Items.Count != 9 || lists[1].Items.Count != 3 || lists.Any(list => !list.Visible ||
                        list.DropDownStyle != ComboBoxStyle.DropDownList || !dialog.ClientRectangle.Contains(
                            dialog.RectangleToClient(list.RectangleToScreen(list.ClientRectangle)))))
                    throw new InvalidOperationException("Incorrect interface settings dialog in " + language.Label);
            });

            profile.DiskMode = DiskMode.Interactive;
            Show(3);
            CheckDiskSettings(automaticSelected: false);
            CheckMethodSettings();
            profile.DiskMode = DiskMode.Custom;
            Show(3);
            Open(wizard, Lang("Dodaj, edytuj i ustaw kolejność partycji danych…", "Add, edit and reorder data partitions…"), AddEditor);
            profile.DiskMode = DiskMode.Interactive;

            profile.CreateUsers = true;
            Show(5);
            Open(wizard, Lang("Dodaj konto…", "Add account…"));
            profile.CreateUsers = false;

            Show(6);
            foreach (var caption in new (string Pl, string En)[]
            {
                ("Eksplorator, Start i pasek zadań…", "Explorer, Start and taskbar…"),
                ("System, zabezpieczenia i aktualizacje…", "System, security and updates…"),
                ("Sieć Wi-Fi i maszyny wirtualne…", "Wi-Fi and virtual machines…"),
                ("Personalizacja i ułatwienia dostępu…", "Personalization and accessibility…"),
                ("Zasady haseł i blokady konta…", "Account password and lockout policies…"),
                ("Aplikacje i funkcje do usunięcia…", "Apps and features to remove…")
            })
                Open(wizard, Lang(caption.Pl, caption.En));
            Open(wizard, Lang("Własne składniki XML…", "Custom XML components…"), AddEditor);

            Show(7);
            Open(wizard, Lang("Dodaj…", "Add…"));
            Open(wizard, Lang("Dodaj skrypty osadzone w XML…", "Manage scripts embedded in XML…"), AddEditor);
            Show(8);
            Open(wizard, Lang("Podejrzyj plik XML…", "Preview the XML file…"));

            void CheckMethodSettings()
            {
                string methodDialog = Lang("Ustawienia wybranej metody…", "Settings for the selected method…");
                string compatibility = Lang("Pomiń sprawdzenie zgodności sprzętu z Windows 11", "Skip Windows 11 hardware compatibility checks");
                string checksExplained = Lang("Dotyczy TPM, Secure Boot i RAM. Działanie zależy od kompilacji Windows 11.",
                    "Applies to TPM, Secure Boot and RAM. The result depends on the Windows 11 build.");
                profile.DiskMode = DiskMode.Interactive;
                Show(3);
                Open(wizard, methodDialog, dialog =>
                {
                    var check = Controls(dialog).OfType<CheckBox>().Single(control => control.AccessibleName == compatibility);
                    if (!check.Visible || check.Checked || !Controls(dialog).OfType<Label>().Any(label => label.Visible && label.Text == checksExplained))
                        throw new InvalidOperationException("Missing the explicit Windows 11 hardware check and its explanation.");
                });
                profile.Advanced["PE"] = "Generated";
                profile.DiskMode = DiskMode.SystemAndWindows;
                Show(3);
                Open(wizard, methodDialog, dialog =>
                {
                    if (Controls(dialog).OfType<CheckBox>().Any(control => control.Visible && control.AccessibleName == compatibility))
                        throw new InvalidOperationException("The Windows Setup compatibility check appeared in DISM mode.");
                });
                profile.Advanced["PE"] = "Custom";
                Show(3);
                Open(wizard, methodDialog);
                profile.Advanced.Remove("PE");
                profile.DiskMode = DiskMode.Interactive;
            }

            void CheckDiskSettings(bool automaticSelected) =>
                Open(wizard, Lang("Zaawansowane ustawienia Windows Setup…", "Advanced Windows Setup settings…"), dialog =>
                {
                    var automatic = Controls(dialog).OfType<CheckBox>().Single(check =>
                        check.AccessibleName == Lang("Pozwól instalatorowi wybrać partycję na dowolnym dysku", "Let Setup choose a partition on any disk"));
                    if (!automatic.Visible || automatic.Checked != automaticSelected)
                        throw new InvalidOperationException("The advanced disk selection checkbox did not reflect the profile.");
                });

            void AddEditor(Form dialog) => Open(dialog, Lang("Dodaj…", "Add…"));
        }
        Console.WriteLine($"{screens} main screens and {dialogues} modal forms passed the control and focus audit ({theme}).");
        wizard.Dispose();

        void Show(int index)
        {
            step.SetValue(wizard, index);
            show.Invoke(wizard, [true]);
            Application.DoEvents();
        }

        void Open(Form owner, string caption, Action<Form>? extra = null)
        {
            var button = Controls(owner).OfType<Button>().Single(item => item.Visible && item.Enabled && item.AccessibleName == caption);
            var previous = Application.OpenForms.Cast<Form>().ToHashSet();
            Exception? failure = null;
            bool visited = false;
            using var timer = new System.Windows.Forms.Timer { Interval = 60 };
            timer.Tick += (_, _) =>
            {
                var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(form => !previous.Contains(form));
                if (dialog is null)
                    return;
                timer.Stop();
                visited = true;
                try
                {
                    CheckFields(dialog);
                    dialogues++;
                    Console.WriteLine($"Dialog {dialog.Text}: named controls and initial keyboard focus.");
                    extra?.Invoke(dialog);
                }
                catch (Exception error) { failure = error; }
                finally { dialog.Close(); }
            };
            button.Select();
            timer.Start();
            button.PerformClick();
            if (failure is not null)
                throw new InvalidOperationException("Dialog audit failed: " + caption, failure);
            if (!visited)
                throw new InvalidOperationException("Did not open the requested dialog: " + caption);
        }
    }

    private static void CheckFields(Form form)
    {
        var fields = Controls(form).Where(control => control.Visible && control is TextBox or ComboBox or CheckBox or Button or ListBox).ToArray();
        if (fields.Any(control => string.IsNullOrWhiteSpace(control.AccessibilityObject.Name)) ||
            fields.OfType<TextBox>().Any(box => box.ReadOnly) ||
            fields.OfType<CheckBox>().Any(box => box.AccessibilityObject.Role != AccessibleRole.CheckButton ||
                box.AccessibilityObject.State.HasFlag(AccessibleStates.Checked) != box.Checked))
            throw new InvalidOperationException("Missing accessible name, checkbox state or editable field in " + form.Text);
        if (form.ActiveControl is null || form.Modal && !form.ContainsFocus)
            throw new InvalidOperationException("Missing initial keyboard focus in " + form.Text);
    }

    private static IEnumerable<Control> Controls(Control root) => root.Controls.Cast<Control>()
        .SelectMany(control => new[] { control }.Concat(Controls(control)));
}
