using System.Xml.Linq;

namespace XMLator;

internal sealed partial class Wizard
{
    private void AccountsPage(Fields f)
    {
        var local = new Fields();
        var cleanup = new Fields();
        ComboBox? signIn = null;
        Action? refreshFirstRun = null;
        CheckBox? cleanupCheck = null;
        var signInChoices = new[]
        {
            C(LogonMode.Manual, "Logowanie ręczne", "Sign in manually"),
            C(LogonMode.FirstAdministrator, "Zaloguj raz na pierwsze dodane konto z uprawnieniami administratora",
                "Sign in once as the first added account with administrator permissions")
        };
        void RefreshSignIn()
        {
            if (signIn is null)
                return;
            bool hasAdministrator = profile.FirstLocalAdministrator is not null;
            if (!hasAdministrator)
                profile.Logon = LogonMode.Manual;
            if (signIn.Items.Count != (hasAdministrator ? 2 : 1))
                Fields.UpdateChoices(signIn, profile.Logon, hasAdministrator ? signInChoices : signInChoices.Take(1));
            cleanup.Table.Visible = profile.Logon == LogonMode.FirstAdministrator;
            if (profile.Logon == LogonMode.Manual && cleanupCheck is not null)
                cleanupCheck.Checked = false;
            refreshFirstRun?.Invoke();
        }
        Explain(f.Select(L("Czy &utworzyć konta lokalne podczas instalacji?", "&Create local accounts during installation?"), profile.CreateUsers,
            [C(false, "Nie. Skonfiguruj konto w instalatorze.", "No. Set up the account in Setup."), C(true, "Tak. Dodaj konta w kreatorze.", "Yes. Add accounts in the wizard.")],
            create =>
            {
                profile.CreateUsers = create;
                local.Table.Visible = create;
                RefreshSignIn();
            }), "Wybierz Tak, a następnie użyj przycisku Dodaj konto. Samodzielnie podajesz jego nazwę i wybierasz uprawnienia.",
            "Choose Yes, then use Add account. You enter its name and choose its permissions.");
        var list = local.List(L("Konta lokalne", "Local accounts"), []);
        void Refresh(int index)
        {
            list.Items.Clear();
            list.Items.AddRange(profile.Users.Select(UserLabel).Cast<object>().ToArray());
            if (list.Items.Count > 0)
                list.SelectedIndex = Math.Clamp(index, 0, list.Items.Count - 1);
            RefreshSignIn();
        }
        Refresh(0);
        local.Button(L("&Dodaj konto…", "&Add account…"), () =>
        {
            var user = EditUser(new LocalUser());
            if (user is null)
                return;
            profile.Users.Add(user);
            Refresh(profile.Users.Count - 1);
            list.Focus();
        });
        local.Button(L("&Edytuj zaznaczone konto…", "&Edit selected account…"), () =>
        {
            int i = list.SelectedIndex;
            if (i < 0)
                return;
            var updated = EditUser(profile.Users[i].Copy());
            if (updated is not null)
                profile.Users[i] = updated;
            Refresh(i);
            list.Focus();
        });
        local.Button(L("&Usuń zaznaczone konto", "&Remove selected account"), () =>
        {
            int i = list.SelectedIndex;
            if (i < 0)
                return;
            profile.Users.RemoveAt(i);
            Refresh(i);
            list.Focus();
        });
        signIn = Explain(local.Select(L("&Pierwsze logowanie", "&First sign-in"), profile.Logon,
            signInChoices,
            value =>
            {
                profile.Logon = value;
                cleanup.Table.Visible = value == LogonMode.FirstAdministrator;
                if (value == LogonMode.Manual && cleanupCheck is not null)
                    cleanupCheck.Checked = false;
            }), "Jednorazowe logowanie pojawi się po dodaniu lokalnego administratora. Jeśli zostawisz jego hasło puste, wynik zależy od zasad haseł w instalowanym systemie.",
            "One-time sign-in is available after you add a local administrator. If you leave the password blank, the installed system's password policy applies.");
        Explain(local.Check(L("Zapisz hasła w XML w postaci zakodowanej", "Encode passwords in the answer file"),
            profile.ObscurePasswords, value => profile.ObscurePasswords = value),
            "Kodowanie Base64 pozwala uniknąć wyświetlania zwykłego tekstu w XML. Zakodowane hasło można odczytać.",
            "Base64 keeps the password from being shown as plain text in the XML. The encoded password can be read.");
        local.Table.Visible = profile.CreateUsers;
        f.Section(local);
        if (profile.Target.Server)
        {
            f.Check(L("Podaj hasło wbudowanego Administratora przy eksporcie", "Enter the built-in Administrator password on export"),
                profile.SetServerAdministratorPassword, value => profile.SetServerAdministratorPassword = value);
            f.Note(L("Windows Server może zażądać hasła wbudowanego Administratora przed pierwszym logowaniem. " +
                "Ta opcja pozwala podać je w pliku odpowiedzi. Profil INI zapamięta wybór, a hasło podasz przy każdym eksporcie.",
                "Windows Server may require a password for its built-in Administrator before the first sign-in. " +
                "This option sets it in the answer file. The INI profile remembers the choice and asks for the password on every export."));
        }
        if (profile.Target.ModernClient)
        {
            if (profile.Logon == LogonMode.Manual)
                profile.Advanced.Remove("CleanSensitive");
            cleanupCheck = cleanup.Check(L("Usuń kopie pliku odpowiedzi po automatycznym zalogowaniu administratora",
                "Remove cached answer files after automatic administrator sign-in"), profile.Has("CleanSensitive"),
                value => profile.Advanced["CleanSensitive"] = value ? "1" : "0");
            cleanup.Table.Visible = profile.Logon == LogonMode.FirstAdministrator;
            local.Section(cleanup);
            refreshFirstRun = FirstRunPageOption(f);
        }
        else if (!profile.Target.Server)
        {
            if (profile.Target.ClientOobeNetwork)
            {
                f.Check(L("Pomiń pytanie o Wi-Fi", "Skip the Wi-Fi question"), profile.HideWireless, value => profile.HideWireless = value);
                f.Check(L("Ukryj ekrany logowania do konta Microsoft", "Hide Microsoft account sign-in screens"), profile.HideOnline,
                    value => profile.HideOnline = value);
            }
            ExpressPageOption(f);
        }
        RefreshSignIn();
    }

    private string UserLabel(LocalUser user)
    {
        string name = user.AskName ? L("Nazwa wpisywana przy tworzeniu", "Name entered on export") : user.Name;
        return $"{name} — {(user.Administrator ? L("Administratorzy", "Administrators") : L("Użytkownicy", "Users"))}";
    }

    private LocalUser? EditUser(LocalUser user)
    {
        var fields = new Fields();
        var name = fields.Entry(L("&Nazwa konta", "Account &name"), user.Name, value => user.Name = value.Trim());
        fields.Entry(L("Nazwa &wyświetlana (opcjonalnie)", "&Display name (optional)"), user.DisplayName, value => user.DisplayName = value.Trim());
        Explain(fields.Select(L("&Uprawnienia konta", "Account &permissions"), user.Administrator,
            [C(true, "Administrator", "Administrator"), C(false, "Użytkownik standardowy", "Standard user")], value => user.Administrator = value),
            "Administrator może zmieniać ustawienia systemu. Użytkownik standardowy ma ograniczone uprawnienia.",
            "An administrator can change system settings. A standard user has limited permissions.");
        bool passwordRequired = profile.Target.RequiresLocalAccountPassword;
        Explain(fields.Entry(passwordRequired ? L("&Hasło konta", "Account &password") : L("&Hasło (opcjonalnie)", "&Password (optional)"),
                user.Password, value => user.Password = value, secret: true),
            passwordRequired ? "Windows Server 2008 wymaga złożonych haseł dla kont lokalnych. Możesz podać hasło teraz lub przy eksporcie." :
                "Windows może przy pierwszym logowaniu poprosić o ustawienie hasła, jeśli pozostawisz to pole puste.",
            passwordRequired ? "Windows Server 2008 requires complex passwords for local accounts. Enter the password here or on export." :
                "Windows may ask you to set a password at the first sign-in if you leave this field empty.");
        fields.Check(L("Pytaj o nazwę i nazwę wyświetlaną przy generowaniu dla klienta", "Ask for the account and display names when exporting for a customer"),
            user.AskName, value => user.AskName = value);
        fields.Check(L("Pytaj o hasło przy każdym generowaniu", "Ask for the password on each export"), user.AskPassword, value => user.AskPassword = value);
        return Ask(L("Konto lokalne", "Local account"), fields, name, validate: () => AnswerFile.ValidateUser(user, template: true)) ? user : null;
    }

    private void MediaPage(Fields f)
    {
        f.Note(L("Przycisk Utwórz bootowalny pendrive dołącza te pliki i Autounattend.xml. Przycisk Zapisz XML może zapisać je jako strukturę do skopiowania na nośnik.",
            "Create a bootable USB drive includes these files and Autounattend.xml. Save XML can export them as a folder to copy onto installation media."));
        FolderField(f, L("Sterowniki dla &Windows PE (folder z INF)", "Drivers for &Windows PE (folder with INF files)"),
            profile.WinPeDrivers, value => profile.WinPeDrivers = value);
        if (profile.Target.ModernClient && profile.Option("PE") == "Generated" || profile.WinPePackages.Length > 0)
            FolderField(f, L("Pakiety CAB lub MSU dla instalowanego Windows (tryb DISM)", "CAB or MSU packages for the installed Windows (DISM mode)"),
                profile.WinPePackages, value => profile.WinPePackages = value);
        FolderField(f, L("Sterowniki dla &instalowanego Windows (folder z plikami INF)", "Drivers for the target &Windows (folder with INF files)"),
            profile.InstalledDrivers, value => profile.InstalledDrivers = value);
        FolderField(f, L("Folder plików do głównego katalogu &dysku systemowego", "Files to copy to the root of the system &drive"),
            profile.SystemFiles, value => profile.SystemFiles = value);

        EditableList(f, L("Pliki i skrypty OEM", "OEM files and scripts"), profile.OemFiles,
            file => $"{Path.GetFileName(file.Path)} — {OemLabel(file.Phase)}", EditOem);
        if (profile.Target.ModernClient)
            f.Button(L("Dodaj skrypty &osadzone w XML…", "Manage scripts &embedded in XML…"), EditEmbeddedScripts);
    }

    private void FolderField(Fields f, string title, string value, Action<string> changed)
    {
        var input = Explain(f.Entry(title, value, text => changed(text.Trim().Trim('"'))),
            "Przyciskiem poniżej wybierz folder. Jego zawartość trafi do pakietu do skopiowania na pendrive.",
            "Use the button below to choose a folder. Its contents go into the package you can copy onto the USB drive.");
        f.Button(L("Wybierz folder. ", "Browse for folder. ") + title.Replace("&", "") + "…", () =>
        {
            using var dialog = new FolderBrowserDialog { Description = title.Replace("&", ""), UseDescriptionForTitle = true, SelectedPath = input.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                input.Text = dialog.SelectedPath;
        });
    }

    private void EditableList<T>(Fields f, string label, List<T> items, Func<T, string> format, Func<T?, T?> edit, bool allowReorder = false) where T : class
    {
        var control = f.List(label, []);
        void Refresh(int selected)
        {
            control.Items.Clear();
            control.Items.AddRange(items.Select(format).Cast<object>().ToArray());
            if (items.Count > 0)
                control.SelectedIndex = Math.Clamp(selected, 0, items.Count - 1);
        }
        Refresh(0);
        f.Button(L("Dodaj…", "Add…"), () =>
        {
            Run(() =>
            {
                if (edit(null) is T item)
                {
                    items.Add(item);
                    Refresh(items.Count - 1);
                    control.Focus();
                }
            });
        });
        f.Button(L("Edytuj zaznaczoną pozycję…", "Edit selected item…"), () =>
        {
            int i = control.SelectedIndex;
            if (i >= 0)
                Run(() =>
                {
                    if (edit(items[i]) is T item)
                    {
                        items[i] = item;
                        Refresh(i);
                        control.Focus();
                    }
                });
        });
        f.Button(L("Usuń zaznaczoną pozycję", "Remove selected item"), () =>
        {
            if (control.SelectedIndex is int index and >= 0)
            {
                items.RemoveAt(index);
                Refresh(index);
                control.Focus();
            }
        });
        if (allowReorder)
        {
            void MoveItem(int offset)
            {
                int index = control.SelectedIndex;
                int to = index + offset;
                if (index < 0 || to < 0 || to >= items.Count)
                    return;
                (items[index], items[to]) = (items[to], items[index]);
                Refresh(to);
                control.Focus();
            }
            f.Button(L("Przesuń zaznaczoną pozycję w górę", "Move the selected item up"), () => MoveItem(-1));
            f.Button(L("Przesuń zaznaczoną pozycję w dół", "Move the selected item down"), () => MoveItem(1));
        }
    }

    private string OemLabel(OemPhase phase) => phase switch
    {
        OemPhase.Copy => L("Tylko kopiuj do folderu XMLator\\Files", "Copy to XMLator\\Files"),
        OemPhase.Specialize => L("Przebieg specialize, jako SYSTEM", "Specialize pass, as SYSTEM"),
        OemPhase.SetupComplete => L("SetupComplete.cmd, jeśli obsługiwany przez nośnik", "SetupComplete.cmd where supported by the media"),
        _ => L("Pierwsze logowanie", "First sign-in")
    };

    private OemFile? EditOem(OemFile? existing)
    {
        string path = existing?.Path ?? "";
        OemPhase phase = existing?.Phase ?? OemPhase.Copy;
        var fields = new Fields();
        var pathBox = fields.Entry(L("Ścieżka &pliku", "&File path"), path, value => path = value.Trim().Trim('"'));
        fields.Button(L("&Wybierz plik…", "&Browse for file…"), () =>
        {
            using var dialog = new OpenFileDialog { FileName = path };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                pathBox.Text = dialog.FileName;
        });
        fields.Select(L("&Kiedy uruchomić plik?", "&When should Setup run this file?"), phase,
            Enum.GetValues<OemPhase>().Select(p => new Choice<OemPhase>(p, OemLabel(p))), value => phase = value);
        return Ask(L("Plik OEM", "OEM file"), fields, pathBox, validate: () =>
        {
            if (!File.Exists(path))
                throw new SetupException("Wybierz istniejący plik.", "Select an existing file.");
            if (phase != OemPhase.Copy && !new[] { ".cmd", ".bat", ".ps1", ".vbs", ".js", ".reg", ".exe" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                throw new SetupException("Wybierz skrypt CMD, BAT, PS1, VBS, JS, REG lub EXE.", "Select a CMD, BAT, PS1, VBS, JS, REG or EXE script.");
        }) ? new OemFile(path, phase) : null;
    }

    private void ReviewPage(Fields f)
    {
        var lines = new List<string>
        {
            $"{profile.TargetName} ({string.Join(", ", new[] { profile.Architecture }.Concat(profile.ExtraArchitectures))})",
            profile.AcceptEula ? L("Automatyczna akceptacja licencji", "Automatically accept the license") : L("Licencja do zaakceptowania w instalatorze", "Accept the license in Setup"),
            profile.ImageMode switch
            {
                ImageMode.Edition => L("Wybrana edycja — ", "Selected edition — ") + profile.SelectedEdition?.Label,
                ImageMode.ExactName => L("Nazwa obrazu — ", "Image name — ") + profile.ImageName,
                ImageMode.Index => L("Indeks obrazu — ", "Image index — ") + profile.ImageIndex,
                _ => L("Edycja do wybrania w instalatorze", "Choose the edition in Setup")
            }
        };
        lines.AddRange(DiskSummary());
        if (profile.KeyMode is KeyMode.Skip or KeyMode.Firmware)
            lines.Add(L("Klucz instalacyjny — pomiń pytanie, bez wpisywania klucza w XML.",
                "Setup product key — skip the prompt, supply no setup key in XML."));
        if (profile.CreateUsers)
        {
            lines.AddRange(profile.Users.Select(UserLabel));
            if (profile.Logon == LogonMode.Manual)
                lines.Add(L("Pierwsze logowanie — ręczne.", "First sign-in — manual."));
            else if (profile.FirstLocalAdministrator is LocalUser user)
                lines.Add(user.AskName
                    ? L("Jednorazowe logowanie — pierwsze dodane konto administratora.", "One-time sign-in — the first added administrator account.")
                    : LF($"Jednorazowe logowanie — {user.Name}.", $"One-time sign-in — {user.Name}."));
            if (profile.Target.Windows11 && profile.Users is [var onlyUser] && onlyUser.Administrator &&
                onlyUser.Password.Length == 0 && !onlyUser.AskPassword)
                lines.Add(L("Windows 11 może sam zalogować jedyne konto bez hasła także po restarcie.",
                    "Windows 11 may also sign in to the only account without a password after a restart."));
        }
        if (profile.Target.Server && profile.SetServerAdministratorPassword)
            lines.Add(L("Hasło wbudowanego Administratora Windows Server zostanie podane przy eksporcie.",
                "The built-in Windows Server Administrator password will be entered on export."));
        if (profile.Target.ModernClient)
        {
            if (profile.SelectedFirstRun == FirstRunMode.SkipSupported)
                lines.Add(L("Pomiń obsługiwane ekrany pierwszej konfiguracji.",
                    "Windows should skip supported first-run screens."));
            if (profile.SelectedFirstRun == FirstRunMode.Offline)
                lines.Add(L("Pierwsza konfiguracja bez internetu. Użyj dodanego konta lokalnego.",
                    "Initial setup without internet using the added local account."));
            if (profile.Target.Windows11 && profile.Has("BypassNetworkCheck"))
                lines.Add(L("Pierwsza konfiguracja — włącz BypassNRO, jeśli obsługuje to kompilacja Windows.",
                    "Initial setup — enable BypassNRO if supported by the Windows build."));
            if (profile.SkipWifi || profile.SelectedFirstRun == FirstRunMode.Custom && profile.HideWireless)
                lines.Add(L("Wi-Fi — pomiń ekran wyboru sieci.", "Wi-Fi — skip the network selection screen."));
            int removedApps = ModernOptions.Engine.Bloatwares.Values.Count(app => profile.Has("App." + app.Id));
            if (removedApps > 0)
                lines.Add(LF($"Aplikacje i składniki zaznaczone do usunięcia — {removedApps}.",
                    $"Apps and components selected for removal — {removedApps}."));
        }
        if (profile.Target.ModernClient && profile.DarkInstalledWindows)
            lines.Add(L("Ciemny motyw w instalowanym Windows.", "Dark theme in the installed Windows."));
        if (profile.WinPeDrivers.Length > 0)
            lines.Add(L("Pendrive i pakiet eksportu — sterowniki Windows PE.", "USB and export package — Windows PE drivers."));
        if (profile.InstalledDrivers.Length > 0)
            lines.Add(L("Pendrive i pakiet eksportu — sterowniki instalowanego Windows.", "USB and export package — drivers for the installed Windows."));
        if (profile.WinPePackages.Length > 0)
            lines.Add(L("Pendrive i pakiet eksportu — pakiety CAB lub MSU dla instalowanego Windows.",
                "USB and export package — CAB or MSU packages for the installed Windows."));
        if (profile.SystemFiles.Length > 0)
            lines.Add(L("Pendrive i pakiet eksportu — pliki do katalogu głównego dysku systemowego.",
                "USB and export package — files for the root of the system drive."));
        if (profile.OemFiles.Count > 0)
            lines.Add(LF($"Pendrive i pakiet eksportu — pliki i skrypty OEM ({profile.OemFiles.Count}).",
                $"USB and export package — OEM files and scripts ({profile.OemFiles.Count})."));
        if (profile.Components.Count > 0 || profile.Target.ModernClient && profile.EmbeddedScripts.Count > 0)
            lines.Add(L("Autounattend.xml zawiera własne składniki XML lub osadzone skrypty.",
                "Autounattend.xml contains custom XML components or embedded scripts."));
        f.List(L("Podsumowanie ustawień", "Settings summary"), lines, height: 168);
        if (profile.RequiresDiskConsent)
            DiskApproval(f);
        f.Button(L("Podejrzyj plik &XML…", "Preview the &XML file…"), () => Run(() =>
        {
            if (BuildInstallationAnswer() is XDocument document)
                ShowXmlPreview(document.ToString());
        }));
        if (lastOutput is not null)
            f.Button(L("Ot&wórz ostatni folder eksportu", "Ope&n last export folder"), () => Run(() =>
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(lastOutput) { UseShellExecute = true })));
        var files = new Fields();
        files.Button(L("&Zapisz XML…", "&Save XML…"), BeginExport);
        files.Button(L("Zapisz profi&l INI…", "Save INI &profile…"), () => Run(SaveProfile));
        f.Group(L("Zapis plików", "Save files"), files);

        var usb = new Fields();
        UsbOptions(usb);
        f.Group(L("Pendrive instalacyjny", "Installation USB drive"), usb);
    }

    private XDocument? BuildInstallationAnswer()
    {
        try { return ResolveCustomer(AnswerFile.Build); }
        catch (Exception error)
        {
            Error(error);
            return null;
        }
    }

    private T? ResolveCustomer<T>(Func<SetupProfile, T> operation) where T : class
    {
        var original = profile.Users;
        string setupKey = profile.SetupKey;
        string activationKey = profile.ActivationKey;
        var session = new Dictionary<string, string>(profile.SessionOptions);
        try
        {
            var fields = new Fields();
            Control? first = null;
            if (profile.KeyMode == KeyMode.Enter && setupKey.Length == 0)
                first = fields.Entry(L("Klucz instalacyjny", "Setup product key"), "", value => profile.SetupKey = value.Trim(), secret: true);
            if (profile.UseActivationKey && activationKey.Length == 0)
            {
                var entry = fields.Entry(L("Klucz aktywacji", "Activation key"), "", value => profile.ActivationKey = value.Trim(), secret: true);
                first ??= entry;
            }
            if (profile.Target.Server && profile.SetServerAdministratorPassword)
            {
                var entry = fields.Entry(L("Hasło wbudowanego Administratora Windows Server", "Built-in Windows Server Administrator password"),
                    "", value => profile.SessionOptions["ServerAdministratorPassword"] = value, secret: true);
                first ??= entry;
            }
            if (profile.Target.ModernClient && profile.Option("WifiMode") is "WPA2PSK" or "WPA3SAE" && profile.Option("WifiPassword").Length == 0)
            {
                var entry = fields.Entry(L("Hasło Wi-Fi", "Wi-Fi password"), "", value => profile.SessionOptions["WifiPassword"] = value, secret: true);
                first ??= entry;
            }
            if (profile.Target.ModernClient && profile.Option("WifiMode") == "XML" && profile.Option("WifiXml").Length == 0)
            {
                var entry = fields.Entry(L("Profil WLAN w XML", "WLAN profile XML"), "", value => profile.SessionOptions["WifiXml"] = value, multiline: true);
                first ??= entry;
            }
            profile.Users = original.Select(u => u.Copy()).ToList();
            if (profile.CreateUsers)
            {
                for (int i = 0; i < profile.Users.Count; i++)
                {
                    LocalUser user = profile.Users[i];
                    int number = i + 1;
                    if (user.AskName)
                    {
                        var entry = fields.Entry(LF($"Nazwa konta {number}", $"Account {number} name"), user.Name, value => user.Name = value.Trim());
                        first ??= entry;
                        fields.Entry(LF($"Nazwa wyświetlana konta {number} (opcjonalnie)", $"Account {number} display name (optional)"),
                            user.DisplayName, value => user.DisplayName = value.Trim());
                    }
                    if (user.AskPassword || profile.Target.RequiresLocalAccountPassword && user.Password.Length == 0)
                    {
                        var entry = fields.Entry(LF($"Hasło konta {number}", $"Account {number} password"), "", value => user.Password = value, secret: true);
                        user.Password = "";
                        first ??= entry;
                    }
                }
            }
            if (first is not null && !Ask(L("Dane tej instalacji", "Details for this installation"), fields, first,
                    validate: () => AnswerFile.ValidateAccounts(profile)))
                return null;
            if (first is null)
                fields.Table.Dispose();
            return operation(profile);
        }
        finally
        {
            profile.Users = original;
            profile.SetupKey = setupKey;
            profile.ActivationKey = activationKey;
            profile.SessionOptions.Clear();
            foreach (var entry in session)
                profile.SessionOptions.Add(entry.Key, entry.Value);
        }
    }

    private string DiskApprovalLabel() => profile.RequiresCustomPeConsent
        ? L("Potwierdz&am użycie własnego skryptu, który może usunąć partycje", "Confirm running the custom script, which c&an erase partitions")
        : profile.FormatsPartition
            ? LF($"Potwierdz&am formatowanie partycji {profile.PartitionNumber} na dysku {profile.DiskNumber}",
                $"Confirm form&atting partition {profile.PartitionNumber} on disk {profile.DiskNumber}")
            : L("Potwierdz&am usunięcie wszystkich partycji z wybranego dysku", "Confirm deleting &all partitions on the selected disk");

    private CheckBox DiskApproval(Fields f) => Explain(f.Check(DiskApprovalLabel(), profile.ConfirmWipe, value => profile.ConfirmWipe = value),
        "Zgoda obejmuje dysk i układ widoczne w kreatorze. Po zmianie dysku lub układu zaznacz zgodę ponownie.",
        "Approval applies to the disk and layout shown in the wizard. Select the checkbox again after changing the disk or layout.");

    private SaveFileDialog AnswerSaveDialog() => new()
    {
        Title = L("Zapisz plik odpowiedzi XML", "Save the XML answer file"),
        Filter = L("Plik odpowiedzi Windows (*.xml)|*.xml", "Windows answer file (*.xml)|*.xml"),
        DefaultExt = "xml",
        AddExtension = true,
        FileName = AnswerFile.DefaultName
    };

    private async void BeginExport()
    {
        if (working)
            return;
        Validate();
        if (step != Steps.Length - 1 && profile.RequiresDiskConsent && !profile.ConfirmWipe)
        {
            step = Steps.Length - 1;
            ShowStep();
            return;
        }
        try
        {
            var document = BuildInstallationAnswer();
            if (document is null)
                return;
            using var answer = AnswerSaveDialog();
            if (answer.ShowDialog(this) != DialogResult.OK)
                return;
            string? packageParent = null;
            if (profile.HasMediaFiles)
            {
                using var dialog = new FolderBrowserDialog
                {
                    UseDescriptionForTitle = true,
                    Description = L("Wybierz osobno miejsce na strukturę pendrive’a lub płyty", "Separately select where to save the USB or disc folder structure")
                };
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;
                packageParent = dialog.SelectedPath;
            }

            SetBusy(true, L("Zapisywanie pliku odpowiedzi…", "Saving the answer file…"));
            string? package = await Task.Run(() => MediaPackage.Export(answer.FileName, packageParent, document, profile));
            MarkConfigurationSaved();
            lastOutput = package ?? Path.GetDirectoryName(answer.FileName);
            string message = L("Zapisano plik odpowiedzi XML.", "Saved the XML answer file.") + "\r\n" + answer.FileName;
            if (package is not null)
                message += "\r\n\r\n" + L("Struktura nośnika do skopiowania na pendrive lub płytę", "Folder structure to copy to a USB drive or disc") + "\r\n" + package;
            SetBusy(false);
            Info(message);
            ShowStep();
        }
        catch (Exception e)
        {
            Error(e);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static void CleanupUsbStaging(DirectoryInfo staging)
    {
        if (!string.Equals(staging.Parent?.FullName, Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
                StringComparison.OrdinalIgnoreCase) || !staging.Name.StartsWith("XMLator-usb-", StringComparison.Ordinal))
            return;
        try { staging.Delete(recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

}
