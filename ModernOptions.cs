using System.Collections.Immutable;
using System.Globalization;
using System.Xml.Linq;
using Schneegans.Unattend;

namespace XMLator;

internal static class ModernOptions
{
    internal static readonly UnattendGenerator Engine = new();

    internal static void Apply(SetupProfile p, string architecture, XElement root, Func<string, string, XElement> component)
    {
        var u = AnswerFile.U;
        bool alternatePe = p.InstallationMethod != "Normal";
        bool firstLogonFile = p.OemFiles.Any(f => f.Phase == OemPhase.FirstLogon);
        bool cleanUp = p.Has("CleanSensitive");
        if (cleanUp && p.Logon == LogonMode.Manual)
            throw new SetupException("Usuwanie kopii plików z hasłami wymaga automatycznego logowania administratora.", "Removing copies of files with passwords requires automatic administrator sign-in.");
        var scripts = p.EmbeddedScripts.Select(x => new Script(x.Text, Enum.Parse<ScriptPhase>(x.Phase), Enum.Parse<ScriptType>(x.Type))).ToList();
        if (p.Target.Windows11 && p.DiskMode == DiskMode.Single && !alternatePe)
        {
            using var reader = new StreamReader(typeof(ModernOptions).Assembly.GetManifestResourceStream("XMLator.KeepSingleMbrPartition.ps1")!);
            string keepSingle = reader.ReadToEnd().Replace("${ExpectedDisk}", p.DiskNumber.ToString(CultureInfo.InvariantCulture));
            scripts.Insert(0, new Script(keepSingle, ScriptPhase.System, ScriptType.Ps1));
        }
        if (p.DarkInstalledWindows)
        {
            scripts.Add(new Script("""
                Windows Registry Editor Version 5.00

                [HKEY_USERS\DefaultUser\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize]
                "AppsUseLightTheme"=dword:00000000
                "SystemUsesLightTheme"=dword:00000000
                """, ScriptPhase.DefaultUser, ScriptType.Reg));
        }
        if (firstLogonFile)
            scripts.Add(new Script("""call "%SystemRoot%\Setup\Scripts\XMLator-FirstLogon.cmd" """, ScriptPhase.FirstLogon, ScriptType.Cmd));

        bool EnabledFor11(string key) => p.Target.Windows11 && p.Has(key);
        Configuration settings = Configuration.Default with
        {
            ProcessorArchitectures = [Enum.Parse<ProcessorArchitecture>(architecture)],
            LanguageSettings = GetPeLanguage(p, alternatePe),
            PESettings = GetPe(p, architecture),
            ExpressSettings = ExpressSettingsMode.Interactive,
            KeepSensitiveFiles = !cleanUp,
            ComputerNameSettings = p.ComputerNameMode switch
            {
                "prompt" => new PromptComputerNameSettings(),
                "script" => new ScriptComputerNameSettings(Required(p, "ComputerNameScript", p.ComputerNameScript)),
                _ => new RandomComputerNameSettings()
            },
            WifiSettings = GetWifi(p),
            LockoutSettings = p.Option("Lockout") switch
            {
                "Disable" => new DisableLockoutSettings(),
                "Custom" => new CustomLockoutSettings(p.Number("LockThreshold", 10), p.Number("LockDuration", 10), p.Number("LockWindow", 10)),
                _ => new DefaultLockoutSettings()
            },
            PasswordExpirationSettings = p.Option("Expiry") switch
            {
                "Never" => new UnlimitedPasswordExpirationSettings(),
                "Custom" => new CustomPasswordExpirationSettings(p.Number("ExpiryDays", 42)),
                _ => new DefaultPasswordExpirationSettings()
            },
            ProcessAuditSettings = p.Has("Audit") ? new EnabledProcessAuditSettings(p.Has("AuditCommand")) : new DisabledProcessAuditSettings(),
            AppLockerSettings = p.Has("AppLocker") ? new ConfigureAppLockerSettings(Required(p, "AppLockerXml")) : new SkipAppLockerSettings(),
            Bloatwares = [.. Engine.Bloatwares.Values.Where(b => p.Has("App." + b.Id))],
            ScriptSettings = new ScriptSettings(scripts, p.Has("RestartExplorer")),
            LockKeySettings = p.Has("LockKeys") ? new ConfigureLockKeySettings(Lock(p, "Caps"), Lock(p, "Num"), Lock(p, "Scroll")) : new SkipLockKeySettings(),
            StickyKeysSettings = p.Option("Sticky") switch
            {
                "Disable" => new DisabledStickyKeysSettings(),
                "Custom" => new CustomStickyKeysSettings(Enum.GetValues<StickyKeys>().Where(item => p.Has("Sticky." + item)).ToHashSet()),
                _ => new DefaultStickyKeysSettings()
            },
            WallpaperSettings = p.Option("Wallpaper") switch
            {
                "Solid" => new SolidWallpaperSettings(ParseColor(p.Option("WallpaperColor", "#000000"))),
                "Script" => new ScriptWallpaperSettings(Required(p, "WallpaperScript")),
                _ => new DefaultWallpaperSettings()
            },
            LockScreenSettings = p.Has("LockScreen") ? new ScriptLockScreenSettings(Required(p, "LockScreenScript")) : new DefaultLockScreenSettings(),
            ColorSettings = p.Has("ColorTheme") ? new CustomColorSettings(
                p.DarkInstalledWindows || p.Has("DarkSystem") ? ColorTheme.Dark : ColorTheme.Light,
                p.DarkInstalledWindows || p.Has("DarkApps") ? ColorTheme.Dark : ColorTheme.Light, p.Has("Transparent"), p.Has("AccentStart"),
                p.Has("AccentBorders"), ParseColor(p.Option("Accent", "#0078D4"))) : new DefaultColorSettings(),
            EnableLongPaths = p.Has("EnableLongPaths"),
            EnableRemoteDesktop = p.Has("EnableRemoteDesktop"),
            HardenSystemDriveAcl = p.Has("HardenSystemDriveAcl"),
            DeleteJunctions = p.Has("DeleteJunctions"),
            AllowPowerShellScripts = p.Has("AllowPowerShellScripts"),
            DisableLastAccess = p.Has("DisableLastAccess"),
            PreventAutomaticReboot = p.Has("PreventAutomaticReboot"),
            DisableSac = EnabledFor11("DisableSac"),
            DisableUac = p.Has("DisableUac"),
            DisableSmartScreen = p.Has("DisableSmartScreen"),
            DisableSystemRestore = p.Has("DisableSystemRestore"),
            DisableFastStartup = p.Has("DisableFastStartup"),
            TurnOffSystemSounds = p.Has("TurnOffSystemSounds"),
            DisableAppSuggestions = p.Has("DisableAppSuggestions"),
            DisableWidgets = EnabledFor11("DisableWidgets"),
            VBoxGuestAdditions = p.Has("VBoxGuestAdditions"),
            VMwareTools = p.Has("VMwareTools"),
            VirtIoGuestTools = p.Has("VirtIoGuestTools"),
            ParallelsTools = p.Has("ParallelsTools"),
            PreventDeviceEncryption = EnabledFor11("PreventDeviceEncryption"),
            ClassicContextMenu = EnabledFor11("ClassicContextMenu"),
            LeftTaskbar = EnabledFor11("LeftTaskbar"),
            HideTaskViewButton = p.Has("HideTaskViewButton"),
            ShowFileExtensions = p.Has("ShowFileExtensions"),
            ShowAllTrayIcons = p.Has("ShowAllTrayIcons"),
            HideFiles = Enum.Parse<HideModes>(p.Option("HideFiles", "Hidden")),
            HideEdgeFre = p.Has("HideEdgeFre"),
            DisableEdgeStartupBoost = p.Has("DisableEdgeStartupBoost"),
            MakeEdgeUninstallable = p.Has("MakeEdgeUninstallable"),
            DeleteEdgeDesktopIcon = p.Has("DeleteEdgeDesktopIcon"),
            LaunchToThisPC = p.Has("LaunchToThisPC"),
            DisableWindowsUpdate = p.Has("DisableWindowsUpdate"),
            DisablePointerPrecision = p.Has("DisablePointerPrecision"),
            DeleteWindowsOld = p.Has("DeleteWindowsOld"),
            DisableBingResults = p.Has("DisableBingResults"),
            HidePowerShellWindows = p.Has("HidePowerShellWindows"),
            ShowEndTask = EnabledFor11("ShowEndTask"),
            UseNarrator = p.Has("UseNarrator"),
            DisableCoreIsolation = p.Has("DisableCoreIsolation"),
            DisableAutomaticRestartSignOn = p.Has("DisableAutomaticRestartSignOn"),
            HideInfoTip = p.Has("HideInfoTip"),
            DisableWpbt = p.Has("DisableWpbt"),
            PreventDeviceApps = p.Has("PreventDeviceApps"),
            TaskbarSearch = Enum.Parse<TaskbarSearchMode>(p.Option("TaskbarSearch", "Box")),
            StartPinsSettings = !p.Target.Windows11 ? new DefaultStartPinsSettings() : p.Option("StartLayout") switch
            {
                "Empty" => new EmptyStartPinsSettings(),
                "Custom" => new CustomStartPinsSettings(Required(p, "StartLayoutText")),
                _ => new DefaultStartPinsSettings()
            },
            StartTilesSettings = p.Target.Windows11 ? new DefaultStartTilesSettings() : p.Option("StartLayout") switch
            {
                "Empty" => new EmptyStartTilesSettings(),
                "Custom" => new CustomStartTilesSettings(Required(p, "StartLayoutText")),
                _ => new DefaultStartTilesSettings()
            },
            TaskbarIcons = p.Option("TaskbarIcons") switch
            {
                "Empty" => new EmptyTaskbarIcons(),
                "Custom" => new CustomTaskbarIcons(Required(p, "TaskbarXml")),
                _ => new DefaultTaskbarIcons()
            },
            Effects = p.Option("Effects") switch
            {
                "Performance" => new BestPerformanceEffects(),
                "Appearance" => new BestAppearanceEffects(),
                "Custom" => new CustomEffects(Enum.GetValues<Effect>().ToImmutableDictionary(e => e, e => p.Has("Effect." + e))),
                _ => new DefaultEffects()
            },
            DesktopIcons = p.Has("DesktopIcons") ? new CustomDesktopIconSettings(Engine.DesktopIcons.Values
                .ToDictionary(x => x, x => p.Has("Desktop." + x.Id))) : new DefaultDesktopIconSettings(),
            StartFolderSettings = p.Target.Windows11 && p.Has("StartFolders") ? new CustomStartFolderSettings(Engine.StartFolders.Values
                .ToDictionary(x => x, x => p.Has("Start." + x.Id))) : new DefaultStartFolderSettings()
        };
        if (p.SetLocale && p.GeoId.Length > 0)
        {
            var geo = Engine.Lookup<GeoLocation>(p.GeoId);
            scripts.Add(new Script($"Set-WinHomeLocation -GeoId {geo.Id}", ScriptPhase.UserOnce, ScriptType.Ps1));
        }

        var generated = XDocument.Parse(Engine.GenerateXml(settings).OuterXml, LoadOptions.PreserveWhitespace);
        foreach (var source in generated.Root!.Elements(u + "settings"))
        {
            string pass = (string)source.Attribute("pass")!;
            if (alternatePe && pass == "windowsPE")
                root.Elements(u + "settings").Where(n => (string?)n.Attribute("pass") == pass).Remove();
            foreach (var node in source.Elements(u + "component"))
            {
                string name = (string)node.Attribute("name")!;
                if (name is "Microsoft-Windows-International-Core-WinPE" or "Microsoft-Windows-International-Core")
                    continue;
                var target = component(pass, name);
                foreach (var child in node.Elements())
                {
                    if (name == "Microsoft-Windows-Setup" && child.Name.LocalName is "UserData" or "UseConfigurationSet")
                        continue;
                    if (child.Name == u + "OOBE")
                        continue;
                    if (child.Name == u + "FirstLogonCommands" && target.Element(child.Name) is XElement firstLogon)
                    {
                        var reset = firstLogon.Descendants(u + "CommandLine").Single();
                        string script = child.Descendants(u + "CommandLine").Single().Value;
                        reset.Value = AnswerFile.FirstLogonCommand(script);
                        continue;
                    }
                    if (child.Name == u + "RunSynchronous" && target.Element(child.Name) is XElement commands)
                    {
                        commands.AddFirst(child.Elements().ToList());
                        int order = 0;
                        foreach (var command in commands.Elements())
                            command.SetElementValue(u + "Order", ++order);
                        continue;
                    }
                    target.Add(child);
                }
            }
        }
        foreach (var extension in generated.Root.Elements().Where(n => n.Name.Namespace != u))
            if (extension.Elements().Any(n => n.Name.LocalName != "Build" || n.Elements().Any(child => child.Name.LocalName == "PEScriptCopy")))
                root.Add(extension);
    }

    private static string Required(SetupProfile p, string name, string? text = null)
    {
        string value = text ?? p.Option(name);
        if (string.IsNullOrWhiteSpace(value))
            throw SetupException.Format($"Uzupełnij ustawienie {name}.", $"Complete the {name} setting.");
        return value;
    }

    private static Color ParseColor(string value)
    {
        if (value.Length != 7 || value[0] != '#' || !value[1..].All(Uri.IsHexDigit))
            throw new SetupException("Podaj kolor w formacie #RRGGBB.", "Enter the color in #RRGGBB format.");
        return ColorTranslator.FromHtml(value);
    }

    private static LockKeySetting Lock(SetupProfile p, string name) => new(
        p.Has(name + "On") ? LockKeyInitial.On : LockKeyInitial.Off,
        p.Has(name + "Ignore") ? LockKeyBehavior.Ignore : LockKeyBehavior.Toggle);

    private static IWifiSettings GetWifi(SetupProfile p)
    {
        string mode = p.Option("WifiMode");
        return mode switch
        {
            "Skip" => new SkipWifiSettings(),
            "XML" => new XmlWifiSettings(Required(p, "WifiXml")),
            "Open" or "WPA2PSK" or "WPA3SAE" => new ParameterizedWifiSettings(
                Required(p, "Ssid"), mode == "Open" ? "" : Required(p, "WifiPassword"), true,
                Enum.Parse<WifiAuthentications>(mode), p.Has("WifiHidden")),
            _ => new InteractiveWifiSettings()
        };
    }

    private static ILanguageSettings GetPeLanguage(SetupProfile p, bool alternate)
    {
        if (!alternate || !p.SetLocale)
            return new InteractiveLanguageSettings();
        var inputs = p.Keyboard.Split(';').Select(keyboard => LocaleCatalog.ResolveKeyboard(keyboard, p.UserLocale)).ToArray();
        if (inputs.FirstOrDefault() is not LocaleAndKeyboard primary || !Engine.ImageLanguages.TryGetValue(p.UiLocale, out var language))
            return new InteractiveLanguageSettings();
        return new UnattendedLanguageSettings(language,
            primary, inputs.ElementAtOrDefault(1), inputs.ElementAtOrDefault(2),
            primary.Locale.GeoLocation ?? Engine.Lookup<GeoLocation>("244"));
    }

    private static IPESettings GetPe(SetupProfile p, string architecture)
    {
        switch (p.InstallationMethod)
        {
            case "Custom":
                if (!p.ConfirmWipe)
                    throw new SetupException("Na stronie dysku potwierdź uruchomienie własnego skryptu WinPE.", "Confirm running your WinPE script on the disk page.");
                return new ScriptPESetttings(Required(p, "PEText"));
            case "Generated":
                if (!p.AcceptEula || p.KeyMode is not (KeyMode.Default or KeyMode.Skip))
                    throw new SetupException("Dla trybu DISM zaakceptuj licencję i wybierz domyślną obsługę klucza albo pominięcie pytania o klucz.",
                        "For DISM mode, accept the license and choose default key handling or skip the key prompt.");
                if (p.ImageName.IndexOfAny(['%', '"', '&', '|', '<', '>', '^', '!', '\r', '\n']) >= 0 && p.ImageMode == ImageMode.ExactName)
                    throw new SetupException("W tym trybie użyj indeksu obrazu dla takiej nazwy.", "Use the image index in this mode for that image name.");

                ITargetDiskSettings disk = p.Option("TargetDisk") switch
                {
                    "Interactive" => new InteractiveTargetDiskSettings(),
                    "Checked" => new GeneratedTargetDiskSettings(
                        p.Number("DiskMinGiB", 64), p.Number("DiskMaxGiB", 4096),
                        p.Has("DiskAnyIndex") ? null : p.DiskNumber, !p.Has("AllowExistingPartitions"),
                        p.Has("DiskInterface"), p.Has("DiskMedia")),
                    "Script" => new ScriptTargetDiskSettings(Required(p, "TargetDiskScript")),
                    _ => new FixedTargetDiskSettings(p.DiskNumber)
                };
                IPartitionSettings layout = new InteractivePartitionSettings();
                if (p.Has("CustomDiskPart"))
                {
                    if (!p.ConfirmWipe)
                        throw new SetupException("Na stronie dysku potwierdź uruchomienie własnego skryptu DiskPart.", "Confirm running your DiskPart script on the disk page.");
                    layout = new CustomPartitionSettings(Required(p, "DiskPartText"));
                }
                else if (p.WipesDisk)
                    layout = new UnattendedPartitionSettings(disk,
                        p.Firmware == Firmware.Gpt ? PartitionLayout.GPT : PartitionLayout.MBR,
                        p.Firmware == Firmware.Gpt && p.Recovery ? RecoveryMode.Partition : RecoveryMode.None,
                        p.EfiMiB, p.RecoveryMiB,
                        WindowsSize: p.HasSeparateData ? p.WindowsMiB : 0,
                        SinglePartition: p.DiskMode == DiskMode.Single);
                IInstallFromSettings image = p.ImageMode switch
                {
                    ImageMode.Edition => new EditionMetadataInstallFromSettings(
                        p.SelectedEdition!.Id, architecture == "amd64" ? "x64" : architecture,
                        p.Target.Windows11 ? 22000 : 10240, p.Target.Windows11 ? 999999 : 21999),
                    ImageMode.ExactName => new NameInstallFromSettings(p.ImageName),
                    ImageMode.Index => new IndexInstallFromSettings(p.ImageIndex),
                    _ => new InteractiveInstallFromSettings()
                };
                IPagingFileSettings paging = p.Option("Paging") switch
                {
                    "None" => new NoPagingFileSettings(),
                    "Custom" => new CustomPagingFileSettings(p.Number("PagingInitial", 2048), p.Number("PagingMax", 4096)),
                    _ => new AutomaticPagingFileSettings()
                };
                return new GeneratePESettings(layout, image, paging, p.Has("DisableDefender"), p.Has("Disable8Dot3"),
                    p.Has("PauseFormat"), p.Has("PauseReboot"), p.Has("CompactOs"), p.Has("SkipIntegrity"));
            default:
                return new DefaultPESettings(new InteractiveEditionSettings(),
                    p.Target.Windows11 && (p.UsesLegacyWindows11 || p.Has("BypassRequirements")), p.Has("UseConfigurationSet"));
        }
    }
}
