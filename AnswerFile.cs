using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace XMLator;

internal sealed class SetupException(string pl, string en) : Exception(en)
{
    private FormattableString? format;

    internal static SetupException Format(FormattableString polish, FormattableString english) =>
        new(polish.ToString(UiText.Culture("pl")), english.ToString(UiText.Culture("en"))) { format = english };

    internal string Translate(string language) => language == "pl" ? pl
        : format is null ? UiText.Get(language, Message) : UiText.Format(language, format);
}

internal sealed class AnswerFile
{
    internal const string DefaultName = "Autounattend.xml";
    private const string Winlogon = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    internal const string StopAutomaticLogon = $"""reg.exe add "{Winlogon}" /v AutoLogonCount /t REG_DWORD /d 0 /f & reg.exe add "{Winlogon}" /v AutoAdminLogon /t REG_SZ /d 0 /f""";

    internal static string FirstLogonCommand(string? nextCommand = null)
    {
        string commands = nextCommand is null ? StopAutomaticLogon : $"{StopAutomaticLogon} & {nextCommand}";
        return $"cmd.exe /d /s /c \"{commands}\"";
    }
    internal static readonly XNamespace U = "urn:schemas-microsoft-com:unattend";
    internal static readonly XNamespace Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";
    private static readonly string[] Passes =
        ["windowsPE", "offlineServicing", "generalize", "specialize", "auditSystem", "auditUser", "oobeSystem"];
    private const string Setup = "Microsoft-Windows-Setup";
    private const string Shell = "Microsoft-Windows-Shell-Setup";
    private const string Deployment = "Microsoft-Windows-Deployment";

    private readonly SetupProfile profile;
    private readonly string architecture;
    private MediaEdition? SelectedEdition => EditionCatalog.For(profile.Target, architecture).FirstOrDefault(e => e.Id == profile.PresetEdition);
    private readonly XElement root = new(U + "unattend", new XAttribute(XNamespace.Xmlns + "wcm", Wcm));

    private AnswerFile(SetupProfile profile, string architecture)
    {
        this.profile = profile;
        this.architecture = architecture;
    }

    public static XDocument Build(SetupProfile profile)
    {
        XDocument answer = BuildForArchitecture(profile, profile.Architecture);
        foreach (string architecture in profile.ExtraArchitectures.Distinct().Where(a => a != profile.Architecture))
        {
            var variant = BuildForArchitecture(profile, architecture);
            foreach (var variantPass in variant.Root!.Elements(U + "settings"))
            {
                var pass = answer.Root!.Elements(U + "settings").SingleOrDefault(p => (string?)p.Attribute("pass") == (string?)variantPass.Attribute("pass"));
                if (pass is not null)
                    pass.Add(variantPass.Elements());
                else
                {
                    int order = Array.IndexOf(Passes, (string)variantPass.Attribute("pass")!);
                    var next = answer.Root!.Elements().FirstOrDefault(node => node.Name != U + "settings" ||
                        Array.IndexOf(Passes, (string)node.Attribute("pass")!) > order);
                    if (next is null)
                        answer.Root!.Add(variantPass);
                    else
                        next.AddBeforeSelf(variantPass);
                }
            }
        }
        return answer;
    }

    private static XDocument BuildForArchitecture(SetupProfile profile, string architecture)
    {
        var file = new AnswerFile(profile, architecture);
        file.Validate();
        file.WriteSetup();
        file.WriteLocale();
        file.WriteShell();
        file.WriteAutomation();
        if (profile.Target.ModernClient)
            ModernOptions.Apply(profile, architecture, file.root, file.Component);
        file.WriteComponents();
        foreach (var component in file.root.Descendants(U + "component").Where(c => !c.HasElements).ToList())
            component.Remove();
        foreach (var pass in file.root.Elements(U + "settings").Where(p => !p.HasElements).ToList())
            pass.Remove();
        return new XDocument(new XDeclaration("1.0", "utf-8", null), file.root);
    }

    public static void Save(XDocument document, string path)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using (var header = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true))
            header.Write("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n");
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Encoding = Encoding.ASCII,
            Indent = true,
            NewLineChars = "\r\n",
            NewLineHandling = NewLineHandling.Entitize
        });
        document.Save(writer);
    }

    private XElement Component(string pass, string name)
    {
        var settings = root.Elements(U + "settings").FirstOrDefault(p => (string?)p.Attribute("pass") == pass);
        if (settings is null)
        {
            settings = new XElement(U + "settings", new XAttribute("pass", pass));
            int index = Array.IndexOf(Passes, pass);
            var next = root.Elements(U + "settings").FirstOrDefault(p => Array.IndexOf(Passes, (string)p.Attribute("pass")!) > index);
            if (next is null)
            {
                var extension = root.Elements().FirstOrDefault(n => n.Name != U + "settings");
                if (extension is null)
                    root.Add(settings);
                else
                    extension.AddBeforeSelf(settings);
            }
            else
                next.AddBeforeSelf(settings);
        }
        var component = settings.Elements(U + "component").FirstOrDefault(c => (string?)c.Attribute("name") == name);
        if (component is not null)
            return component;

        component = new XElement(U + "component",
            new XAttribute("name", name), new XAttribute("processorArchitecture", architecture),
            new XAttribute("publicKeyToken", "31bf3856ad364e35"), new XAttribute("language", "neutral"),
            new XAttribute("versionScope", "nonSxS"));
        settings.Add(component);
        return component;
    }

    private static XElement E(string name, params object?[] value) => new(U + name, value);
    private static XElement AddItem(string name, params object?[] value) =>
        E(name, new XAttribute(Wcm + "action", "add"), value);

    private void Validate()
    {
        var p = profile;
        if (!p.Target.Architectures.Contains(architecture))
            throw new SetupException("Wybierz architekturę obsługiwaną przez ten system.", "Select an architecture supported by this Windows version.");
        if (p.ImageMode == ImageMode.Index && p.ImageIndex < 1 || p.ImageMode == ImageMode.ExactName && string.IsNullOrWhiteSpace(p.ImageName))
            throw new SetupException("Wpisz nazwę obrazu z nośnika lub poprawny indeks.", "Enter the image name from your media or a valid index.");
        if (p.ImageMode == ImageMode.Edition && SelectedEdition is null)
            throw new SetupException("Wybierz edycję dostępną dla tego systemu.", "Select an edition available for this Windows version.");
        if ((p.KeyMode == KeyMode.Enter && !ValidKey(p.SetupKey)) || (p.UseActivationKey && !ValidKey(p.ActivationKey)))
            throw new SetupException("Wpisz klucz produktu w formacie XXXXX-XXXXX-XXXXX-XXXXX-XXXXX.", "Enter a product key in the format XXXXX-XXXXX-XXXXX-XXXXX-XXXXX.");
        if (p.KeyMode is KeyMode.Ask or KeyMode.Firmware or KeyMode.Generic && !p.Target.ModernClient)
            throw new SetupException("Ten tryb klucza jest dostępny dla Windows 10 i 11.", "This product key mode is available for Windows 10 and 11.");
        if (!p.SupportsDiskMode(p.DiskMode))
            throw new SetupException("W kroku Dysk i partycje wybierz układ dostępny dla wybranego trybu rozruchu i metody instalacji.",
                "On the Disk and partitions page, choose a layout supported by the boot mode and installation method.");
        if (p.RequiresDiskConsent && !p.ConfirmWipe)
            throw new SetupException("Zaznacz zgodę na wybraną operację w kroku Dysk i partycje albo w podsumowaniu.", "Confirm the selected operation on the Disk and partitions page or on the review page.");
        if (p.NeedsDiskNumber && p.DiskNumber is < 0 or > 999)
            throw new SetupException("Numer dysku musi wynosić od 0 do 999.", "The disk number must be between 0 and 999.");
        if (p.WipesDisk)
            _ = Layout.Plan(p, architecture);
        if (p.SetLocale)
        {
            try
            {
                foreach (var locale in new[] { p.UiLocale, p.UserLocale, p.SystemLocale })
                    if (locale.Length == 0 || CultureInfo.GetCultureInfo(locale).IsNeutralCulture)
                        throw new CultureNotFoundException();
            }
            catch (CultureNotFoundException)
            {
                throw new SetupException("Wpisz pełne kody języka, na przykład pl-PL lub en-US.", "Enter full language tags such as pl-PL or en-US.");
            }
            if (string.IsNullOrWhiteSpace(p.Keyboard))
                throw new SetupException("Wpisz układ klawiatury, na przykład 0415:00000415.", "Enter a keyboard layout, for example 0409:00000409.");
        }
        if (p.ComputerName.Length > 0 && p.ComputerName != "*" && (!Regex.IsMatch(p.ComputerName, @"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,13}[A-Za-z0-9])?$") || p.ComputerName.All(char.IsAsciiDigit)))
            throw new SetupException("Nazwa komputera musi mieć do 15 znaków ASCII, zawierać literę lub łącznik i zaczynać oraz kończyć się literą albo cyfrą.", "Use up to 15 ASCII letters, digits and hyphens for the computer name. Start and end with a letter or digit and avoid a digits-only name.");
        if (p.Workgroup.Length > 0 && (p.Workgroup.Length > 15 || p.Workgroup.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|', '.', '[', ']', '+', '=', ',', ';']) >= 0 || p.Workgroup.Any(char.IsControl)))
            throw new SetupException("Nazwa grupy roboczej może mieć do 15 znaków. Użyj na przykład WORKGROUP.", "Use a workgroup name of up to 15 characters, for example WORKGROUP.");
        ValidateAccounts(p);
        MediaPackage.Validate(p);
    }

    internal static void ValidateAccounts(SetupProfile p)
    {
        if (p.Target.Server && p.SetServerAdministratorPassword &&
            (!p.SessionOptions.TryGetValue("ServerAdministratorPassword", out var serverPassword) || string.IsNullOrWhiteSpace(serverPassword)))
            throw new SetupException("Wpisz hasło wbudowanego Administratora Windows Server.", "Enter the password for the built-in Windows Server Administrator.");
        if (p.Target.ModernClient && p.SelectedFirstRun is FirstRunMode.SkipSupported or FirstRunMode.Offline && p.FirstLocalAdministrator is null)
            throw new SetupException("Aby użyć wybranego wariantu pierwszej konfiguracji, dodaj lokalne konto administratora.",
                "Add a local administrator account to use the selected initial setup option.");
        if (p.CreateUsers)
        {
            if (p.Users.Count == 0)
                throw new SetupException("Dodaj co najmniej jedno konto lokalne.", "Add at least one local account.");
            if (p.FirstLocalAdministrator is null)
                throw new SetupException("Co najmniej jedno dodane konto musi mieć uprawnienia administratora.", "At least one account you add needs administrator permissions.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var user in p.Users)
            {
                ValidateUser(user, template: false, requirePassword: p.Target.RequiresLocalAccountPassword);
                if (!names.Add(user.Name) || string.Equals(user.Name, p.ComputerName, StringComparison.OrdinalIgnoreCase))
                    throw SetupException.Format($"Nazwa konta „{user.Name}” powtarza się lub jest nazwą komputera.", $"Account name “{user.Name}” is duplicated or matches the computer name.");
            }
        }
        if (p.Logon == LogonMode.FirstAdministrator && p.FirstLocalAdministrator is null)
            throw new SetupException("Automatyczne logowanie wymaga lokalnego konta administratora.", "Automatic sign-in requires a local administrator account.");
    }

    internal static void ValidateUser(LocalUser user, bool template, bool requirePassword = false)
    {
        if (!(template && user.AskName))
        {
            string name = user.Name;
            if (name.Length is < 1 or > 20 || name != name.Trim() || name.EndsWith('.') ||
                name.Any(char.IsControl) || name.IndexOfAny(['/', '\\', '[', ']', ':', ';', '|', '=', ',', '+', '*', '?', '<', '>', '"', '%', '@']) >= 0 ||
                new[] { "Administrator", "Guest", "DefaultAccount", "SYSTEM", "Network Service", "Local Service", "None", "WDAGUtilityAccount" }.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new SetupException("Wpisz nazwę konta od 1 do 20 znaków. Użyj nazwy innej niż nazwa konta systemowego.", "Enter an account name of 1 to 20 characters. Use a name other than a built-in account name.");
        }
        if (!template && (user.AskPassword || requirePassword) && user.Password.Length == 0)
            throw SetupException.Format($"Wpisz hasło dla konta „{user.Name}”.", $"Enter a password for account “{user.Name}”.");
    }

    private static bool ValidKey(string key) => Regex.IsMatch(key, @"^[A-Za-z0-9]{5}(?:-[A-Za-z0-9]{5}){4}$");

    private void WriteSetup()
    {
        var p = profile;
        var setup = Component("windowsPE", Setup);
        var userData = E("UserData");
        if (p.AcceptEula)
            userData.Add(E("AcceptEula", true));
        var presetKey = p.Target.ModernClient && p.ImageMode == ImageMode.Edition &&
            p.KeyMode == KeyMode.Generic
                ? ModernOptions.Engine.WindowsEditions.Values.FirstOrDefault(e => e.DisplayName == SelectedEdition!.Label)
                : null;
        switch (p.KeyMode)
        {
            case KeyMode.Enter:
                userData.Add(E("ProductKey", E("Key", p.SetupKey.ToUpperInvariant()), E("WillShowUI", "OnError")));
                break;
            case KeyMode.Firmware:
            case KeyMode.Skip:
                userData.Add(E("ProductKey", E("WillShowUI", "Never")));
                break;
            case KeyMode.Ask:
                userData.Add(E("ProductKey", E("Key", "00000-00000-00000-00000-00000"), E("WillShowUI", "Always")));
                break;
            case KeyMode.Generic:
                var generic = p.ImageMode == ImageMode.Edition
                    ? presetKey
                    : ModernOptions.Engine.Lookup<Schneegans.Unattend.WindowsEdition>(p.GenericEdition);
                if (generic is null)
                    throw new SetupException("Dla wybranej edycji wybierz domyślną obsługę klucza lub wpisz własny klucz.", "For this edition, use Setup's default key handling or enter your own key.");
                userData.Add(E("ProductKey", E("Key", generic.ProductKey),
                    E("WillShowUI", "OnError")));
                break;
        }
        if (userData.HasElements)
            setup.Add(userData);
        if (p.Has("UseConfigurationSet"))
            setup.Add(E("UseConfigurationSet", true));

        var osImage = E("OSImage");
        if (p.ImageMode != ImageMode.Interactive)
        {
            string selector = p.ImageMode == ImageMode.Index ? "/IMAGE/INDEX" : "/IMAGE/NAME";
            object value = p.ImageMode switch
            {
                ImageMode.Edition => SelectedEdition!.ImageName,
                ImageMode.ExactName => p.ImageName.Trim(),
                _ => p.ImageIndex
            };
            osImage.Add(E("InstallFrom", AddItem("MetaData", E("Key", selector), E("Value", value))));
        }

        if (p.WipesDisk)
        {
            var parts = Layout.Plan(p, architecture);
            int installPartition = parts.FindIndex(part => part.Windows) + 1;
            if (Layout.PrepareRecoveryInWinPe(p))
            {
                WriteFlexibleGptLayout(setup, parts);
                var label = AddItem("ModifyPartition", E("Order", 1), E("PartitionID", installPartition),
                    E("Label", parts[installPartition - 1].Label));
                setup.Add(E("DiskConfiguration", AddItem("Disk", E("DiskID", p.DiskNumber),
                    E("WillWipeDisk", false), E("ModifyPartitions", label)), E("WillShowUI", p.DiskPrompt.ToString())));
            }
            else
            {
                var creates = E("CreatePartitions");
                var changes = E("ModifyPartitions");
                int modifyOrder = 0;
                for (int i = 0; i < parts.Count; i++)
                {
                    var part = parts[i];
                    creates.Add(AddItem("CreatePartition", E("Order", i + 1), E("Type", part.Type),
                        part.Size > 0 ? E("Size", part.Size) : E("Extend", true)));
                    if (part.Format is null)
                        continue;
                    changes.Add(AddItem("ModifyPartition", E("Order", ++modifyOrder), E("PartitionID", i + 1),
                        E("Format", part.Format), E("Label", part.Label), part.Active ? E("Active", true) : null,
                        part.Letter is null ? null : E("Letter", part.Letter),
                        part.TypeId is null ? null : E("TypeID", part.TypeId)));
                }
                setup.Add(E("DiskConfiguration", AddItem("Disk", E("DiskID", p.DiskNumber),
                    E("WillWipeDisk", true), creates, changes), E("WillShowUI", p.DiskPrompt.ToString())));
            }
            osImage.Add(E("InstallTo", E("DiskID", p.DiskNumber), E("PartitionID", installPartition)));
        }

        if (osImage.HasElements)
        {
            osImage.Add(E("WillShowUI", p.EffectiveImagePrompt.ToString()));
            setup.Add(E("ImageInstall", osImage));
        }
    }

    private void WriteFlexibleGptLayout(XElement setup, IReadOnlyList<Partition> partitions)
    {
        const string batch = @"X:\XMLator-Partition.cmd";
        const string detail = @"X:\XMLator-DiskDetail.txt";
        const string layout = @"X:\XMLator-Partition.txt";
        string selectDisk = FormattableString.Invariant($"select disk {profile.DiskNumber}");
        string efi = FormattableString.Invariant($"create partition efi size={partitions[0].Size}");
        string msr = FormattableString.Invariant($"create partition msr size={partitions[1].Size}");
        string shrink = FormattableString.Invariant($"shrink desired={partitions[3].Size} minimum={partitions[3].Size}");
        var commands = new Schneegans.Unattend.CommandBuilder(hidePowerShellWindows: false);
        string[] dispatcher =
        [
            "@echo off",
            "setlocal",
            "wpeutil.exe UpdateBootInfo >nul || exit /b 1",
            """set "UEFI=" """,
            """for /f "tokens=3" %%F in ('reg.exe query HKLM\System\CurrentControlSet\Control /v PEFirmwareType') do if /i "%%F"=="0x2" set "UEFI=1" """,
            "if not defined UEFI exit /b 1",
            $"""diskpart.exe /s {detail} > X:\XMLator-DiskDetail.log 2>&1 || exit /b 1""",
            """for /f "tokens=2 delims=:" %%B in (X:\XMLator-DiskDetail.log) do for /f %%C in ("%%B") do (""",
            """  if /i "%%C"=="USB" exit /b 1""",
            """  if /i "%%C"=="1394" exit /b 1""",
            ")",
            "for %%D in (A B C D E F G H I J K L M N O P Q R S T U V W Y Z) do (",
            "  call :checkSource %%D",
            "  if errorlevel 1 exit /b 1",
            ")",
            $"""diskpart.exe /s {layout} > X:\XMLator-Partition.log 2>&1""",
            "exit /b %errorlevel%",
            ":checkSource",
            @"if exist %1:\sources\boot.wim goto checkDrive",
            @"if exist %1:\sources\install.wim goto checkDrive",
            @"if exist %1:\sources\install.esd goto checkDrive",
            @"if exist %1:\sources\install.swm goto checkDrive",
            @"if exist %1:\Autounattend.xml goto checkDrive",
            "exit /b 0",
            ":checkDrive",
            """for /f "tokens=3" %%L in (X:\XMLator-DiskDetail.log) do if /i "%%L"=="%1" exit /b 1""",
            "exit /b 0"
        ];
        var paths = new List<string> { $"""cmd.exe /d /c "type nul >{batch} & type nul >{detail} & type nul >{layout}" """ };
        paths.AddRange(commands.WriteToFilePE(detail, [selectDisk, "detail disk"]));
        paths.AddRange(commands.WriteToFilePE(layout,
            [selectDisk, "clean", "convert gpt", efi, "format quick fs=fat32 label=System", msr, "create partition primary",
             "format quick fs=ntfs label=Windows", shrink, "create partition primary", "format quick fs=ntfs label=Recovery",
             $"set id={partitions[3].TypeId}", "gpt attributes=0x8000000000000001"]));
        paths.AddRange(commands.WriteToFilePE(batch, dispatcher));
        paths.Add($"""cmd.exe /d /c "{batch}" """);
        setup.Add(E("RunSynchronous", paths.Select((path, index) =>
            AddItem("RunSynchronousCommand", E("Order", index + 1), E("Path", path)))));
    }

    private void WriteLocale()
    {
        var p = profile;
        if (!p.SetLocale)
            return;
        XElement[] LocaleValues() => [E("InputLocale", p.Keyboard.Trim()), E("SystemLocale", p.SystemLocale.Trim()),
            E("UILanguage", p.UiLocale.Trim()), E("UserLocale", p.UserLocale.Trim())];
        Component("windowsPE", "Microsoft-Windows-International-Core-WinPE")
            .Add(E("SetupUILanguage", E("UILanguage", p.UiLocale.Trim())), LocaleValues());
        Component("oobeSystem", "Microsoft-Windows-International-Core").Add(LocaleValues());
    }

    private void WriteShell()
    {
        var p = profile;
        var specialize = Component("specialize", Shell);
        if (p.ComputerName.Length > 0 && (!p.Target.ModernClient || p.ComputerNameMode == "fixed"))
            specialize.Add(E("ComputerName", p.ComputerName));
        if (p.TimeZone.Length > 0)
            specialize.Add(E("TimeZone", p.TimeZone));
        if (p.RegisteredOwner.Length > 0)
            specialize.Add(E("RegisteredOwner", p.RegisteredOwner));
        if (p.RegisteredOrganization.Length > 0)
            specialize.Add(E("RegisteredOrganization", p.RegisteredOrganization));
        if (p.Workgroup.Length > 0)
            Component("specialize", "Microsoft-Windows-UnattendedJoin").Add(E("Identification", E("JoinWorkgroup", p.Workgroup)));
        if (p.UseActivationKey)
            specialize.Add(E("ProductKey", p.ActivationKey.ToUpperInvariant()));
        else if (p.KeyMode == KeyMode.Enter && p.UseSetupKeyForActivation)
            specialize.Add(E("ProductKey", p.SetupKey.ToUpperInvariant()));
        if (!specialize.HasElements)
            specialize.Remove();

        var shell = Component("oobeSystem", Shell);
        var automaticUser = p.Logon == LogonMode.FirstAdministrator ? p.FirstLocalAdministrator : null;
        var accounts = E("UserAccounts");
        if (p.Target.Server && p.SetServerAdministratorPassword)
            accounts.Add(Password("AdministratorPassword", p.SessionOptions["ServerAdministratorPassword"]));
        if (p.CreateUsers)
        {
            accounts.Add(E("LocalAccounts", p.Users.Select(u => AddItem("LocalAccount",
                E("Name", u.Name), u.DisplayName.Length == 0 ? null : E("DisplayName", u.DisplayName),
                E("Group", u.Administrator ? "Administrators" : "Users"),
                u.Password.Length > 0 || u == automaticUser ? Password("Password", u.Password) : null))));
        }
        if (accounts.HasElements)
            shell.Add(accounts);

        if (automaticUser is not null)
        {
            shell.Add(E("AutoLogon", E("Enabled", true), E("LogonCount", 1),
                E("Username", automaticUser.Name), Password("Password", automaticUser.Password)));
        }
        var oobe = E("OOBE");
        bool skip = p.Target.ModernClient && p.SelectedFirstRun == FirstRunMode.SkipSupported;
        bool offline = p.Target.ModernClient && p.SelectedFirstRun == FirstRunMode.Offline;
        bool custom = !p.Target.ModernClient || p.SelectedFirstRun == FirstRunMode.Custom;
        if (p.AcceptEula && !p.Target.Server)
            oobe.Add(E("HideEULAPage", true));
        if (p.Target.ClientOobeNetwork && (skip || offline || custom && p.HideOnline))
            oobe.Add(E("HideOnlineAccountScreens", true));
        if (p.Target.ClientOobeNetwork && (skip || offline || p.SkipWifi || custom && p.HideWireless))
            oobe.Add(E("HideWirelessSetupInOOBE", true));
        if (p.Target.ModernClient && (skip || custom && p.HideOemRegistration))
            oobe.Add(E("HideOEMRegistrationScreen", true));
        if (!p.Target.Server && (skip || custom && p.Express is "EnableAll" or "DisableAll"))
            oobe.Add(E("ProtectYourPC", !skip && p.Express == "EnableAll" ? 1 : 3));
        if (oobe.HasElements)
            shell.Add(oobe);
        if (!shell.HasElements)
            shell.Remove();
    }

    private XElement Password(string element, string password)
    {
        bool encode = profile.ObscurePasswords && password.Length > 0;
        return E(element, E("Value", encode ? Convert.ToBase64String(Encoding.Unicode.GetBytes(password + element)) : password),
            E("PlainText", !encode));
    }

    private void WriteAutomation()
    {
        var p = profile;
        var specialize = new List<XElement>();
        void AddSpecialize(string path) => specialize.Add(AddItem("RunSynchronousCommand",
            E("Order", specialize.Count + 1), E("Description", "XMLator"), E("Path", path)));
        if (p.Target.Windows11 && p.Has("BypassNetworkCheck"))
            AddSpecialize("""reg.exe add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE" /v BypassNRO /t REG_DWORD /d 1 /f""");
        if (p.InstalledDrivers.Length > 0 || p.OemFiles.Any(f => f.Phase == OemPhase.Specialize))
            AddSpecialize("cmd.exe /d /c \"\"%SystemRoot%\\Setup\\Scripts\\XMLator-Specialize.cmd\"\"");
        if (specialize.Count > 0)
            Component("specialize", Deployment).Add(E("RunSynchronous", specialize));
        bool firstLogonFile = p.OemFiles.Any(f => f.Phase == OemPhase.FirstLogon);
        if (p.Logon == LogonMode.FirstAdministrator || !p.Target.ModernClient && firstLogonFile)
        {
            string command = !p.Target.ModernClient && firstLogonFile
                ? "cmd.exe /d /c \"\"%SystemRoot%\\Setup\\Scripts\\XMLator-FirstLogon.cmd\"\""
                : FirstLogonCommand();
            Component("oobeSystem", Shell).Add(E("FirstLogonCommands",
                AddItem("SynchronousCommand", E("Order", 1), E("Description", "XMLator"), E("CommandLine", command))));
        }
    }

    private void WriteComponents()
    {
        var seen = new HashSet<(string Pass, string Name)>();
        foreach (var item in profile.Components)
        {
            if (!Passes.Contains(item.Pass) || !Regex.IsMatch(item.Name, "^[A-Za-z0-9][A-Za-z0-9_.-]+$") || !seen.Add((item.Pass, item.Name)))
                throw SetupException.Format($"Sprawdź przebieg i nazwę składnika „{item.Name}”.", $"Check the pass and name for component “{item.Name}”.");
            using var text = new StringReader($"<root xmlns='{U}' xmlns:wcm='{Wcm}'>{item.Markup}</root>");
            using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            var markup = XElement.Load(reader, LoadOptions.PreserveWhitespace);
            if (!markup.HasElements || markup.Descendants().Any(n => n.Name == U + "settings" || n.Name == U + "component"))
                throw new SetupException("Wklej ustawienia wewnątrz składnika XML.", "Paste the settings inside the XML component.");
            var component = Component(item.Pass, item.Name);
            foreach (var entry in markup.Elements())
            {
                component.Elements(entry.Name).Remove();
                component.Add(entry);
            }
        }
    }
}

internal sealed record Partition(string Type, int Size, string? Format = null, string? Label = null,
    bool Windows = false, bool Active = false, string? Letter = null, string? TypeId = null);

internal static class Layout
{
    // Unattend's Extend consumes all remaining space. WinPE reserves the recovery space after C before Setup starts applying the image.
    internal static bool PrepareRecoveryInWinPe(SetupProfile p) =>
        p.DiskMode == DiskMode.SystemAndWindows && p.Recovery && !p.Target.OldMsr && p.InstallationMethod == "Normal";

    public static List<Partition> Plan(SetupProfile p, string? architecture = null)
    {
        architecture ??= p.Architecture;
        if (p.DiskMode == DiskMode.Single)
            return [new("Primary", 0, "NTFS", "Windows", Windows: true, Active: true)];

        int minimumEfi = p.Target.OldMsr ? 100 : 200;
        if (p.EfiMiB < minimumEfi)
            throw SetupException.Format($"Partycja EFI musi mieć co najmniej {minimumEfi} MiB. Dla dysków 4Kn użyj co najmniej 300 MiB.", $"The EFI partition must be at least {minimumEfi} MiB. For 4Kn drives, use at least 300 MiB.");
        if (p.Recovery && p.RecoveryMiB < 300)
            throw new SetupException("Partycja odzyskiwania musi mieć co najmniej 300 MiB. Zalecane jest co najmniej 1024 MiB.", "The recovery partition must be at least 300 MiB. At least 1024 MiB is recommended.");

        int minWindows = p.Target.Windows11 ? 65536 : p.Target.OldMsr ? architecture == "x86" ? 16384 : 20480 : 32768;
        if (p.HasSeparateData && p.WindowsMiB < minWindows)
            throw SetupException.Format($"Ustaw co najmniej {minWindows} MiB dla partycji C.", $"Set the C partition to at least {minWindows} MiB.");

        List<Partition> result = [new("EFI", p.EfiMiB, "FAT32", "System"), new("MSR", p.Target.OldMsr ? 128 : 16)];
        var recovery = new Partition("Primary", p.RecoveryMiB, "NTFS", "Recovery",
            TypeId: "de94bba4-06d1-4d40-a16a-bfd50179d6ac");
        bool recoveryAfterWindows = p.HasSeparateData || PrepareRecoveryInWinPe(p) || p.InstallationMethod == "Generated";
        if (p.Recovery && !recoveryAfterWindows)
            result.Add(recovery);
        result.Add(new("Primary", p.HasSeparateData ? p.WindowsMiB : 0, "NTFS", "Windows", Windows: true));
        if (p.Recovery && recoveryAfterWindows)
            result.Add(recovery);
        if (p.HasSeparateData)
            result.Add(new("Primary", 0, "NTFS", "Data", Letter: "D"));
        return result;
    }
}
