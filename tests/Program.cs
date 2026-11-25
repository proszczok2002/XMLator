using System.Reflection;
using System.Buffers.Binary;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using XMLator;

internal static class Checks
{
    private static readonly XNamespace U = AnswerFile.U;
    private static int passed;

    [STAThread]
    private static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args is ["--validate-iso", var isoPath, var isoTarget, var isoArchitecture, var choice])
        {
            var isoProfile = new SetupProfile { TargetName = isoTarget, Architecture = isoArchitecture };
            if (int.TryParse(choice, out int index))
            {
                isoProfile.ImageMode = ImageMode.Index;
                isoProfile.ImageIndex = index;
            }
            else if (choice != "-")
            {
                isoProfile.ImageMode = ImageMode.Edition;
                isoProfile.PresetEdition = choice;
            }
            try
            {
                OpticalImage.ValidateWindows(isoPath, isoProfile);
                Console.WriteLine("ISO metadata matches.");
            }
            catch (SetupException error)
            {
                Console.WriteLine(error.Message);
                Environment.ExitCode = 1;
            }
            return;
        }
        if (args is ["--close-ui", var application])
        {
            SelectionAudit.VerifyClose(application);
            return;
        }
        if (args is ["--extract-iso-audit", var image, var outputFolder])
        {
            string label = OpticalImage.Extract(image, outputFolder, long.MaxValue, percent =>
            {
                if (percent % 10 == 0) Console.WriteLine($"Extracted {percent}%.");
            });
            Console.WriteLine("ISO label: " + label);
            return;
        }
        if (args is ["--microsoft-audit"])
        {
            MicrosoftIsoAudit.Run().GetAwaiter().GetResult();
            return;
        }
        if (args is ["--proszczok-catalog-audit"])
        {
            VerifyLiveProszczokCatalog().GetAwaiter().GetResult();
            return;
        }
        if (args is ["--usb-live-audit", var worker, var sourceIso, var evidence])
        {
            UsbLiveAudit.Run(worker, sourceIso, evidence).GetAwaiter().GetResult();
            return;
        }
        if (args is ["--usb-live-audit", var usbWorker, var usbIso, var usbEvidence, var previousOverlay, var driverFolder])
        {
            UsbLiveAudit.Run(usbWorker, usbIso, usbEvidence, previousOverlay, driverFolder).GetAwaiter().GetResult();
            return;
        }
        if (args is [var operation, var output, var version, var firmwareName, var edition, var culture] &&
            operation is "--vm-answer" or "--vm-no-key-answer" or "--vm-blank-password-answer" or "--vm-offline-answer" or
                "--vm-embedded-script-answer" or "--vm-embedded-script-profile")
        {
            var setupFirmware = Enum.Parse<Firmware>(firmwareName);
            var p = new SetupProfile { TargetName = version, Architecture = "amd64", AcceptEula = true,
                DiskMode = DiskMode.SystemAndWindows, Firmware = setupFirmware, DiskNumber = 0, CreateUsers = true, SetLocale = true,
                ImageMode = ImageMode.Edition, PresetEdition = edition,
                FirstRun = operation == "--vm-offline-answer" ? FirstRunMode.Offline : FirstRunMode.SkipSupported,
                Logon = LogonMode.FirstAdministrator, KeyMode = operation == "--vm-answer" ? KeyMode.Default : KeyMode.Skip };
            if (p.Target.Windows11 && setupFirmware == Firmware.Mbr)
                p.Advanced["BypassRequirements"] = "1";
            if (p.Target.Server)
            {
                p.SetServerAdministratorPassword = true;
                p.SessionOptions["ServerAdministratorPassword"] = "Xmlator-Server-Lab-2026!";
            }
            p.ConfirmWipe = true;
            p.Users.Add(new LocalUser { Name = "XmlatorTest", Password =
                operation is "--vm-blank-password-answer" or "--vm-embedded-script-answer" or "--vm-embedded-script-profile" ? "" : "Xmlator-Lab-2026!" });
            if (operation is "--vm-embedded-script-answer" or "--vm-embedded-script-profile")
            {
                p.EmbeddedScripts.AddRange([
                    new(@"Set-Content -LiteralPath 'C:\Windows\Setup\Scripts\Xmlator-Audit-System.txt' -Value 'System OK'", "System", "Ps1"),
                    new(@"reg.exe add HKU\DefaultUser\Software\XMLatorAudit /v DefaultUser /t REG_SZ /d OK /f", "DefaultUser", "Cmd"),
                    new(@"reg.exe add HKCU\Software\XMLatorAudit /v FirstLogon /t REG_SZ /d OK /f", "FirstLogon", "Cmd"),
                    new(@"reg.exe add HKCU\Software\XMLatorAudit /v UserOnce /t REG_SZ /d OK /f", "UserOnce", "Cmd")
                ]);
                var feedbackHub = ModernOptions.Engine.Bloatwares.Values.Single(app => app.DisplayName == "Feedback Hub");
                p.Advanced["App." + feedbackHub.Id] = "1";
            }
            LocaleCatalog.ApplyLanguage(p, culture);
            if (operation == "--vm-embedded-script-profile")
                IniProfile.Save(output, p);
            else
                MediaPackage.Export(output, null, AnswerFile.Build(p), profile: null);
            return;
        }
        if (args is ["--vm-export-ui", var vmExecutable, var vmProfile, var vmAnswer])
        {
            Task.Run(() => SelectionAudit.ExportVmAnswer(vmExecutable, vmProfile, vmAnswer)).GetAwaiter().GetResult();
            return;
        }
        if (args is ["--selection-audit", var executable])
        {
            Task.Run(() => SelectionAudit.Run(executable)).GetAwaiter().GetResult();
            return;
        }
        if (args is ["--iso-sources-ui", var isoExecutable, var sourceVersion, var sourceArchitecture])
        {
            Task.Run(() => SelectionAudit.IsoSources(isoExecutable, sourceVersion, sourceArchitecture)).GetAwaiter().GetResult();
            return;
        }
        if (args is ["--preferences-ui", var portableExecutable])
        {
            Task.Run(() => SelectionAudit.Preferences(portableExecutable)).GetAwaiter().GetResult();
            return;
        }
        if (args is ["--sign-in-ui", var signInExecutable])
        {
            Task.Run(() => SelectionAudit.SignIn(signInExecutable)).GetAwaiter().GetResult();
            return;
        }
        if (args is ["--selection-audit", var appExecutable, var profile])
        {
            Task.Run(() => SelectionAudit.Run(appExecutable, profile: profile)).GetAwaiter().GetResult();
            return;
        }
        if (args is ["--live-download-ui", var downloadExecutable, var expectedHash])
        {
            Task.Run(() => SelectionAudit.Run(downloadExecutable, expectedHash)).GetAwaiter().GetResult();
            return;
        }
        if (args is ["--live-download-ui", var retailExecutable, var retailHash, var retailVersion, var retailRelease, var retailArch])
        {
            Task.Run(() => SelectionAudit.Run(retailExecutable, retailHash, isoVersion: retailVersion, isoRelease: retailRelease, isoArchitecture: retailArch)).GetAwaiter().GetResult();
            return;
        }
        if (args is ["--proszczok-download-ui", var siteExecutable, var siteFileName, var siteFileHash])
        {
            Task.Run(() => SelectionAudit.Run(siteExecutable, siteFileHash, proszczokImage: siteFileName)).GetAwaiter().GetResult();
            return;
        }
        if (args is ["--proszczok-ui", var siteApp, var siteImage, var siteProfile])
        {
            Task.Run(() => SelectionAudit.Run(siteApp, profile: siteProfile, proszczokImage: siteImage)).GetAwaiter().GetResult();
            return;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args is ["--updater-audit", var olderExe, var releaseExe, var workDirectory])
        {
            Task.Run(() => UpdateAudit.Run(olderExe, releaseExe, workDirectory)).GetAwaiter().GetResult();
            return;
        }
        if (args is ["--updater-public", var installedExe, var publishedExe])
        {
            Task.Run(() => UpdateAudit.CheckPublished(installedExe, publishedExe)).GetAwaiter().GetResult();
            return;
        }
        if (args is ["--release-settings-ui", var settingsExe, var languages, var settingsWork])
        {
            Task.Run(() => SelectionAudit.ReleaseSettings(settingsExe, languages, settingsWork)).GetAwaiter().GetResult();
            return;
        }
        if (args is ["--dark-theme-ui", var themeExe, var themeWork])
        {
            Task.Run(() => SelectionAudit.DarkTheme(themeExe, themeWork)).GetAwaiter().GetResult();
            return;
        }
        if (args is ["--boot-mode-ui", var bootExecutable, var bootEvidence])
        {
            Task.Run(() => SelectionAudit.BootMode(bootExecutable, bootEvidence)).GetAwaiter().GetResult();
            return;
        }
        if (args is ["--updater-public-ui", var updateExe, var referenceExe, var updateWork])
        {
            Task.Run(() => SelectionAudit.PublicUpdate(updateExe, referenceExe, updateWork)).GetAwaiter().GetResult();
            return;
        }
        if (args is ["--interface-audit"] or ["--interface-audit", _])
        {
            RunWinFormsChecks(() => InterfaceAudit.Run(args.ElementAtOrDefault(1)));
            return;
        }
        if (args is ["--catalog-audit"])
        {
            InterfaceAudit.CheckCatalogs();
            return;
        }
        if (args is ["--interface-audit", var interfaceLanguages, var interfaceTheme])
        {
            RunWinFormsChecks(() => InterfaceAudit.Run(interfaceLanguages, Enum.Parse<AppTheme>(interfaceTheme)));
            return;
        }
        if (args is ["--iso-source-audit"])
        {
            MicrosoftIsoAudit.Discover();
            return;
        }
        if (args is ["--microsoft-resolve", var versionToResolve, var releaseToResolve, var architectureToResolve, var cultureToResolve])
        {
            MicrosoftIsoAudit.Resolve(versionToResolve, releaseToResolve, architectureToResolve, cultureToResolve);
            return;
        }
        if (args is ["--nvda-audit"])
        {
            NvdaAudit.Run();
            return;
        }
        if (args is ["--nvda-interface-audit"])
        {
            NvdaAudit.RunInterface();
            return;
        }
        if (args is ["--nvda-sign-in-audit"])
        {
            NvdaAudit.RunFirstSignIn();
            return;
        }
        if (args is ["--nvda-initial-setup-audit"])
        {
            NvdaAudit.RunInitialSetup();
            return;
        }
        if (args is ["--nvda-localized-phrases", var nvdaLanguage])
        {
            NvdaAudit.RunLocalizedPhrases(nvdaLanguage);
            return;
        }
        if (args is ["--nvda-disk-iso-audit", var mismatchIso])
        {
            NvdaAudit.RunDiskAndIso(mismatchIso);
            return;
        }
        if (args is ["--nvda-iso-audit"])
        {
            NvdaAudit.RunIso();
            return;
        }
        if (args is ["--nvda-iso-source-audit"])
        {
            NvdaAudit.RunIsoSources();
            return;
        }
        if (args is ["--nvda-proszczok-audit"])
        {
            NvdaAudit.RunProszczok();
            return;
        }
        if (args is ["--tooltip-audit"])
        {
            TooltipAudit.Run();
            return;
        }
        if (args is ["--dialog-audit"])
        {
            RunWinFormsChecks(() => WizardDialogAudit.Run());
            return;
        }
        if (args is ["--dialog-audit", var dialogTheme])
        {
            RunWinFormsChecks(() => WizardDialogAudit.Run(Enum.Parse<AppTheme>(dialogTheme, ignoreCase: true)));
            return;
        }
        if (args is ["--disk-dialog-audit", var diskDialogTheme])
        {
            RunWinFormsChecks(() => WizardDialogAudit.Run(Enum.Parse<AppTheme>(diskDialogTheme), diskSettingsOnly: true));
            return;
        }
        if (args is ["--method-dialog-audit", var methodDialogTheme])
        {
            RunWinFormsChecks(() => WizardDialogAudit.Run(Enum.Parse<AppTheme>(methodDialogTheme), methodsOnly: true));
            return;
        }
        if (args is ["--sign-in-prompts-audit"])
        {
            WizardDialogAudit.VerifySignInPrompts();
            return;
        }
        string folder = Directory.CreateTempSubdirectory("XMLator-checks-").FullName;
        try
        {
            var schema = new XmlSchemaSet();
            using var stream = typeof(Schneegans.Unattend.Configuration).Assembly
                .GetManifestResourceStream("Schneegans.Unattend.resource.autounattend.xsd")!;
            using var reader = XmlReader.Create(stream);
            schema.Add(U.NamespaceName, reader);
            foreach (var target in Target.All)
            {
                foreach (string architecture in target.Architectures)
                {
                    foreach (Firmware firmware in Enum.GetValues<Firmware>())
                    {
                        if (firmware == Firmware.Mbr && (target.Windows11 || architecture is "arm64" or "ia64") ||
                            firmware == Firmware.Gpt && !target.CanUseGpt(architecture))
                            continue;
                        foreach (DiskMode mode in Enum.GetValues<DiskMode>().Where(m => m is not (DiskMode.Single or DiskMode.Available) || firmware == Firmware.Mbr))
                        {
                            var p = new SetupProfile
                            {
                                TargetName = target.Name, Architecture = architecture, AcceptEula = true,
                                DiskMode = mode, Firmware = firmware, DiskNumber = 2, PartitionNumber = 3,
                                ImageMode = ImageMode.Index, ImageIndex = 2, CreateUsers = true, SetLocale = true
                            };
                            p.ConfirmWipe = true;
                            p.Users.Add(new LocalUser { Name = "Łucja & Ewa", Password = "S&>ł-test!" });
                            XDocument doc = AnswerFile.Build(p);
                            doc.Validate(schema, null);
                            VerifyLayout(p, doc);
                        }
                    }
                }
            }

            VerifyProfilesAndPackage(folder, schema);
            VerifyModern(schema);
            VerifyEditionSelection(schema);
            VerifyInstallChoices(schema);
            VerifyRegionsAndAccounts(folder, schema);
            VerifyAccountSetupByTarget(folder, schema);
            VerifyAccountSignInMatrix(folder, schema);
            VerifyFirstRunAndExplorer(folder, schema);
            VerifyDiskApproval(schema);
            VerifyBootModeProfiles(folder, schema);
            VerifyCustomDiskProfiles(schema);
            RunWinFormsChecks(() =>
            {
                VerifyWizard();
                VerifyConditionalFields();
            });
            VerifyOpticalImage(folder);
            VerifyUsb(folder);
            Task.Run(() => VerifyIsoDownload(folder)).GetAwaiter().GetResult();
            Task.Run(() => VerifyProszczok(folder)).GetAwaiter().GetResult();
            Console.WriteLine($"{passed} checks passed.");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void RunWinFormsChecks(Action check)
    {
        using var loop = new ApplicationContext();
        Exception? failure = null;
        Application.Idle += CheckControls;
        Application.Run(loop);
        if (failure is not null)
            throw new InvalidOperationException("The wizard control tests failed.", failure);

        void CheckControls(object? sender, EventArgs args)
        {
            Application.Idle -= CheckControls;
            try { check(); }
            catch (Exception error) { failure = error; }
            finally { loop.ExitThread(); }
        }
    }

    private static void VerifyAccountSetupByTarget(string folder, XmlSchemaSet schema)
    {
        const string password = "Only-for-server-2026!";
        foreach (var target in Target.All)
        foreach (string architecture in target.Architectures)
        {
            var profile = new SetupProfile { TargetName = target.Name, Architecture = architecture, HideOnline = true, HideWireless = true };
            profile.FirstRun = FirstRunMode.Custom;
            XDocument answer = AnswerFile.Build(profile);
            answer.Validate(schema, null);
            Assert(answer.Descendants(U + "HideWirelessSetupInOOBE").Any() == target.ClientOobeNetwork &&
                answer.Descendants(U + "HideOnlineAccountScreens").Any() == target.ClientOobeNetwork,
                $"Emit supported network and Microsoft account OOBE options for {target.Name}, {architecture}");
            Assert(!answer.Descendants(U + "AdministratorPassword").Any(), "Leave the built-in account password out by default");
            profile.SetServerAdministratorPassword = true;
            profile.SessionOptions["ServerAdministratorPassword"] = password;
            answer = AnswerFile.Build(profile);
            answer.Validate(schema, null);
            var administrator = answer.Descendants(U + "AdministratorPassword").SingleOrDefault();
            Assert(target.Server ? administrator?.Element(U + "Value")?.Value ==
                Convert.ToBase64String(Encoding.Unicode.GetBytes(password + "AdministratorPassword")) &&
                (bool?)administrator.Element(U + "PlainText") == false : administrator is null,
                $"Set the optional built-in password only for Server in {target.Name}, {architecture}");
        }
        var server = new SetupProfile { TargetName = "Windows Server 2025", SetServerAdministratorPassword = true };
        AssertThrows<SetupException>(() => AnswerFile.Build(server), "Ask for the planned server password when exporting");
        server.SessionOptions["ServerAdministratorPassword"] = password;
        string path = Path.Combine(folder, "server-account.ini");
        IniProfile.Save(path, server);
        Assert(!File.ReadAllText(path).Contains(password) && IniProfile.Load(path).SetServerAdministratorPassword,
            "Keep the selected server option in a profile and leave out its password");
    }

    private static void VerifyAccountSignInMatrix(string folder, XmlSchemaSet schema)
    {
        const string usedPassword = "S3cure-Test & ż!";
        string output = Directory.CreateDirectory(Path.Combine(folder, "sign-in")).FullName;
        string savedXml = Path.Combine(output, "saved.xml");
        string script = Path.Combine(output, "first-logon.cmd");
        File.WriteAllText(script, "echo first-logon-fixture");
        int editions = 0, exports = 0, oemPairs = 0, requiredPasswordCases = 0;
        foreach (var target in Target.All)
        foreach (string architecture in target.Architectures)
        {
            var profile = new SetupProfile { TargetName = target.Name, Architecture = architecture, CreateUsers = true,
                ImageMode = ImageMode.Edition, KeyMode = KeyMode.Skip };
            var administrator = new LocalUser { Name = "Łucja" };
            profile.Users.AddRange([new LocalUser { Name = "StandardUser", Administrator = false,
                    Password = target.RequiresLocalAccountPassword ? "Standard-Test!23" : "" }, administrator,
                new LocalUser { Name = "SecondAdmin", Password = "Other-Test!23" }]);
            foreach (var edition in EditionCatalog.For(target, architecture))
            {
                editions++;
                profile.PresetEdition = edition.Id;
                foreach (LogonMode mode in Enum.GetValues<LogonMode>())
                foreach (bool obscure in new[] { false, true })
                foreach (string password in new[] { "", usedPassword })
                {
                    profile.Logon = mode;
                    profile.ObscurePasswords = obscure;
                    administrator.Password = password;
                    if (target.RequiresLocalAccountPassword && password.Length == 0)
                    {
                        AssertThrows<SetupException>(() => AnswerFile.Build(profile), $"Require a password for {target.Name}/{architecture}/{edition.Id}/{mode}");
                        requiredPasswordCases++;
                        continue;
                    }
                    XDocument answer = AnswerFile.Build(profile);
                    answer.Validate(schema, null);
                    VerifyCredentials(answer, mode, obscure, password);
                    MediaPackage.Export(savedXml, null, answer, profile);
                    string usbStage = Directory.CreateDirectory(Path.Combine(output, $"usb-{++exports}")).FullName;
                    var staged = MediaPackage.StageUsb(usbStage, AnswerFile.Build(profile), profile);
                    Assert(File.ReadAllBytes(savedXml).SequenceEqual(File.ReadAllBytes(Path.Combine(staged, AnswerFile.DefaultName))),
                        $"Identical XML and USB answer for {target.Name}/{architecture}/{edition.Id}/{mode}/{obscure}");
                }
            }
            profile.Logon = LogonMode.FirstAdministrator;
            administrator.Password = target.RequiresLocalAccountPassword ? usedPassword : "";
            profile.OemFiles.Add(new(script, OemPhase.FirstLogon));
            XDocument withOem = AnswerFile.Build(profile);
            string package = MediaPackage.Export(savedXml, output, withOem, profile)!;
            var usbParent = Directory.CreateDirectory(Path.Combine(output, $"oem-{exports}")).FullName;
            string usbPackage = MediaPackage.StageUsb(usbParent, AnswerFile.Build(profile), profile);
            string relativeScript = Path.Combine("sources", "$OEM$", "$$", "Setup", "Scripts", "XMLator-FirstLogon.cmd");
            string standaloneScript = File.ReadAllText(Path.Combine(package, relativeScript));
            Assert(File.ReadAllBytes(savedXml).SequenceEqual(File.ReadAllBytes(Path.Combine(usbPackage, AnswerFile.DefaultName))) &&
                standaloneScript == File.ReadAllText(Path.Combine(usbPackage, relativeScript)), "Identical first-logon XML and OEM package at both entry points");
            string firstCommand = withOem.Descendants(U + "FirstLogonCommands").Single().Descendants(U + "CommandLine").Single().Value;
            Assert(target.ModernClient
                ? firstCommand.StartsWith($"cmd.exe /d /s /c \"{AnswerFile.StopAutomaticLogon} & ", StringComparison.Ordinal) && !standaloneScript.Contains("AutoLogonCount")
                : firstCommand.Contains("XMLator-FirstLogon.cmd") && standaloneScript.Contains(AnswerFile.StopAutomaticLogon),
                "Reset the logon count before OEM scripts without depending on script extraction in Windows 10/11");
            oemPairs++;
        }
        foreach (var target in Target.All.Where(target => target.Architectures.Length > 1))
        {
            var profile = new SetupProfile { TargetName = target.Name, Architecture = target.Architectures[0],
                ExtraArchitectures = [.. target.Architectures.Skip(1)], CreateUsers = true, Logon = LogonMode.FirstAdministrator, ObscurePasswords = false };
            string password = target.RequiresLocalAccountPassword ? usedPassword : "";
            profile.Users.Add(new LocalUser { Name = "MultiArch", Password = password });
            var answer = AnswerFile.Build(profile);
            answer.Validate(schema, null);
            var logons = answer.Descendants(U + "AutoLogon").ToArray();
            Assert(logons.Select(logon => (string?)logon.Parent?.Attribute("processorArchitecture")).SequenceEqual(target.Architectures) &&
                logons.All(logon => logon.Element(U + "Username")?.Value == "MultiArch" &&
                    logon.Element(U + "Password")?.Element(U + "Value")?.Value == password), $"Matching credentials on each requested architecture for {target.Name}");
        }
        Console.WriteLine($"First sign-in: {editions} system/architecture/edition combinations, {exports} identical standalone and USB XML pairs, {oemPairs} OEM pairs, {requiredPasswordCases} Windows Server 2008 password requirements.");

        static void VerifyCredentials(XDocument answer, LogonMode mode, bool obscure, string password)
        {
            var logon = answer.Descendants(U + "AutoLogon").SingleOrDefault();
            var local = answer.Descendants(U + "LocalAccount").Single(user => user.Element(U + "Name")?.Value == "Łucja");
            var localPassword = local.Element(U + "Password");
            bool automatic = mode == LogonMode.FirstAdministrator;
            bool encoded = obscure && password.Length > 0;
            string value = encoded ? Convert.ToBase64String(Encoding.Unicode.GetBytes(password + "Password")) : password;
            if (automatic || password.Length > 0)
                Assert(localPassword?.Element(U + "Value")?.Value == value && (bool?)localPassword.Element(U + "PlainText") == !encoded,
                    "Use explicitly blank or matching local credentials");
            else
                Assert(localPassword is null, "Keep the manual blank password optional");
            string? firstCommand = answer.Descendants(U + "FirstLogonCommands").SingleOrDefault()?.Descendants(U + "CommandLine").Single().Value;
            Assert(automatic
                ? logon?.Element(U + "Username")?.Value == "Łucja" && (bool?)logon.Element(U + "Enabled") == true &&
                    (int?)logon.Element(U + "LogonCount") == 1 && XNode.DeepEquals(logon.Element(U + "Password"), localPassword) &&
                    firstCommand == AnswerFile.FirstLogonCommand() && firstCommand.Contains("/v AutoAdminLogon /t REG_SZ /d 0") &&
                    firstCommand.Contains("/v AutoLogonCount /t REG_DWORD /d 0")
                : logon is null && firstCommand is null, "Select the first local administrator and set exactly one sign-in");
        }
    }

    private static void VerifyOpticalImage(string folder)
    {
        string source = Path.Combine(folder, "fixture.iso");
        string extraction = Path.Combine(folder, "optical-image");
        var builder = new DiscUtils.Iso9660.CDBuilder { UseJoliet = true, VolumeIdentifier = "XMLATOR_TEST" };
        var files = new Dictionary<string, byte[]>
        {
            ["setup.exe"] = [1, 2, 3],
            ["bootmgr"] = [6, 7, 8],
            [@"sources\boot.wim"] = [9, 10, 11],
            [@"sources\install.swm"] = TestWimCatalog(),
            [@"sources\install2.swm"] = [15, 16, 17],
            [@"efi\boot\bootx64.efi"] = [18, 19, 20]
        };
        foreach (var (path, data) in files)
            builder.AddFile(path, data);
        builder.Build(source);
        byte[] original = File.ReadAllBytes(source);
        var progress = new List<int>();
        var profile = new SetupProfile { ImageMode = ImageMode.Edition, PresetEdition = "Professional" };
        Assert(OpticalImage.Extract(source, extraction, long.MaxValue, progress.Add, profile) == "XMLATOR_TEST",
            "Read the Joliet ISO label");
        Assert(files.All(file => File.ReadAllBytes(Path.Combine(extraction, file.Key)).SequenceEqual(file.Value)),
            "Extract complete nested Windows media with both extensionless files and files with extensions");
        Assert(original.SequenceEqual(File.ReadAllBytes(source)) && progress[0] == 0 && progress[^1] == 100,
            "Keep the original ISO and report progress through completion");

        foreach (var (wrongTarget, architecture, edition) in new[]
        {
            ("Windows 10", "amd64", "Professional"), ("Windows 11", "arm64", "Professional"), ("Windows 11", "amd64", "Core")
        })
        {
            var wrong = new SetupProfile { TargetName = wrongTarget, Architecture = architecture, ImageMode = ImageMode.Edition, PresetEdition = edition };
            string blocked = Path.Combine(folder, "incompatible-iso");
            AssertThrows<SetupException>(() => OpticalImage.Extract(source, blocked, long.MaxValue, profile: wrong),
                "Reject a mismatched Windows version, architecture or edition");
            Assert(!Directory.Exists(blocked), "Check ISO compatibility before extracting or formatting");
        }

        files.Remove(@"sources\install.swm");
        files.Remove(@"sources\install2.swm");
        foreach (string extension in new[] { "wim", "esd" })
        {
            builder = new DiscUtils.Iso9660.CDBuilder { UseJoliet = true, VolumeIdentifier = "XMLATOR_TEST" };
            foreach (var (path, data) in files)
                builder.AddFile(path, data);
            builder.AddFile($@"sources\install.{extension}", TestWimCatalog());
            string imagePath = Path.Combine(folder, $"{extension}.iso");
            builder.Build(imagePath);
            OpticalImage.ValidateWindows(imagePath, profile);
            AssertThrows<SetupException>(() => OpticalImage.ValidateWindows(imagePath,
                new SetupProfile { ImageMode = ImageMode.Index, ImageIndex = 2 }), "Check the selected image index for " + extension);
        }

        static byte[] TestWimCatalog()
        {
            const string xml = """
                <WIM><IMAGE INDEX="1"><NAME>Windows 11 Pro</NAME><WINDOWS><EDITIONID>Professional</EDITIONID><ARCH>9</ARCH>
                <INSTALLATIONTYPE>Client</INSTALLATIONTYPE><PRODUCTTYPE>WinNT</PRODUCTTYPE><VERSION><BUILD>26300</BUILD></VERSION>
                </WINDOWS></IMAGE></WIM>
                """;
            byte[] metadata = Encoding.Unicode.GetBytes("\uFEFF" + xml);
            byte[] file = new byte[208 + metadata.Length];
            "MSWIM\0\0\0"u8.CopyTo(file);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), 208);
            BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(72), (ulong)metadata.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(80), 208);
            BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(88), (ulong)metadata.Length);
            metadata.CopyTo(file, 208);
            return file;
        }
    }

    private static void VerifyLayout(SetupProfile profile, XDocument doc)
    {
        XElement? configuration = doc.Descendants(U + "DiskConfiguration").SingleOrDefault();
        Assert((configuration is not null) == (profile.WipesDisk || profile.FormatsPartition),
            $"{profile.TargetName} {profile.Architecture} {profile.Firmware} {profile.DiskMode}");
        Assert(doc.Descendants(U + "InstallToAvailablePartition").Any() == (profile.DiskMode == DiskMode.Available),
            "Emit automatic target selection only for the explicit Legacy BIOS choice");
        if (profile.DiskMode == DiskMode.Interactive)
        {
            Assert(!doc.Descendants(U + "InstallTo").Any(), "Leave target selection interactive");
            return;
        }
        if (profile.DiskMode == DiskMode.Available)
        {
            Assert(!doc.Descendants(U + "InstallTo").Any() && (bool)doc.Descendants(U + "InstallToAvailablePartition").Single(),
                "Let Setup select the first available partition");
            return;
        }
        var installTo = doc.Descendants(U + "InstallTo").Single();
        Assert((int)installTo.Element(U + "DiskID")! == profile.DiskNumber, "Preserve target disk number");
        if (!profile.WipesDisk)
        {
            Assert((int)installTo.Element(U + "PartitionID")! == profile.PartitionNumber, "Use existing partition");
            if (profile.FormatsPartition)
                Assert(!(bool)configuration!.Descendants(U + "WillWipeDisk").Single() &&
                    configuration.Descendants(U + "ModifyPartition").Single().Element(U + "Format")?.Value == "NTFS" &&
                    !configuration.Descendants(U + "CreatePartitions").Any(), "Format only the existing target partition");
            return;
        }
        var created = configuration!.Descendants(U + "CreatePartition").ToArray();
        var modified = configuration.Descendants(U + "ModifyPartition").ToArray();
        int number = (int)installTo.Element(U + "PartitionID")!;
        Assert((string?)modified.Single(x => (int)x.Element(U + "PartitionID")! == number).Element(U + "Label") == "Windows",
            "Install to the Windows partition");
        Assert(modified.Select(x => (int)x.Element(U + "Order")!).SequenceEqual(Enumerable.Range(1, modified.Length)), "Continuous modification order");
        if (Layout.PrepareRecoveryInWinPe(profile))
        {
            var plan = XMLator.Layout.Plan(profile);
            var commands = doc.Root!.Elements(U + "settings").Single(pass => (string?)pass.Attribute("pass") == "windowsPE")
                .Descendants(U + "Path").Select(path => path.Value).ToArray();
            Assert(created.Length == 0 && number == 3 && modified.Length == 1 && modified[0].Element(U + "Format") is null &&
                !(bool)configuration.Descendants(U + "WillWipeDisk").Single(), "Use the partition created by the guarded WinPE script");
            Assert(plan is [{ Type: "EFI" }, { Type: "MSR" }, { Windows: true, Size: 0 }, { Label: "Recovery" }] &&
                commands.All(command => command.Length <= 255) && commands.Any(command => command.Contains("PEFirmwareType")) &&
                commands.Any(command => command.Contains("shrink desired=" + profile.RecoveryMiB)),
                "Reserve the recovery partition after flexible C using guarded Windows PE commands");
            return;
        }
        Assert((string?)created[0].Element(U + "Type") == (profile.Firmware == Firmware.Gpt ? "EFI" : "Primary"), "Boot partition type");
        Assert(created.Last().Element(U + "Size") is null && (bool)created.Last().Element(U + "Extend")!, "Extend only the last partition");
        if (profile.Firmware == Firmware.Gpt)
            Assert((int)created[1].Element(U + "Size")! == (profile.Target.OldMsr ? 128 : 16), "Version-specific MSR size");
        if (profile.HasSeparateData)
            Assert((string?)modified.Last().Element(U + "Letter") == "D", "Data partition letter");
        if (profile.DiskMode == DiskMode.Single)
            Assert(created.Length == 1 && (bool)modified[0].Element(U + "Active")!, "Single active MBR partition");
    }

    private static void VerifyProfilesAndPackage(string folder, XmlSchemaSet schema)
    {
        string payload = Directory.CreateDirectory(Path.Combine(folder, "payload")).FullName;
        string output = Directory.CreateDirectory(Path.Combine(folder, "output")).FullName;
        string inf = Path.Combine(payload, "test.inf");
        File.WriteAllText(inf, "[Version]");
        string script = Path.Combine(payload, "Żółty skrypt.cmd");
        File.WriteAllText(script, "echo fixture");
        var profile = new SetupProfile
        {
            AcceptEula = true, ImageMode = ImageMode.ExactName, ImageName = "Obraz <test>",
            KeyMode = KeyMode.Enter, SetupKey = "XXXXX-XXXXX-XXXXX-XXXXX-XXXXX",
            DiskMode = DiskMode.SystemAndWindows, ConfirmWipe = true,
            CreateUsers = true, Logon = LogonMode.FirstAdministrator,
            WinPeDrivers = payload, InstalledDrivers = payload,
            OemFiles = [new(script, OemPhase.Specialize), new(script, OemPhase.FirstLogon), new(script, OemPhase.SetupComplete)]
        };
        profile.Users.Add(new LocalUser { AskName = true, AskPassword = true, Name = "Żaneta", Password = "only-for-testing" });
        profile.SessionOptions["WifiPassword"] = "synthetic-wifi";
        string ini = Path.Combine(output, "test.ini");
        IniProfile.Save(ini, profile);
        var loaded = IniProfile.Load(ini);
        var iniText = File.ReadAllText(ini);
        Assert(!iniText.Contains("only-for-testing") && !iniText.Contains("synthetic-wifi") && !iniText.Contains(profile.SetupKey), "Keep secrets out of INI");
        Assert(loaded.Users[0].AskName && loaded.Users[0].AskPassword && loaded.Users[0].Name.Length == 0 && loaded.OemFiles.SequenceEqual(profile.OemFiles), "Round-trip the reusable profile without customer names");
        Assert(!loaded.ConfirmWipe && loaded.SetupKey.Length == 0, "Require fresh disk confirmation and key");
        Throws<SetupException>(() => AnswerFile.Build(loaded), "Reject export when wipe is unconfirmed");

        var answer = AnswerFile.Build(profile);
        answer.Validate(schema, null);
        string xmlPath = Path.Combine(output, "Client.xml");
        string package = MediaPackage.Export(xmlPath, output, answer, profile)!;
        string file = Path.Combine(package, "Autounattend.xml");
        Assert(File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(xmlPath)), "Write the chosen XML and a matching answer file in the separate package");
        byte[] serialized = File.ReadAllBytes(file);
        Assert(serialized.AsSpan().StartsWith("<?xml"u8) && serialized.All(b => b < 128) &&
            XDocument.Load(file).ToString().Contains("Żaneta"), "Write a BOM-free answer and preserve Unicode with XML character references");
        Assert(File.Exists(Path.Combine(package, "$WinPEDriver$", "test.inf")), "WinPE drivers path");
        Assert(File.Exists(Path.Combine(package, "sources", "$OEM$", "$1", "XMLator", "Drivers", "test.inf")), "Installed driver path");
        var scriptPath = Path.Combine(package, "sources", "$OEM$", "$$", "Setup", "Scripts");
        Assert(File.Exists(Path.Combine(scriptPath, "SetupComplete.cmd")) && File.Exists(Path.Combine(scriptPath, "XMLator", "003.cmd")), "SetupComplete and script payload");
        Assert(answer.ToString().Contains("AutoLogonCount") &&
            !File.ReadAllText(Path.Combine(scriptPath, "XMLator-FirstLogon.cmd")).Contains("AutoLogonCount"), "Reset the automatic sign-in count in XML before OEM scripts");
        foreach (var (target, modern, legacy) in new[] { ("Windows 7", false, true), ("Windows 10", true, true), ("Windows 11", true, false) })
        {
            var drivers = new SetupProfile { TargetName = target, InstalledDrivers = payload };
            string driverPackage = MediaPackage.Export(Path.Combine(output, target + ".xml"), output, AnswerFile.Build(drivers), drivers)!;
            string command = File.ReadAllText(Path.Combine(driverPackage, "sources", "$OEM$", "$$", "Setup", "Scripts", "XMLator-Specialize.cmd"));
            Assert(command.Contains("/subdirs /install") == modern && command.Contains("pnputil.exe -i -a") == legacy &&
                command.Contains("if errorlevel 1") == (modern && legacy), "Select an available PnPUtil syntax on " + target);
        }
        Assert(XDocument.Load(file).ToString().Contains(Convert.ToBase64String(Encoding.Unicode.GetBytes("only-for-testingPassword"))), "Encode the account password as Windows expects");
        string standalone = Directory.CreateDirectory(Path.Combine(folder, "standalone")).FullName;
        var noFiles = new SetupProfile();
        string standaloneXml = Path.Combine(standalone, "Autounattend.xml");
        Assert(MediaPackage.Export(standaloneXml, null, AnswerFile.Build(noFiles), noFiles) is null &&
            Directory.EnumerateFileSystemEntries(standalone).SequenceEqual([standaloneXml]), "Export only XML when there are no extra media files");
        Assert(MediaPackage.Export(standaloneXml, null, answer, profile: null) is null &&
            File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(standaloneXml)) &&
            Directory.EnumerateFileSystemEntries(standalone).SequenceEqual([standaloneXml]),
            "Copy the same resolved USB answer file without exporting its OEM package again");
        Throws<SetupException>(() => MediaPackage.Export(Path.Combine(payload, "test.xml"), output, answer, profile), "Keep the exported XML out of auxiliary source files");
    }

    private static void VerifyModern(XmlSchemaSet schema)
    {
        var minimal = AnswerFile.Build(new SetupProfile());
        minimal.Validate(schema, null);
        Assert(!minimal.Descendants(U + "DiskConfiguration").Any() && !minimal.Descendants(U + "AcceptEula").Any(), "Modern default preserves interactive setup");
        var profile = new SetupProfile
        {
            TargetName = "Windows 10", ExtraArchitectures = ["x86", "arm64"], AcceptEula = true,
            KeyMode = KeyMode.Generic, GenericEdition = "pro", ComputerNameMode = "fixed", ComputerName = "TEST-PC",
            EmbeddedScripts = [new("Write-Output 'fixture'", "System", "Ps1"), new("echo fixture", "FirstLogon", "Cmd")],
            Components = [new("specialize", "Microsoft-Windows-Shell-Setup", "<RegisteredOwner>Test &amp; QA</RegisteredOwner>")]
        };
        profile.Advanced["ShowFileExtensions"] = "1";
        profile.Advanced["UseNarrator"] = "1";
        profile.Advanced["WifiMode"] = "WPA2PSK";
        profile.Advanced["Ssid"] = "Synthetic WLAN";
        profile.SessionOptions["WifiPassword"] = "synthetic-wlan";
        profile.Advanced["Wallpaper"] = "Solid";
        profile.Advanced["App.RemoveTeams"] = "1";
        var doc = AnswerFile.Build(profile);
        doc.Validate(schema, null);
        Assert(doc.Descendants(U + "ComputerName").Count() == 3 && doc.Descendants(U + "RegisteredOwner").Count() == 3, "Multi-architecture settings");
        Assert(doc.Descendants(U + "Key").Count() == 3 && !doc.Descendants(U + "ProductKey").Any(e => !e.HasElements), "Generic setup key does not become activation key");
        Assert(doc.Descendants().Count(e => e.Name.LocalName == "Extensions") == 1 && doc.ToString().Contains("fixture"), "Modern extension scripts appear once for all architectures");

        var dism = new SetupProfile { AcceptEula = true, DiskMode = DiskMode.SystemAndWindows, ConfirmWipe = true, ImageMode = ImageMode.Index };
        dism.Advanced["PE"] = "Generated";
        dism.ConfirmWipe = true;
        var dismXml = AnswerFile.Build(dism);
        dismXml.Validate(schema, null);
        Assert(!dismXml.Descendants(U + "DiskConfiguration").Any() && dismXml.Descendants().Any(e => e.Name.LocalName == "PEScriptCopy"), "Alternate WinPE implementation");
        dism.Advanced["PE"] = "Custom";
        dism.UpdateDiskOptions();
        dism.ConfirmWipe = false;
        Throws<SetupException>(() => AnswerFile.Build(dism), "Require custom WinPE confirmation");
    }

    private static void VerifyConditionalFields()
    {
        const BindingFlags members = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(Wizard);
        var catalog = ProszczokIsoCatalog.Parse("""
            <a href='Windows%207%20x64.iso'>Windows 7</a>
            <a href='Windows%2010%2022H2.iso'>Windows 10</a>
            <a href='Windows%2011%2026H2.iso'>Windows 11</a>
            """);
        bool offline = false;
        bool nextRelease = false;
        TaskCompletionSource<IsoProduct[]>? microsoftPending = null;
        TaskCompletionSource? publishMicrosoft = null;
        int microsoftAttempts = 0;
        using var wizard = new Wizard(null)
        {
            FindMicrosoftIsos = async (_, version, arch, _, cancel, report) =>
            {
                if (offline)
                    throw new System.Net.Http.HttpRequestException("Testing an unavailable catalog");
                var products = MicrosoftIsoCatalog.Products.Where(product => product.Version == version && product.Architectures.Contains(arch));
                if (version == "Windows 11" && nextRelease)
                    products = products.Prepend(new("Windows 11", "26H2", ["amd64"]));
                var offers = products.ToArray();
                if (microsoftPending is not { } pending)
                    return offers;
                if (++microsoftAttempts == 1)
                    return [];
                await publishMicrosoft!.Task.WaitAsync(cancel);
                report(offers);
                return await pending.Task.WaitAsync(cancel);
            },
            FindSiteIsos = (target, arch, _) => Task.FromResult(offline ? [] : catalog
                .Where(item => item.Target == target && ProszczokIsoCatalog.SupportsArchitecture(item, arch)).ToArray())
        };
        var profile = (SetupProfile)type.GetField("profile", members)!.GetValue(wizard)!;
        type.GetField("usbDisks", members)!.SetValue(wizard, Array.Empty<UsbDisk>());
        var step = type.GetField("step", members)!;
        var show = type.GetMethod("ShowStep", members)!;
        wizard.Show();
        foreach (bool polish in new[] { false, true })
        {
            type.GetMethod("ChangeLanguage", members)!.Invoke(wizard, [polish ? "pl" : "en"]);
            foreach (var target in Target.All)
            {
                profile.TargetName = target.Name;
                foreach (string arch in target.Architectures)
                {
                    profile.Architecture = arch;
                    var expectedBoot = new List<Firmware>();
                    if (target.CanUseGpt(arch))
                        expectedBoot.Add(Firmware.Gpt);
                    if (!target.Windows11 && arch is not ("arm64" or "ia64"))
                        expectedBoot.Add(Firmware.Mbr);
                    foreach (Firmware expectedFirmware in expectedBoot)
                    {
                        profile.DiskMode = DiskMode.Interactive;
                        profile.Firmware = expectedFirmware;
                        Show(3);
                        var disk = Field<ComboBox>("How should the disk and partitions be selected?", "Jak wybrać dysk i partycje?");
                        var boot = Field<ComboBox>("Installer boot mode", "Tryb rozruchu instalatora");
                        var modes = disk.Items.OfType<Choice<DiskMode>>().ToDictionary(choice => choice.Value);
                        bool legacy = expectedFirmware == Firmware.Mbr;
                        Assert(boot.Visible && boot.Focused && boot.SelectedItem is Choice<Firmware?> choice &&
                            choice.Value == expectedFirmware && boot.Items.OfType<Choice<Firmware?>>().Select(item => item.Value)
                                .SequenceEqual(expectedBoot.Select(firmware => (Firmware?)firmware)),
                            $"{target.Name}/{arch}/{expectedFirmware}: focus the firmware list first and select a supported mode");
                        Assert(modes.ContainsKey(DiskMode.Single) == legacy && modes.ContainsKey(DiskMode.Available) == legacy &&
                            modes.Count == (legacy ? 8 : 6), $"{target.Name}/{arch}/{expectedFirmware}: offer only the applicable layouts");
                        foreach (var diskOption in modes.Values)
                        {
                            disk.SelectedItem = diskOption;
                            Assert(boot.Visible && profile.Firmware == expectedFirmware && UsbMedia.RequiredFirmware(profile) == expectedFirmware.ToString(),
                                $"{target.Name}/{arch}/{expectedFirmware}/{diskOption.Value}: keep the selected boot mode and use it for USB");
                            if (diskOption.Value == DiskMode.Single)
                                Assert(XMLator.Layout.Plan(profile) is [{ Windows: true, Active: true, Format: "NTFS" }],
                                    $"{target.Name}/{arch}: create one active Windows partition");
                        }
                    }
                }

                profile.DiskMode = DiskMode.Interactive;
                profile.Architecture = target.Architectures[0];
                Show(8);
                var source = Field<ComboBox>("ISO source", "Źródło obrazu ISO");
                var expected = new List<IsoSource> { IsoSource.Local };
                if (target.ModernClient)
                    expected.Add(IsoSource.Microsoft);
                if (catalog.Any(item => item.Target == target))
                    expected.Add(IsoSource.Proszczok);
                Assert(source.Visible == (expected.Count > 1) &&
                    source.Items.OfType<Choice<IsoSource>>().Select(choice => choice.Value).SequenceEqual(expected) &&
                    Field<TextBox>("Windows ISO path", "Ścieżka do obrazu ISO Windows").Visible,
                    $"{target.Name}, {(polish ? "PL" : "EN")}: show only matching online sources and always expose the local ISO path");
            }
        }
        type.GetMethod("ChangeLanguage", members)!.Invoke(wizard, ["en"]);
        profile.TargetName = "Windows 11";
        profile.Architecture = "amd64";
        profile.ImageMode = ImageMode.Edition;
        profile.PresetEdition = "EnterpriseS";
        Show(8);
        Assert(Field<ComboBox>("ISO source", "Źródło obrazu ISO").Items.OfType<Choice<IsoSource>>()
            .Select(choice => choice.Value).SequenceEqual([IsoSource.Local, IsoSource.Proszczok]),
            "Microsoft's public retail ISO is not offered for Enterprise LTSC");
        profile.ImageMode = ImageMode.Interactive;
        profile.TargetName = "Windows 7";
        profile.Architecture = "x86";
        Show(8);
        Assert(!Field<ComboBox>("ISO source", "Źródło obrazu ISO").Visible,
            "Windows 7 x86 does not list an explicitly marked x64 image");
        profile.TargetName = "Windows 11";
        profile.Architecture = "amd64";
        Show(8);
        var isoSource = Field<ComboBox>("ISO source", "Źródło obrazu ISO");
        var refresh = Field<Button>("Refresh ISO sources", "Odśwież źródła ISO");
        var editions = Field<ComboBox>("ISO release (Home / Pro / Education)", "Wydanie ISO (Home / Pro / Education)");
        var path = Field<TextBox>("Windows ISO path", "Ścieżka do obrazu ISO Windows");
        path.Text = @"F:\an existing test image.iso";
        isoSource.SelectedIndex = 1;
        nextRelease = true;
        refresh.PerformClick();
        Assert(editions.Items.Count == 2 && editions.Text == "25H2", "Refresh discovers a future release and preserves the current selection");
        editions.SelectedIndex = 0;
        Assert(editions.Text == "26H2" && !Descendants(wizard).OfType<ComboBox>().Any(item => item.AccessibleName == "ISO architecture"),
            "A new release uses the architecture already selected in step one");
        offline = true;
        wizard.Activate();
        Assert(editions.Focus(), "The ISO release list has keyboard focus before its source becomes unavailable");
        refresh.PerformClick();
        Application.DoEvents();
        Assert(!isoSource.Visible && !editions.Visible && path.Visible && path.Focused && path.Text.EndsWith("test image.iso"),
            "Remove both unavailable catalogs and move focus to the unchanged local path");
        offline = false;
        refresh.PerformClick();
        Assert(isoSource.Visible && isoSource.SelectedIndex == 0 && path.Visible, "Restore available sources without switching from the local file");

        nextRelease = false;
        foreach (string version in new[] { "Windows 10", "Windows 11" })
        {
            profile.TargetName = version;
            microsoftAttempts = 0;
            microsoftPending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            publishMicrosoft = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Show(8);
            var source = Field<ComboBox>("ISO source", "Źródło obrazu ISO");
            Assert(source.Items.OfType<Choice<IsoSource>>().Select(choice => choice.Value)
                .SequenceEqual([IsoSource.Local, IsoSource.Proszczok]), $"{version}: site is usable while Microsoft is checked");
            source.SelectedIndex = 1;
            source.Select();
            publishMicrosoft.SetResult();
            Assert(SpinWait.SpinUntil(() =>
            {
                Application.DoEvents();
                return source.Items.Count == 3;
            }, 3000) && microsoftAttempts == 2 && !microsoftPending.Task.IsCompleted,
                $"{version}: retry and show a confirmed Microsoft ISO before all checks finish, without refreshing");
            Assert(source.SelectedItem is Choice<IsoSource> { Value: IsoSource.Proszczok } && source.Focused,
                $"{version}: preserve the user's source and focus when Microsoft becomes available");
            microsoftPending.SetResult(MicrosoftIsoCatalog.Products.Where(product => product.Version == version).ToArray());
            microsoftPending = null;
        }

        profile.TargetName = "Windows 10";
        profile.Firmware = Firmware.Gpt;
        profile.DiskMode = DiskMode.SystemAndWindows;
        profile.Advanced["PE"] = "Generated";
        profile.Advanced["DetectFirmware"] = "1";
        Show(3);
        var layouts = Field<ComboBox>("How should the disk and partitions be selected?", "Jak wybrać dysk i partycje?");
        Assert(layouts.Items.OfType<Choice<DiskMode>>().Select(choice => choice.Value)
            .SequenceEqual([DiskMode.Interactive, DiskMode.SystemAndWindows]) &&
            Field<ComboBox>("Installer boot mode", "Tryb rozruchu instalatora").SelectedItem is Choice<Firmware?> { Value: null } &&
            Field<NumericUpDown>("EFI or BIOS boot partition size in MiB", "Rozmiar partycji rozruchowej EFI lub BIOS w MiB").Visible,
            "DISM offers only applicable layouts and selects firmware detection on the first list");
        profile.Advanced["CustomDiskPart"] = "1";
        Show(3);
        Assert(!Field<ComboBox>("How should the disk and partitions be selected?", "Jak wybrać dysk i partycje?").Visible &&
            profile.RequiresCustomPeConsent, "A custom DiskPart script replaces the wizard's layout controls and requires confirmation");
        profile.Advanced["PE"] = "Custom";
        Show(3);
        Assert(!Field<ComboBox>("How should the disk and partitions be selected?", "Jak wybrać dysk i partycje?").Visible &&
            !Field<Button>("Advanced Windows Setup settings…", "Zaawansowane ustawienia Windows Setup…").Visible,
            "A custom WinPE script hides both the layout and the Windows Setup screens settings");
        profile.Advanced.Clear();
        profile.DiskMode = DiskMode.Interactive;
        Show(3);
        var boots = Field<ComboBox>("Installer boot mode", "Tryb rozruchu instalatora");
        layouts = Field<ComboBox>("How should the disk and partitions be selected?", "Jak wybrać dysk i partycje?");
        boots.SelectedIndex = 1;
        Assert(layouts.Items.OfType<Choice<DiskMode>>().Count(choice => choice.Value == DiskMode.Available) == 1 &&
            boots.Focused, "Selecting Legacy BIOS keeps focus and adds the first-partition choice");
        layouts.SelectedItem = layouts.Items.OfType<Choice<DiskMode>>().Single(choice => choice.Value == DiskMode.Available);
        Assert(profile.DiskMode == DiskMode.Available &&
            !Field<NumericUpDown>("Disk number in Windows Setup", "Numer dysku według Windows Setup").Visible,
            "Automatic partition selection applies from the main list without an irrelevant disk number");
        boots.SelectedIndex = 0;
        Assert(profile.DiskMode == DiskMode.Interactive && layouts.SelectedItem is Choice<DiskMode> { Value: DiskMode.Interactive } &&
            layouts.Items.OfType<Choice<DiskMode>>().All(choice => choice.Value != DiskMode.Available),
            "Switching to UEFI safely restores manual partition selection");
        boots.SelectedIndex = 1;
        layouts.SelectedItem = layouts.Items.OfType<Choice<DiskMode>>().Single(choice => choice.Value == DiskMode.Single);
        Show(8);
        Assert(!Descendants(wizard).OfType<ComboBox>().Any(combo => combo.AccessibleName == "USB boot mode") &&
            Descendants(wizard).OfType<Label>().Any(label => label.Visible && label.Text.Contains("USB boot mode — Legacy BIOS")) &&
            profile.Firmware == Firmware.Mbr, "Review uses the Legacy BIOS mode selected on the disk page");
        Show(5);
        var accounts = Field<ComboBox>("Create local accounts during installation?", "Czy utworzyć konta lokalne podczas instalacji?");
        var firstRun = Field<ComboBox>("How should initial Windows setup run?", "Jak przeprowadzić pierwszą konfigurację Windows?");
        var signIn = Field<ComboBox>("First sign-in", "Pierwsze logowanie");
        var cleanup = Field<CheckBox>("Remove cached answer files after automatic administrator sign-in",
            "Usuń kopie pliku odpowiedzi po automatycznym zalogowaniu administratora");
        Assert(!cleanup.Visible && firstRun.Items.Count == 2, "Only offer first-run choices that do not require a local account");
        accounts.SelectedIndex = 1;
        Assert(firstRun.Items.Count == 2 && signIn.Items.Count == 1 && !cleanup.Visible, "An empty account list does not offer automatic sign-in or the skip preset");
        profile.Users.Add(new LocalUser { Name = "LocalAdmin" });
        Show(5);
        firstRun = Field<ComboBox>("How should initial Windows setup run?", "Jak przeprowadzić pierwszą konfigurację Windows?");
        signIn = Field<ComboBox>("First sign-in", "Pierwsze logowanie");
        cleanup = Field<CheckBox>("Remove cached answer files after automatic administrator sign-in",
            "Usuń kopie pliku odpowiedzi po automatycznym zalogowaniu administratora");
        Assert(firstRun.Items.Count == 4 && signIn.Items.Count == 2 && !cleanup.Visible, "Adding an administrator makes automatic sign-in and the skip preset available");
        signIn.SelectedIndex = 1;
        cleanup.Checked = true;
        Assert(cleanup.Visible && profile.Has("CleanSensitive"), "Offer answer file cleanup when using automatic administrator sign-in");
        signIn.SelectedIndex = 0;
        Assert(!cleanup.Visible && !profile.Has("CleanSensitive"), "Turning off automatic sign-in also turns off answer file cleanup");
        signIn.SelectedIndex = 1;
        firstRun.SelectedIndex = 1;
        Field<Button>("Remove selected account", "Usuń zaznaczone konto").PerformClick();
        Assert(profile.Users.Count == 0 && profile.Logon == LogonMode.Manual && signIn.Items.Count == 1 &&
            firstRun.Items.Count == 2 && profile.SelectedFirstRun == FirstRunMode.WindowsDefault,
            "Removing the last administrator also clears incompatible OOBE and sign-in choices on the same page");
        profile.TargetName = "Windows Server 2025";
        Show(0);
        Assert(!Field<ComboBox>("Installation image architecture", "Architektura obrazu instalacyjnego").Visible &&
            Descendants(wizard).OfType<Label>().Any(label => label.Visible && label.Text.Contains("architecture — x64")),
            "Display the only supported architecture without an extra selector");

        void Show(int index)
        {
            step.SetValue(wizard, index);
            show.Invoke(wizard, [true]);
            Application.DoEvents();
        }

        T Field<T>(string en, string pl) where T : Control =>
            Descendants(wizard).OfType<T>().Single(item => item.AccessibleName == en || item.AccessibleName == pl);
    }

    private static void VerifyWizard()
    {
        using var wizard = new Wizard(null)
        {
            FindMicrosoftIsos = (_, version, _, _, _, _) => Task.FromResult(MicrosoftIsoCatalog.Products.Where(item => item.Version == version).ToArray()),
            FindSiteIsos = (target, _, _) => Task.FromResult(ProszczokIsoCatalog.Parse("<a href='Windows%2011%2026H2.iso'>ISO</a>")
                .Where(item => item.Target == target).ToArray())
        };
        Type type = typeof(Wizard);
        var profileField = type.GetField("profile", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var activeProfile = (SetupProfile)profileField.GetValue(wizard)!;
        static string? MouseHelp(Control control) => (control.Tag as Func<string>)?.Invoke();
        var step = type.GetField("step", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var switchLanguage = type.GetMethod("ChangeLanguage", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var render = type.GetMethod("ShowStep", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var go = type.GetMethod("ChangeStep", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var heading = (Label)type.GetField("heading", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(wizard)!;
        var next = type.GetField("next", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var testDisk = new UsbDisk(71, 8L << 30, "test-path", "test-serial", "USB Test", "X:");
        type.GetField("usbDisks", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(wizard, new[] { testDisk });
        using var xmlDialog = (SaveFileDialog)type.GetMethod("AnswerSaveDialog", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(wizard, null)!;
        using var iniDialog = (SaveFileDialog)type.GetMethod("ProfileSaveDialog", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(wizard, null)!;
        Assert(xmlDialog.FileName == "Autounattend.xml" && xmlDialog.DefaultExt == "xml" && xmlDialog.AddExtension,
            "Use the recognized answer file name and extension by default");
        Assert(iniDialog.FileName == Path.Combine(AppContext.BaseDirectory, "XMLator.ini") && iniDialog.InitialDirectory == AppContext.BaseDirectory,
            "Default the portable profile to the executable directory");
        switchLanguage.Invoke(wizard, ["en"]);
        var fileMenu = (ToolStripMenuItem)wizard.MainMenuStrip!.Items[0];
        Assert(fileMenu.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.ShortcutKeys == (Keys.Control | Keys.S)).Text?.Contains("XML") == true &&
            fileMenu.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.ShortcutKeys == (Keys.Control | Keys.Shift | Keys.S)).Text?.Contains("INI") == true,
            "Use separate shortcuts for XML export and the portable INI profile");
        wizard.Show();
        Application.DoEvents();
        Assert(wizard.IsHandleCreated && wizard.Visible, "Wizard opens an actual WinForms window");
        Assert(heading.Text == "Step 1 of 9. Windows version" && wizard.Text == $"{Wizard.ProductCaption} — {heading.Text}" &&
            Descendants(wizard).OfType<ComboBox>().Single(combo => combo.AccessibleName == "Installation image architecture").Text == "x64 (AMD64)",
            "Start the wizard with the selected architecture exposed as a list value");
        ((Button)next.GetValue(wizard)!).PerformClick();
        Assert(heading.Text == "Step 2 of 9. License" && activeProfile.Advanced.Count == 0 &&
            activeProfile.DiskMode == DiskMode.Interactive, "Proceed to the license without applying presets");
        go.Invoke(wizard, [-1]);
        Assert(heading.Text == "Step 1 of 9. Windows version", "Back returns to Windows selection");

        for (int page = 0; page < 9; page++)
        {
            step.SetValue(wizard, page);
            foreach (bool polish in new[] { true, false })
            {
                switchLanguage.Invoke(wizard, [polish ? "pl" : "en"]);
                render.Invoke(wizard, [false]);
                Assert(wizard.Text == $"{Wizard.ProductCaption} — {heading.Text}", "Expose the version and localized step in the window title");
                var controls = Descendants(wizard);
                Assert(controls.Where(c => c is TextBox or ComboBox or CheckBox or RadioButton or Button or ListBox)
                    .All(c => !string.IsNullOrWhiteSpace(c.AccessibleName)), $"Named standard controls, page {page + 1}, {(polish ? "PL" : "EN")}");
                Assert(!controls.OfType<TextBox>().Any(box => box.ReadOnly) && !heading.TabStop,
                    $"Use static labels and native lists, page {page + 1}, {(polish ? "PL" : "EN")}");
                Assert(controls.Where(c => c.TabStop).All(c => string.IsNullOrEmpty(c.AccessibleDescription)),
                    $"Offer help without an automatic field description on page {page + 1}, {(polish ? "PL" : "EN")}");
                Assert(!wizard.MainMenuStrip!.Items.OfType<ToolStripMenuItem>()
                    .SelectMany(item => item.DropDownItems.OfType<ToolStripMenuItem>()).Any(item => item.Text?.Contains("F1") == true) &&
                    !controls.OfType<Button>().Any(button => button.Text.Contains("F1")),
                    $"Remove the field-help command, page {page + 1}, {(polish ? "PL" : "EN")}");
            }
        }
        var review = Descendants(wizard);
        Assert(review.OfType<ListBox>().Single(list => list.AccessibleName == "Settings summary").Items.Count >= 5,
            "Review settings using a native list");
        bool previewListShown = false;
        using (var closePreview = new System.Windows.Forms.Timer { Interval = 100 })
        {
            closePreview.Tick += (_, _) =>
            {
                foreach (Form dialog in Application.OpenForms.Cast<Form>().Where(form => form.Text == AnswerFile.DefaultName).ToArray())
                {
                    previewListShown = Descendants(dialog).OfType<ListBox>().Any(list => dialog.ActiveControl == list &&
                        list.Items.Count > 0 && list.AccessibleName == "Autounattend.xml lines") &&
                        !Descendants(dialog).OfType<TextBox>().Any();
                    dialog.Close();
                }
            };
            closePreview.Start();
            review.OfType<Button>().Single(button => button.AccessibleName == "Preview the XML file…").PerformClick();
        }
        Assert(previewListShown, "Focus the native XML line list in the real preview dialog");
        var usb = review.OfType<ComboBox>().Single(c => c.AccessibleName == "USB drive");
        var saveXml = review.OfType<Button>().Single(c => c.AccessibleName == "Save XML…");
        var saveIni = review.OfType<Button>().Single(c => c.AccessibleName == "Save INI profile…");
        var prepare = review.OfType<Button>().Single(c => c.AccessibleName == "Create a bootable USB drive");
        var iso = review.OfType<TextBox>().Single(c => c.AccessibleName == "Windows ISO path");
        var saveGroup = (GroupBox)saveXml.Parent!.Parent!;
        var usbGroup = (GroupBox)prepare.Parent!.Parent!;
        Assert(usb.SelectedItem is Choice<UsbDisk?> { Value: null } && saveXml.Enabled && saveIni.Enabled && !prepare.Enabled &&
            saveIni.Parent == saveXml.Parent && saveGroup.AccessibleName == "Save files" &&
            usbGroup.AccessibleName == "Installation USB drive" && saveGroup.TabIndex < usbGroup.TabIndex &&
            saveXml.TabIndex < saveIni.TabIndex && usb.TabIndex < iso.Parent!.TabIndex && iso.Parent.TabIndex < prepare.TabIndex &&
            wizard.AcceptButton is null,
            "Group file actions separately from the USB drive, ISO and disabled create action in keyboard order");
        var originalSize = wizard.Size;
        wizard.Size = wizard.MinimumSize;
        Assert(saveGroup.Bottom <= usbGroup.Top && saveXml.Bottom <= saveIni.Top && saveXml.Right <= saveXml.Parent.ClientSize.Width &&
            saveIni.Right <= saveIni.Parent!.ClientSize.Width && prepare.Right <= prepare.Parent.ClientSize.Width,
            "Keep the groups and their actions visible in order at minimum window width");
        wizard.Size = originalSize;
        using var temporaryIso = new TemporaryIso();
        iso.Text = temporaryIso.Path;
        Assert(!prepare.Enabled, "ISO alone cannot start USB creation");
        iso.Clear();
        usb.SelectedIndex = 1;
        Assert(!prepare.Enabled, "USB drive alone cannot start USB creation");
        iso.Text = temporaryIso.Path + ".missing.iso";
        Assert(TemporaryIso.PumpUntil(() => Descendants(wizard).OfType<Label>().Any(c => c.Visible && c.Text == "Choose an existing Windows ISO file.")) &&
            !prepare.Enabled, "Show an inline message and keep creation disabled for a nonexistent ISO");
        iso.Text = $"\"{temporaryIso.Path}\"";
        Assert(TemporaryIso.PumpUntil(() => prepare.Enabled), "Enable USB preparation after choosing a drive and an existing ISO");
        usb.SelectedIndex = 0;
        Assert(!prepare.Enabled, "Removing the drive selection disables USB preparation again");
        usb.SelectedIndex = 1;
        var source = review.OfType<ComboBox>().Single(c => c.AccessibleName == "ISO source");
        var release = review.OfType<ComboBox>().Single(c => c.AccessibleName == "ISO release (Home / Pro / Education)");
        var siteFiles = review.OfType<ComboBox>().Single(c => c.AccessibleName == "ISO file from proszczok.pl");
        Assert(source.Items.OfType<Choice<IsoSource>>().Select(item => item.Value)
            .SequenceEqual([IsoSource.Local, IsoSource.Microsoft, IsoSource.Proszczok]) && !release.Visible && !siteFiles.Visible && iso.Visible,
            "Offer all three ISO sources and initially display only local selection");
        source.SelectedIndex = 1;
        Assert(!iso.Visible && !siteFiles.Visible && release.Visible && release.Text == "25H2" && !prepare.Enabled &&
            review.OfType<Button>().Count(c => c.Visible && c.AccessibleName == "Download and use ISO…") == 1,
            "Expose ISO downloads through native lists in the same wizard window");
        var isoLanguage = review.OfType<ComboBox>().Single(c => c.AccessibleName == "ISO language");
        Assert(isoLanguage.Visible && isoLanguage.Enabled && isoLanguage.Items.Count >= 35 &&
            !review.OfType<ComboBox>().Any(combo => combo.AccessibleName == "ISO architecture"),
            "Show Windows 11 download languages using the selected Windows architecture");
        source.SelectedIndex = 0;
        Assert(iso.Visible && iso.Text == $"\"{temporaryIso.Path}\"" && !release.Visible && prepare.Enabled,
            "Keep the local ISO and restore creation when toggling between sources");
        var progress = (ProgressBar)type.GetField("operationProgress", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(wizard)!;
        var report = type.GetMethod("ShowUsbProgress", BindingFlags.NonPublic | BindingFlags.Instance)!;
        report.Invoke(wizard, [new UsbProgress(UsbStage.Splitting)]);
        Assert(progress.Visible && progress.Enabled && progress.Style == ProgressBarStyle.Marquee && !heading.Visible &&
            wizard.Text.Contains("Splitting the WIM"), "Show an accessible indeterminate phase in the window title");
        report.Invoke(wizard, [new UsbProgress(UsbStage.Copy, 42)]);
        Assert(progress.Style == ProgressBarStyle.Continuous && progress.Value == 42 && progress.AccessibleName == "USB creation progress" &&
            progress.AccessibilityObject.Role == AccessibleRole.ProgressBar && wizard.Text.Contains("42%"),
            "Expose the actual copy percent on a named native progress bar and in the window title");
        var setBusy = type.GetMethod("SetBusy", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var reportOperation = type.GetMethod("ReportOperation", BindingFlags.NonPublic | BindingFlags.Instance)!;
        setBusy.Invoke(wizard, [false, null]);
        setBusy.Invoke(wizard, [true, "Saving the answer file…"]);
        Assert(progress.Visible && progress.Style == ProgressBarStyle.Marquee && wizard.Text.Contains("Saving the answer file"),
            "Show the shared progress bar during XML and package export");
        Assert(!saveIni.Enabled, "Disable profile saving while an export is running");
        reportOperation.Invoke(wizard, ["ISO download progress", "Downloading an ISO", 63]);
        Assert(progress.Visible && progress.Style == ProgressBarStyle.Continuous && progress.Value == 63 &&
            progress.AccessibleName == "ISO download progress" && wizard.Text.Contains("63%"), "Show numeric download progress on the shared bar");
        reportOperation.Invoke(wizard, ["ISO download progress", "Downloading an ISO", 64]);
        Assert(progress.Value == 64 && wizard.Text.Contains("64%"), "Refresh the exact percentage on every update");
        IntPtr progressHandle = progress.Handle;
        var reading = Task.Run(() =>
        {
            var element = System.Windows.Automation.AutomationElement.FromHandle(progressHandle);
            var range = (System.Windows.Automation.RangeValuePattern)element.GetCurrentPattern(System.Windows.Automation.RangeValuePattern.Pattern);
            return (element.Current.Name, range.Current.Value);
        });
        Assert(SpinWait.SpinUntil(() => { Application.DoEvents(); return reading.IsCompleted; }, 4000) &&
            reading.GetAwaiter().GetResult() == ("ISO download progress", 64d), "Windows UI Automation can read the label and percentage");
        setBusy.Invoke(wizard, [false, null]);
        Assert(!progress.Visible && heading.Visible && saveIni.Enabled, "Restore the title and INI action after the operation");
        iso.Clear();
        setBusy.Invoke(wizard, [true, null]);
        setBusy.Invoke(wizard, [false, null]);
        Assert(!prepare.Enabled && saveXml.Enabled, "Restoring the review after an operation preserves the missing-ISO state");

        var questionPage = type.GetMethod("QuestionPage", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var resultPage = type.GetMethod("UsbResultPage", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (bool polish in new[] { true, false })
        {
            switchLanguage.Invoke(wizard, [polish ? "pl" : "en"]);
            foreach (bool accept in new[] { true, false })
            {
                var (question, yes) = ((TaskDialogPage, TaskDialogButton))questionPage.Invoke(wizard,
                    ["USB dialog fixture", "Format a test drive?", MessageBoxIcon.Warning])!;
                var no = question.DefaultButton!;
                Assert(no.Text == (polish ? "&Nie" : "&No") && yes.Text == (polish ? "&Tak" : "&Yes"),
                    "Use localized native dialog mnemonics and default to No");
                var selected = accept ? yes : no;
                question.Created += (_, _) => wizard.BeginInvoke(selected.PerformClick);
                Assert(TaskDialog.ShowDialog(wizard, question) == selected, "Return the selected result from a real Windows Task Dialog");
            }
            foreach (bool downloaded in new[] { true, false })
            {
                var (result, save, close) = ((TaskDialogPage, TaskDialogButton, TaskDialogButton))resultPage.Invoke(wizard, ["USB is ready.", downloaded])!;
                Assert(result.DefaultButton == close && result.Buttons.Contains(save) && result.Buttons.Contains(close) &&
                    (result.Verification is not null) == downloaded && result.Verification?.Checked != true,
                    "Offer XML copy and a Close button, with optional ISO removal off by default");
                result.Created += (_, _) => wizard.BeginInvoke(close.PerformClick);
                Assert(TaskDialog.ShowDialog(wizard, result) == close, "Close the real USB completion dialog");
            }
        }
        step.SetValue(wizard, 1);
        go.Invoke(wizard, [1]);
        var editionList = Descendants(wizard).OfType<ComboBox>().Single(c => c.AccessibleName == "Which Windows edition should be installed?");
        Assert(editionList.Focused && wizard.Text.Contains("Edition and key"), "Start directly in the edition menu with the step in the title");
        Assert(!Descendants(wizard).Any(c => c.AccessibleName is "Current field description" or "Opis bieżącego pola" ||
            c.Text is "Current field description" or "Opis bieżącego pola"), "Remove the permanent field help panel");
        Assert(MouseHelp(editionList)?.Length > 0 && string.IsNullOrEmpty(editionList.AccessibilityObject.Description),
            "Keep edition help available on demand");
        var choices = editionList.Items.OfType<Choice<string>>().ToArray();
        Assert(new[] { "Home", "Pro", "Enterprise" }.All(edition => choices.Any(c => c.Label == edition)) &&
            choices.Any(c => c.Value == "Index"), "Expose common editions and the optional index in one menu");
        editionList.SelectedItem = choices.Single(c => c.Value == "edition:Professional");
        Assert(activeProfile.ImageMode == ImageMode.Edition && activeProfile.PresetEdition == "Professional", "Select Pro without reading installation media");
        string selectedEdition = editionList.Text;
        var theme = type.GetMethod("ChangeTheme", BindingFlags.NonPublic | BindingFlags.Instance)!;
        theme.Invoke(wizard, [AppTheme.Dark]);
        var darkEdition = Descendants(wizard).OfType<ComboBox>().Single(c => c.AccessibleName == "Which Windows edition should be installed?");
        Assert(((InterfacePreferences)type.GetField("preferences", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(wizard)!).Theme == AppTheme.Dark && darkEdition.Text == selectedEdition &&
            (SystemInformation.HighContrast || (wizard.BackColor.GetBrightness() < 0.25f && darkEdition.BackColor.GetBrightness() < 0.25f &&
                darkEdition.ForeColor.GetBrightness() > 0.75f)), "Theme the native edition list and preserve the user's selection when switching");
        Assert(!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || SystemInformation.HighContrast ||
            Application.ColorMode == SystemColorMode.Dark, "Enable native Windows 11 dark mode for standard controls");
        theme.Invoke(wizard, [AppTheme.Light]);
        Assert(wizard.BackColor == SystemColors.Control && Application.ColorMode == SystemColorMode.Classic, "Restore light application theme");
        go.Invoke(wizard, [1]);
        var disk = Descendants(wizard).OfType<ComboBox>().Single(control => control.AccessibleName == "How should the disk and partitions be selected?");
        var method = Descendants(wizard).OfType<ComboBox>().Single(control => control.AccessibleName == "Installation method");
        Assert(method.Items.OfType<Choice<string>>().Any(choice => choice.Label == "Microsoft Windows Setup (recommended)") &&
            method.Items.OfType<Choice<string>>().Any(choice => choice.Label == "DISM script from schneegans.de"), "Describe the Windows Setup and DISM methods in the disk wizard");
        disk.SelectedItem = disk.Items.OfType<Choice<DiskMode>>().Single(choice => choice.Value == DiskMode.SystemAndWindows);
        Assert(MouseHelp(disk)?.Contains("EFI and MSR") == true, "Describe the selected disk layout, including its system partitions");
        var efiSize = Descendants(wizard).OfType<NumericUpDown>().Single(control => control.AccessibleName == "EFI size in MiB");
        var efiEditor = efiSize.Controls.OfType<TextBox>().Single();
        Assert(efiEditor.Focus() && !efiEditor.ReadOnly && efiEditor.SelectedText == efiEditor.Text &&
            MouseHelp(efiEditor)?.Contains("Type the size in MiB") == true,
            "Focus the editable spin box, select the current size and expose typing instructions");
        efiEditor.SelectedText = "512";
        wizard.SelectNextControl(efiSize, true, true, true, true);
        var plan = Descendants(wizard).OfType<ListBox>().Single(control => control.AccessibleName == "Partition plan in disk order");
        Assert(activeProfile.EfiMiB == 512 && plan.Items.Cast<string>().Any(line => line.Contains("EFI boot — 512 MiB")),
            "Apply a typed EFI size to the profile and live partition plan after Tab");
        efiSize.UpButton();
        Assert(activeProfile.EfiMiB == 513, "Keep one-MiB spin increments");

        disk.SelectedItem = disk.Items.OfType<Choice<DiskMode>>().Single(choice => choice.Value == DiskMode.SystemWindowsData);
        var windowsSize = Descendants(wizard).OfType<NumericUpDown>().Single(control => control.AccessibleName == "C partition size in MiB");
        var windowsEditor = windowsSize.Controls.OfType<TextBox>().Single();
        windowsEditor.Focus();
        windowsEditor.SelectedText = "102400";
        var reviewShortcut = wizard.MainMenuStrip!.Items.OfType<ToolStripMenuItem>()
            .SelectMany(item => item.DropDownItems.OfType<ToolStripMenuItem>()).Single(item => item.ShortcutKeys == (Keys.Control | Keys.End));
        reviewShortcut.PerformClick();
        Assert(activeProfile.WindowsMiB == 102400 && heading.Text.Contains("Review and export"),
            "Commit direct entry before jumping to the review through the menu shortcut");
        step.SetValue(wizard, 3);
        render.Invoke(wizard, [true]);
        var approval = Descendants(wizard).OfType<CheckBox>().Single(control => control.AccessibleName?.StartsWith("Confirm deleting") == true);
        approval.Checked = true;
        Assert(activeProfile.ConfirmWipe && approval.Visible && MouseHelp(approval)?.Length > 0, "Check a visible and described disk approval");
        Assert(AnswerFile.Build(activeProfile).Descendants(U + "Size").Any(size => size.Value == "102400"),
            "Export the manually entered partition size as an integer in XML");
        go.Invoke(wizard, [1]);
        switchLanguage.Invoke(wizard, ["pl"]);
        var windowsLanguage = Descendants(wizard).OfType<ComboBox>().Single(control => control.AccessibleName == "Język instalatora i Windows");
        windowsLanguage.SelectedItem = windowsLanguage.Items.OfType<Choice<string>>().Single(choice => choice.Label == "Polski (Polska)");
        Assert(Descendants(wizard).OfType<ComboBox>().Single(control => control.AccessibleName == "Główna klawiatura").Text == "Polski (programisty)" &&
            Descendants(wizard).OfType<ComboBox>().Single(control => control.AccessibleName == "Kraj lub region").Text == "Polska",
            "Selecting Polish chooses Polish Programmers and Poland in visible lists");
        windowsLanguage.SelectedItem = windowsLanguage.Items.OfType<Choice<string>>().Single(choice => choice.Value == "en-GB");
        Assert(Descendants(wizard).OfType<ComboBox>().Single(control => control.AccessibleName == "Główna klawiatura").Text == "Angielski (Wielka Brytania)",
            "Selecting British English chooses the UK keyboard");
        Assert(MouseHelp(windowsLanguage)?.Length > 0, "Offer language help on demand");
        step.SetValue(wizard, 5);
        render.Invoke(wizard, [true]);
        var oobe = Descendants(wizard).OfType<ComboBox>().Single(control => control.AccessibleName == "Jak przeprowadzić pierwszą konfigurację Windows?");
        var wifiQuestion = Descendants(wizard).OfType<CheckBox>().Single(control => control.AccessibleName == "Pomiń pytanie o Wi-Fi");
        Assert(!wifiQuestion.Visible, "Hide individual controls until the user asks for them");
        oobe.SelectedItem = oobe.Items.OfType<Choice<FirstRunMode>>().Single(choice => choice.Value == FirstRunMode.Custom);
        Assert(wifiQuestion.Visible && MouseHelp(wifiQuestion)?.Contains("Ethernet") == true,
            "Name and explain the Wi-Fi skip control in custom first-run settings");
        var privacy = Descendants(wizard).OfType<ComboBox>().Single(control => control.AccessibleName == "Ustawienia zalecane i prywatność");
        Assert(privacy.Items.OfType<Choice<string>>().Any(choice => choice.Label == "Pomiń pytanie i włącz ustawienia zalecane"), "Use ordinary language for first-startup options");
        Assert(!Descendants(wizard).Any(c => c.AccessibleName?.Contains("wbudowanego administratora") == true), "Show only accounts added by the user");

        step.SetValue(wizard, 8);
        render.Invoke(wizard, [true]);
        Assert(Descendants(wizard).OfType<CheckBox>().Single(control => control.AccessibleName?.StartsWith("Potwierdzam usunięcie") == true).Checked &&
            AnswerFile.Build(activeProfile).Descendants(U + "WillWipeDisk").Any(), "Carry an actual disk approval through navigation to XML generation");

        var appFields = new Fields();
        type.GetMethod("RemovalOptions", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(wizard, [appFields]);
        using var appTable = appFields.Table;
        var apps = Descendants(appTable).OfType<CheckBox>().ToArray();
        var store = apps.Single(app => app.AccessibleName == "Microsoft Store — zalecamy zachować");
        Assert(MouseHelp(store)?.Contains("Zaznaczona aplikacja zostanie usunięta") == true &&
            Descendants(appTable).OfType<Label>().Any(label => label.Text.Contains("Odznaczona zostanie zachowana")), "Explain the checked and unchecked removal states visibly and accessibly");
        Assert(!store.Checked && apps.All(app => app.TabStop && app.AccessibilityObject.Role == AccessibleRole.CheckButton) &&
            apps.Any(app => app.AccessibleName?.StartsWith("Clipchamp — w zestawie Dodatki i gry") == true),
            "Show each app as a named checkbox with preset advice");
        Assert(MouseHelp(store)?.Contains("Przywrócenie Sklepu może być trudne") == true, "Expose the Store's detailed help");
        store.Checked = true;
        Assert(activeProfile.Has("App.RemoveStore") && activeProfile.RemoveApps == AppRemoval.Custom, "Manual checking selects the app for removal");
        store.Checked = false;
        Assert(!activeProfile.Has("App.RemoveStore"), "Unchecking keeps the app");
        var networkFields = new Fields();
        type.GetMethod("NetworkOptions", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(wizard, [networkFields]);
        using var networkTable = networkFields.Table;
        var wifi = Descendants(networkTable).OfType<ComboBox>().Single(control => control.AccessibleName == "Jak skonfigurować Wi-Fi?");
        Assert(!wifi.Items.OfType<Choice<string>>().Any(choice => choice.Value == "Skip"), "Keep screen skipping off the Wi-Fi profile list");
        wifi.SelectedItem = wifi.Items.OfType<Choice<string>>().Single(choice => choice.Value == "Open");
        Assert(activeProfile.Option("WifiMode") == "Open", "Configure an open network in advanced Wi-Fi settings");
        wizard.Dispose();
    }

    private static void VerifyRegionsAndAccounts(string folder, XmlSchemaSet schema)
    {
        var keyboardChoices = LocaleCatalog.Keyboards(polish: true);
        foreach (Choice<string> language in LocaleCatalog.Languages(polish: true))
        {
            var regional = new SetupProfile();
            LocaleCatalog.ApplyLanguage(regional, language.Value);
            AnswerFile.Build(regional).Validate(schema, null);
            Assert(regional.SetLocale && keyboardChoices.Any(choice => choice.Value.Equals(regional.Keyboard, StringComparison.OrdinalIgnoreCase)),
                "Give every display language a selectable keyboard default");
        }
        var profile = new SetupProfile { Express = "EnableAll", DarkInstalledWindows = true, HideOemRegistration = true };
        LocaleCatalog.ApplyLanguage(profile, "pl-PL");
        var document = AnswerFile.Build(profile);
        document.Validate(schema, null);
        Assert(profile.Keyboard == "0415:00000415" && profile.GeoId == "191" &&
            document.Descendants(U + "InputLocale").All(e => e.Value == "0415:00000415"), "Polish matches the correct Windows keyboard and region identifiers");
        Assert(document.Descendants(U + "ProtectYourPC").Single().Value == "1", "Recommended settings write the documented value");
        Assert(document.Descendants(U + "HideOEMRegistrationScreen").Single().Value == "true", "Write the OEM registration answer");
        Assert(document.ToString().Contains("AppsUseLightTheme") && document.ToString().Contains("SystemUsesLightTheme"), "Give new Windows accounts a dark theme");
        AppRemovalOptions.Apply(profile, AppRemoval.Extended);
        Assert(profile.Has("App.RemoveCamera") && !profile.Has("App.RemoveStore") && !profile.Has("App.RemoveSpeech"), "App removal presets keep Store and speech");
        AppRemovalOptions.Apply(profile, AppRemoval.Keep);
        Assert(!profile.Has("App.RemoveClipchamp") && !profile.Has("App.RemoveCamera"), "Keep apps when explicitly selected");

        string path = Path.Combine(folder, "server-old.ini");
        File.WriteAllText(path, """
            [XMLator]
            Version=1
            [Setup]
            TargetName="Windows Server 2025"
            QuickMode=true
            ManualAnswerName=true
            SetAdministratorPassword=true
            Logon="BuiltInAdministrator"
            """);
        var legacy = IniProfile.Load(path);
        Assert(legacy.CreateUsers && legacy.Logon == LogonMode.FirstAdministrator && legacy.Users.Single().AskName, "Migrate an old profile to a named account");
        legacy.Users[0].Name = "Klient";
        legacy.Users[0].Password = "only-for-testing";
        var server = AnswerFile.Build(legacy);
        server.Validate(schema, null);
        Assert(!server.Descendants(U + "AdministratorPassword").Any() && server.Descendants(U + "Username").Single().Value == "Klient",
            "Export the account supplied by the user on Server");
    }

    private static void VerifyFirstRunAndExplorer(string folder, XmlSchemaSet schema)
    {
        var profile = new SetupProfile { FirstRun = FirstRunMode.SkipSupported, Express = "EnableAll" };
        Throws<SetupException>(() => AnswerFile.Build(profile), "Require a local account for the first-run skip preset");
        profile.CreateUsers = true;
        profile.Users.Add(new LocalUser { Name = "Klient" });
        var skipped = AnswerFile.Build(profile);
        skipped.Validate(schema, null);
        Assert(new[] { "HideOnlineAccountScreens", "HideWirelessSetupInOOBE", "HideOEMRegistrationScreen" }
            .All(name => skipped.Descendants(U + name).Single().Value == "true") &&
            skipped.Descendants(U + "ProtectYourPC").Single().Value == "3" &&
            !skipped.Descendants(U + "SkipMachineOOBE").Any() && !skipped.Descendants(U + "AutoLogon").Any(),
            "Use documented first-run settings without forcing automatic sign-in");

        foreach (var target in Target.All)
        foreach (var architecture in target.Architectures)
        {
            var offline = new SetupProfile { TargetName = target.Name, Architecture = architecture,
                CreateUsers = target.ModernClient, FirstRun = FirstRunMode.Offline };
            if (target.ModernClient)
                offline.Users.Add(new LocalUser { Name = "LocalTest" });
            var answer = AnswerFile.Build(offline);
            answer.Validate(schema, null);
            Assert(answer.Descendants(U + "HideWirelessSetupInOOBE").Any() == target.ModernClient &&
                answer.Descendants(U + "HideOnlineAccountScreens").Any() == target.ModernClient &&
                !answer.Descendants(U + "HideOEMRegistrationScreen").Any() && !answer.Descendants(U + "ProtectYourPC").Any() &&
                !answer.Descendants(U + "AutoLogon").Any() && !answer.ToString().Contains("BypassNRO", StringComparison.Ordinal),
                $"Use only native network and account screen settings in the offline preset ({target.Name}/{architecture})");
            if (target.ModernClient)
            {
                offline.CreateUsers = false;
                Throws<SetupException>(() => AnswerFile.Build(offline), "Offline setup requires a local administrator");
            }
            offline.FirstRun = FirstRunMode.WindowsDefault;
            offline.Advanced["BypassNetworkCheck"] = "1";
            answer = AnswerFile.Build(offline);
            answer.Validate(schema, null);
            var registry = answer.Descendants(U + "Path").Where(node => node.Value.Contains("/v BypassNRO", StringComparison.Ordinal)).ToArray();
            Assert(registry.Length == (target.Windows11 ? 1 : 0) && registry.All(node =>
                (string?)node.Ancestors(U + "settings").Single().Attribute("pass") == "specialize") &&
                !answer.Descendants(U + "HideWirelessSetupInOOBE").Any(),
                $"Keep the legacy manual BypassNRO choice independent and Windows 11 only ({target.Name}/{architecture})");
        }
        string offlinePath = Path.Combine(folder, "first-run-offline.ini");
        profile.FirstRun = FirstRunMode.Offline;
        profile.Advanced["BypassNetworkCheck"] = "1";
        IniProfile.Save(offlinePath, profile);
        var restoredOffline = IniProfile.Load(offlinePath);
        Assert(restoredOffline.SelectedFirstRun == FirstRunMode.Offline && restoredOffline.Has("BypassNetworkCheck"),
            "Preserve the offline preset and an explicit compatibility option in INI");
        profile.Advanced.Clear();

        profile.CreateUsers = false;
        profile.FirstRun = FirstRunMode.WindowsDefault;
        profile.HideOnline = profile.HideWireless = true;
        var defaults = AnswerFile.Build(profile);
        Assert(!defaults.Descendants(U + "OOBE").Any(), "With standard first-run questions, ignore the inactive custom values");
        profile.Advanced["WifiMode"] = "Skip";
        string savedProfile = Path.Combine(folder, "first-run.ini");
        IniProfile.Save(savedProfile, profile);
        profile = IniProfile.Load(savedProfile);
        Assert(profile.SelectedFirstRun == FirstRunMode.Custom && profile.HideWireless && !profile.SkipWifi,
            "Migrate the legacy Wi-Fi skip into the first-run options when loading INI");
        var wifiOnly = AnswerFile.Build(profile);
        wifiOnly.Validate(schema, null);
        Assert(wifiOnly.Descendants(U + "HideWirelessSetupInOOBE").Single().Value == "true" &&
            wifiOnly.Descendants(U + "OOBE").Single().Elements().Count() == 1 &&
            !wifiOnly.ToString().Contains("BypassNRO", StringComparison.OrdinalIgnoreCase), "Generate only the independent Wi-Fi skip flag");
        profile.FirstRun = FirstRunMode.Custom;
        profile.Advanced["WifiMode"] = "Open";
        profile.Advanced["Ssid"] = "XMLator-Test";
        var wifiProfile = AnswerFile.Build(profile);
        wifiProfile.Validate(schema, null);
        Assert(wifiProfile.Descendants(U + "HideWirelessSetupInOOBE").Single().Value == "true" &&
            wifiProfile.ToString().Contains("XMLator-Test"), "A preconfigured Wi-Fi profile preserves the explicit first-run screen settings");

        profile.Advanced["ClassicContextMenu"] = "1";
        const string olderHandler = "86ca1aa0-34aa-4e8b-a509-50c905bae2a2";
        const string newerHandler = "51668040-5e31-4023-bbe3-149163bd80f1";
        string explorer = AnswerFile.Build(profile).ToString();
        Assert(explorer.Contains(olderHandler) && explorer.Contains(newerHandler), "Emit both classic Explorer context menu entries for Windows 11");
        profile.TargetName = "Windows 10";
        Assert(!AnswerFile.Build(profile).ToString().Contains(newerHandler), "Keep the Windows 11 Explorer setting out of Windows 10 XML");
    }

    private static void VerifyDiskApproval(XmlSchemaSet schema)
    {
        var profile = new SetupProfile { TargetName = "Windows 11", ExtraArchitectures = ["arm64"], DiskMode = DiskMode.SystemAndWindows };
        profile.ConfirmWipe = true;
        profile.Express = "EnableAll";
        var answer = AnswerFile.Build(profile);
        answer.Validate(schema, null);
        Assert(answer.Descendants(U + "WillWipeDisk").Count() == 2 && profile.ConfirmWipe && profile.Architecture == "amd64",
            "Keep the approval during multiarchitecture XML generation and unrelated edits");
        profile.DiskNumber = 1;
        Assert(!profile.ConfirmWipe, "Require a new approval for a different disk");
        Throws<SetupException>(() => AnswerFile.Build(profile), "Block explicit erasure until the new layout is confirmed");
    }

    private static void VerifyBootModeProfiles(string folder, XmlSchemaSet schema)
    {
        string ini = Path.Combine(folder, "boot-mode.ini");
        var profile = new SetupProfile { TargetName = "Windows 10", Firmware = Firmware.Mbr, ImageMode = ImageMode.Index,
            ImagePrompt = SetupPrompt.Never };
        foreach (var layout in new[] { DiskMode.Available, DiskMode.Single })
        {
            profile.DiskMode = layout;
            IniProfile.Save(ini, profile);
            var loaded = IniProfile.Load(ini);
            Assert(!loaded.UpdateDiskOptions() && loaded.DiskMode == layout && loaded.Firmware == Firmware.Mbr && !loaded.ConfirmWipe,
                "Keep compatible BIOS profiles and ask for fresh consent after loading");

            loaded.Firmware = Firmware.Gpt;
            loaded.ConfirmWipe = true;
            Throws<SetupException>(() => AnswerFile.Build(loaded), "Reject a BIOS-only layout paired with UEFI at export");
            Assert(loaded.UpdateDiskOptions() && loaded.DiskMode == DiskMode.Interactive && loaded.Firmware == Firmware.Gpt && !loaded.ConfirmWipe,
                "Restore manual selection without changing the selected boot mode or retaining consent");
            var answer = AnswerFile.Build(loaded);
            answer.Validate(schema, null);
            var image = answer.Descendants(U + "OSImage").Single();
            Assert(image.Element(U + "InstallTo") is null && image.Element(U + "InstallToAvailablePartition") is null &&
                image.Element(U + "WillShowUI")?.Value == "OnError", "Keep the disk selection screen available for a migrated UEFI profile");
        }

        profile.ExtraArchitectures.Add("arm64");
        Assert(profile.UpdateDiskOptions() && profile.Firmware == Firmware.Gpt && profile.DiskMode == DiskMode.Interactive,
            "Adding ARM64 invalidates the Legacy layout and selects the common UEFI mode");
        var mixed = new SetupProfile { TargetName = "Windows Server 2008 SP2", Architecture = "x86",
            ExtraArchitectures = ["ia64"], Firmware = Firmware.Mbr, DiskMode = DiskMode.Existing };
        Assert(mixed.UpdateDiskOptions() && mixed.DiskMode == DiskMode.Interactive && UsbMedia.RequiredFirmware(mixed) == "Mbr",
            "Keep manual destinations when legacy and Itanium images need different boot modes");
        var combined = AnswerFile.Build(mixed);
        combined.Validate(schema, null);
        Assert(!combined.Descendants(U + "InstallTo").Any(), "Export a manual profile for architectures without a common firmware mode");

        var dism = new SetupProfile { TargetName = "Windows 10", AcceptEula = true, ImageMode = ImageMode.Index,
            DiskMode = DiskMode.SystemAndWindows };
        dism.Advanced["PE"] = "Generated";
        foreach (var firmware in Enum.GetValues<Firmware>())
        {
            dism.Firmware = firmware;
            dism.ConfirmWipe = true;
            var answer = AnswerFile.Build(dism);
            answer.Validate(schema, null);
            var commands = answer.Descendants(U + "Path").Select(path => path.Value).ToList();
            int guard = commands.FindIndex(path => path.Contains("Restart the installation media", StringComparison.Ordinal) &&
                path.Contains(firmware.ToString().ToUpperInvariant(), StringComparison.Ordinal));
            int partition = commands.FindIndex(path => path.Contains(@"diskpart.exe /s X:\diskpart.txt", StringComparison.OrdinalIgnoreCase));
            Assert(guard >= 0 && partition > guard && UsbMedia.RequiredFirmware(dism) == firmware.ToString(),
                "DISM checks the selected firmware before partitioning and uses the same USB mode");
        }
        dism.Advanced["DetectFirmware"] = "1";
        dism.ConfirmWipe = true;
        var auto = AnswerFile.Build(dism);
        auto.Validate(schema, null);
        Assert(dism.DetectsDiskFirmware && UsbMedia.RequiredFirmware(dism) == "Any" &&
            auto.ToString().Contains(@"diskpart.exe /s X:\%LAYOUT%.txt", StringComparison.Ordinal),
            "With detection selected, DISM chooses the matching layout at boot");
        dism.BiosMiB = 150;
        dism.ConfirmWipe = true;
        Throws<SetupException>(() => AnswerFile.Build(dism), "Automatic mode requires a partition size suitable for EFI too");
    }

    private static void VerifyEditionSelection(XmlSchemaSet schema)
    {
        foreach (var target in Target.All)
        {
            foreach (string architecture in target.Architectures)
            {
                var choices = EditionCatalog.For(target, architecture);
                Assert(choices.Length > 0 && choices.Select(e => e.Id).Distinct().Count() == choices.Length,
                    $"Unique editions for {target.Name} {architecture}");
                foreach (var edition in choices)
                {
                    var p = new SetupProfile
                    {
                        TargetName = target.Name, Architecture = architecture, ImageMode = ImageMode.Edition,
                        PresetEdition = edition.Id, ImageIndex = 999, KeyMode = KeyMode.Skip,
                        SetupKey = "XXXXX-XXXXX-XXXXX-XXXXX-XXXXX", GenericEdition = "pro"
                    };
                    var doc = AnswerFile.Build(p);
                    doc.Validate(schema, null);
                    var selector = doc.Descendants(U + "MetaData").Single();
                    Assert((string?)selector.Element(U + "Key") == "/IMAGE/NAME" &&
                        (string?)selector.Element(U + "Value") == edition.ImageName, $"Select {target.Name} {architecture} {edition.Label} by media name");
                    var productKey = doc.Descendants(U + "ProductKey").Single();
                    Assert(productKey.Element(U + "Key") is null && (string?)productKey.Element(U + "WillShowUI") == "Never",
                        $"Suppress the setup key page without supplying any key for {target.Name} {architecture} {edition.Label}");
                    foreach (var firmware in Enum.GetValues<Firmware>())
                    {
                        if (firmware == Firmware.Mbr && (target.Windows11 || architecture is "arm64" or "ia64") ||
                            firmware == Firmware.Gpt && !target.CanUseGpt(architecture))
                            continue;
                        p.Firmware = firmware;
                        foreach (var layout in Enum.GetValues<DiskMode>().Where(mode => mode is not (DiskMode.Single or DiskMode.Available) || firmware == Firmware.Mbr))
                        {
                            p.DiskMode = layout;
                            p.PartitionNumber = 1;
                            p.ConfirmWipe = true;
                            var variant = AnswerFile.Build(p);
                            variant.Validate(schema, null);
                            var key = variant.Descendants(U + "ProductKey").Single();
                            Assert(key.Element(U + "Key") is null && (string?)key.Element(U + "WillShowUI") == "Never" &&
                                (string?)variant.Descendants(U + "MetaData").Single().Element(U + "Value") == edition.ImageName,
                                $"Match the no-key choice and image on {target.Name} {architecture} {edition.Label}, {firmware} {layout}");
                        }
                    }
                }
            }
        }
        var server2025 = new SetupProfile { TargetName = "Windows Server 2025", ImageMode = ImageMode.Edition, PresetEdition = "DatacenterDesktop" };
        Assert(AnswerFile.Build(server2025).Descendants(U + "MetaData").Single().Element(U + "Value")?.Value ==
            "Windows Server 2025 Datacenter (Desktop Experience)", "Match the Standard or Datacenter image using Server 2025 media names");
        server2025.PresetEdition = "DatacenterDesktopEval";
        Assert(AnswerFile.Build(server2025).Descendants(U + "MetaData").Single().Element(U + "Value")?.Value ==
            "Windows Server 2025 SERVERDATACENTER", "Use the exact WIM name on official Server 2025 Evaluation media");

        var dism = new SetupProfile
        {
            AcceptEula = true, DiskMode = DiskMode.SystemAndWindows, ConfirmWipe = true,
            ImageMode = ImageMode.Edition, PresetEdition = "Professional", KeyMode = KeyMode.Skip
        };
        dism.Advanced["PE"] = "Generated";
        dism.ConfirmWipe = true;
        var result = AnswerFile.Build(dism);
        result.Validate(schema, null);
        string batch = result.Descendants().Single(e => e.Name.LocalName == "PEScriptCopy").Value;
        Assert(batch.Contains("Edition : Professional") && batch.Contains("Architecture : x64") && batch.Contains("GEQ 22000"),
            "Find the edition, architecture and Windows build in WinPE");
        Assert(batch.IndexOf("if not defined XMLATOR_INDEX call :fail", StringComparison.Ordinal) <
            batch.IndexOf("diskpart.exe /s", StringComparison.Ordinal), "Resolve or fail before partitioning");
        Assert(batch.Contains("set \"IMG_PARAM=/Index:%XMLATOR_INDEX%\""), "Apply the index found at runtime");
        Assert(!result.Descendants(U + "ProductKey").Any(), "DISM applies the image without adding a product key");
        var home = new SetupProfile { ImageMode = ImageMode.Edition, PresetEdition = "Core", KeyMode = KeyMode.Generic, GenericEdition = "pro" };
        string expected = ModernOptions.Engine.Lookup<Schneegans.Unattend.WindowsEdition>("home").ProductKey;
        Assert(AnswerFile.Build(home).Descendants(U + "ProductKey").Single().Element(U + "Key")?.Value == expected,
            "Use the selected Home edition's generic key instead of a stored Pro preference");
    }

    private static void VerifyInstallChoices(XmlSchemaSet schema)
    {
        const string installationKey = "AAAAA-BBBBB-CCCCC-DDDDD-EEEEE";
        const string activationKey = "FFFFF-GGGGG-HHHHH-IIIII-JJJJJ";
        foreach (var target in Target.All)
        foreach (string architecture in target.Architectures)
        {
            var profile = new SetupProfile { TargetName = target.Name, Architecture = architecture, SetupKey = installationKey,
                ActivationKey = activationKey, GenericEdition = "pro" };
            var keyModes = target.ModernClient ? Enum.GetValues<KeyMode>() : [KeyMode.Default, KeyMode.Enter, KeyMode.Skip];
            foreach (bool accept in new[] { false, true })
            foreach (KeyMode mode in keyModes)
            foreach (ImageMode image in Enum.GetValues<ImageMode>())
            {
                if (mode == KeyMode.Generic && image == ImageMode.Edition)
                    continue; // Exact edition/key matching is covered by VerifyEditionSelection.
                profile.KeyMode = mode;
                profile.ImageMode = image;
                profile.ImageIndex = 2;
                profile.ImageName = "Sample image";
                profile.PresetEdition = EditionCatalog.For(target, architecture)[0].Id;
                profile.AcceptEula = accept;
                profile.UseSetupKeyForActivation = mode == KeyMode.Enter;
                XDocument answer = AnswerFile.Build(profile);
                answer.Validate(schema, null);
                var userData = answer.Descendants(U + "UserData").SingleOrDefault();
                Assert(((bool?)userData?.Element(U + "AcceptEula") == true) == accept, $"EULA {target.Name}/{architecture}/{mode}/{image}");
                string? prompt = (string?)userData?.Element(U + "ProductKey")?.Element(U + "WillShowUI");
                Assert(prompt == (mode switch { KeyMode.Default => null, KeyMode.Ask => "Always",
                    KeyMode.Skip or KeyMode.Firmware => "Never", _ => "OnError" }),
                    $"Setup key behavior {target.Name}/{architecture}/{mode}/{image}");
                var metadata = answer.Descendants(U + "MetaData").SingleOrDefault();
                Assert((string?)metadata?.Element(U + "Value") == (image switch
                {
                    ImageMode.Index => "2", ImageMode.ExactName => "Sample image",
                    ImageMode.Edition => profile.SelectedEdition!.ImageName, _ => null
                }), $"Image selection {target.Name}/{architecture}/{mode}/{image}");
                Assert(answer.Descendants(U + "ProductKey").Any(key => !key.HasElements && key.Value == installationKey) ==
                    (mode == KeyMode.Enter), $"Reuse the installation key for activation {target.Name}/{architecture}/{mode}/{image}");
            }

            profile.KeyMode = KeyMode.Enter;
            profile.UseSetupKeyForActivation = true;
            profile.UseActivationKey = true;
            XDocument withActivation = AnswerFile.Build(profile);
            withActivation.Validate(schema, null);
            Assert((string?)withActivation.Descendants(U + "ProductKey").Single(key => !key.HasElements) == activationKey,
                $"An explicit activation key takes precedence {target.Name}/{architecture}");

            foreach (string language in UiText.Languages.Select(choice => UiText.CultureName(choice.Value)))
            {
                LocaleCatalog.ApplyLanguage(profile, language);
                XDocument localized = AnswerFile.Build(profile);
                localized.Validate(schema, null);
                var locale = localized.Descendants(U + "UILanguage");
                Assert(locale.Any() && locale.All(field => field.Value == language), $"Apply the installation locale {language} to {target.Name}/{architecture}");
            }
        }
    }

    private static void VerifyCustomDiskProfiles(XmlSchemaSet schema)
    {
        var profile = new SetupProfile
        {
            TargetName = "Windows Vista SP2", Architecture = "x86", DiskMode = DiskMode.Custom, Firmware = Firmware.Mbr,
            SeparateBiosBoot = false, CustomWindowsMiB = 32768,
            DiskPrompt = SetupPrompt.Always, ImagePrompt = SetupPrompt.Never,
            DataPartitions = [new("Dane", "D", 8192), new("Archiwum", "E", 16384), new("Pliki", "F", 0)],
            RegisteredOwner = "Przykład", RegisteredOrganization = "Firma", Workgroup = "TEST"
        };
        profile.ConfirmWipe = true;
        var document = AnswerFile.Build(profile);
        document.Validate(schema, null);
        Assert(document.Descendants(U + "RegisteredOwner").Single().Value == "Przykład" &&
            document.Descendants(U + "RegisteredOrganization").Single().Value == "Firma" &&
            document.Descendants(U + "JoinWorkgroup").Single().Value == "TEST", "Configure ownership and the workgroup in a Vista answer file");
        Assert(document.Descendants(U + "DiskConfiguration").Single().Element(U + "WillShowUI")?.Value == "Always" &&
            document.Descendants(U + "OSImage").Single().Element(U + "WillShowUI")?.Value == "Never", "Set the native disk and image prompts independently");
        var types = document.Descendants(U + "CreatePartition").Select(partition => (string)partition.Element(U + "Type")!);
        Assert(types.SequenceEqual(["Primary", "Primary", "Extended", "Logical", "Logical", "Logical"]), "Create an extended container for more than four MBR partitions");
        Assert((bool)document.Descendants(U + "ModifyPartition").First().Element(U + "Active")! &&
            (int)document.Descendants(U + "InstallTo").Single().Element(U + "PartitionID")! == 1, "Mark C active and install to it without an auxiliary BIOS partition");
        var last = document.Descendants(U + "ModifyPartition").Last();
        Assert((int)last.Element(U + "PartitionID")! == 5 && (bool)last.Element(U + "Extend")!, "Assign data partition IDs without counting the extended container and extend the last logical partition");
        profile.DataPartitions.Reverse();
        Assert(!profile.ConfirmWipe, "A change to data partition order needs new approval");
        profile.ConfirmWipe = true;
        Throws<SetupException>(() => AnswerFile.Build(profile), "Reject remaining space before the last custom partition");
        profile.TargetName = "Windows Server 2008 R2";
        profile.Architecture = "ia64";
        profile.Firmware = Firmware.Gpt;
        profile.DataPartitions.Reverse();
        profile.ConfirmWipe = true;
        var ia64 = AnswerFile.Build(profile);
        ia64.Validate(schema, null);
        Assert(ia64.Descendants(U + "component").All(component => (string?)component.Attribute("processorArchitecture") == "ia64"),
            "Build an Itanium-specific Server 2008 R2 answer file");
    }

    private static async Task VerifyIsoDownload(string folder)
    {
        var source = new Uri("https://go.microsoft.com/fwlink/?linkid=2345730");
        var official = new Uri("https://software-static.download.prss.microsoft.com/example.iso");
        var external = new Uri("https://example.net/example.iso");
        Assert(IsoDownload.IsMicrosoftDownload(source) && IsoDownload.IsMicrosoftDownload(official) &&
            !IsoDownload.IsMicrosoftDownload(external) && !IsoDownload.IsMicrosoftDownload(new Uri("http://download.microsoft.com/example.iso")),
            "Accept HTTPS Microsoft download endpoints only");
        byte[] image = new byte[100000];
        "CD001"u8.CopyTo(image.AsSpan(16 * 2048 + 1));
        string path = Path.Combine(folder, "download-test.iso");
        using (var http = new HttpClient(new IsoFixture(request => request.RequestUri == official
            ? new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(image) }
            : new(System.Net.HttpStatusCode.Redirect) { Headers = { Location = official } })))
        {
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image));
            await IsoDownload.Save(source, path, new Progress<int>(), CancellationToken.None, http, expectedSha256: hash).ConfigureAwait(false);
            Assert(File.ReadAllBytes(path).SequenceEqual(image), "Download an ISO with a matching checksum through an official redirect");
            string untrusted = Path.Combine(folder, "wrong-hash.iso");
            try
            {
                await IsoDownload.Save(source, untrusted, new Progress<int>(), CancellationToken.None, http,
                    expectedSha256: new string('0', 64)).ConfigureAwait(false);
                throw new InvalidOperationException("Published an ISO with the wrong checksum");
            }
            catch (SetupException)
            {
                Assert(!File.Exists(untrusted) && !Directory.EnumerateFiles(folder, "*.part").Any(), "Discard an ISO with the wrong checksum");
            }
        }
        using (var http = new HttpClient(new IsoFixture(_ => new(System.Net.HttpStatusCode.Redirect) { Headers = { Location = external } })))
        {
            try
            {
                await IsoDownload.Save(source, path, new Progress<int>(), CancellationToken.None, http).ConfigureAwait(false);
                throw new InvalidOperationException("Followed an unofficial redirect");
            }
            catch (SetupException) { Assert(File.ReadAllBytes(path).SequenceEqual(image), "Reject an unofficial redirect without overwriting the saved ISO"); }
        }
        using (var http = new HttpClient(new IsoFixture(_ => new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(image[..64000])
            { Headers = { ContentLength = image.Length } } })))
        {
            try
            {
                await IsoDownload.Save(official, path, new Progress<int>(), CancellationToken.None, http).ConfigureAwait(false);
                throw new InvalidOperationException("Published a truncated ISO");
            }
            catch (SetupException) { Assert(File.ReadAllBytes(path).SequenceEqual(image) && !Directory.EnumerateFiles(folder, "*.part").Any(),
                "Preserve the previous ISO and discard temporary files after an incomplete download"); }
        }
        string portable = IsoDownload.LocalPath(folder, "download-test");
        Assert(portable == Path.Combine(folder, "download-test (2).iso"), "Keep a previous ISO alongside the portable executable");
        using (var http = new HttpClient(new IsoFixture(_ => new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(image) })))
        {
            try
            {
                await IsoDownload.Save(official, path, new Progress<int>(), CancellationToken.None, http, overwrite: false).ConfigureAwait(false);
                throw new InvalidOperationException("Replaced a portable ISO with the same name");
            }
            catch (IOException) { Assert(File.ReadAllBytes(path).SequenceEqual(image) && !Directory.EnumerateFiles(folder, "*.part").Any(),
                "Guard against a filename collision at the end of the transfer"); }
        }
        var downloaded = DownloadedIso.Snapshot(path);
        File.AppendAllText(path, "modified");
        Throws<SetupException>(downloaded.Delete, "Refuse to delete an ISO that changed after it was downloaded");
        Assert(File.Exists(path), "Keep a replacement image at the downloaded ISO path");
    }

    private static async Task VerifyProszczok(string folder)
    {
        const string html = """
            <a href="/">Home</a>
            <a href="/windows/Windows%207%20Pro%20Stycze%C5%84%202026%20x64.iso" download>Windows 7</a>
            <a HREF='Windows_8.1_x64.ISO'>Windows 8.1</a>
            <a href="Windows 8 x86.iso">Windows 8</a>
            <a href="/windows/Serwery/Windows Server 2012 R2.ISO">Server 2012 R2</a>
            <a href="/windows/Windows Server version 23H2.iso">Server 23H2</a>
            <a href="/windows/PL%20Windows%2010%2022H2%20x64%20Pa%C5%BAdziernik%202026.ISO">Polish Windows 10</a>
            <a href="/windows/hr-Windows-11-26H2-Studeni-2026.iso">Croatian Windows 11</a>
            <a href="/windows/RU_Windows_11_26H2_Noyabr_2026.iso">Russian Windows 11</a>
            <a href="/windows/zh-Hans-CN%20Windows%2011%20ARM64.iso">Chinese Windows 11</a>
            <a href="/windows/en_US_Microsoft_Hyper-V_Server_2019.iso">English Hyper-V Server</a>
            <a href="https://proszczok.pl/windows/Windows%207%20Pro%20Stycze%C5%84%202026%20x64.iso">Duplicate</a>
            <a href="/windows/Windows 110 x64.iso">Unknown</a>
            <a href="/windows/PL Windows 110 x64.iso">Unknown with language</a>
            <a href="/windows/Notes about Windows 11.iso">Unknown filename prefix</a>
            <a href="https://example.com/windows/Windows 11.iso">External</a>
            <a href="http://proszczok.pl/windows/Windows 11.iso">Insecure</a>
            <a href="/other/Windows 11.iso">Outside of catalog</a>
            <a href="/windows/Windows 11.iso.txt">Text file</a>
            <a href="/windows/Windows 11%5Cother.iso">Invalid path</a>
            """;
        using var page = new HttpClient(new IsoFixture(request =>
        {
            Assert(request.RequestUri == new Uri("https://proszczok.pl/windows/") && request.Headers.CacheControl?.NoCache == true,
                "proszczok.pl requests the current public catalog");
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };
        }));
        var files = await ProszczokIsoCatalog.Load(CancellationToken.None, page);
        Assert(files.Select(file => file.Target.Name).SequenceEqual(["Windows 7", "Windows 8.1", "Windows 8",
            "Windows Server 2012 R2", "Windows Server, version 23H2", "Windows 10", "Windows 11", "Windows 11", "Windows 11",
            "Microsoft Hyper-V Server 2019"]), "proszczok.pl exact system matching with language prefixes and trusted links");
        Assert(files[0].FileName == "Windows 7 Pro Styczeń 2026 x64.iso", "proszczok.pl displays the decoded file name");
        Assert(files[5].FileName == "PL Windows 10 22H2 x64 Październik 2026.ISO" &&
            files[6].FileName.StartsWith("hr-", StringComparison.Ordinal) && files[7].FileName.StartsWith("RU_", StringComparison.Ordinal),
            "proszczok.pl keeps language prefixes in the selectable file names");
        Assert(ProszczokIsoCatalog.SupportsArchitecture(files[5], "amd64") &&
            ProszczokIsoCatalog.SupportsArchitecture(files[6], "amd64") &&
            !ProszczokIsoCatalog.SupportsArchitecture(files[8], "amd64") &&
            ProszczokIsoCatalog.SupportsArchitecture(files[8], "arm64"), "proszczok.pl still checks the architecture");

        var image = new byte[65536];
        "CD001"u8.CopyTo(image.AsSpan(16 * 2048 + 1));
        var fileUrl = files[0].Uri;
        string path = Path.Combine(folder, "proszczok.iso");
        using var http = new HttpClient(new IsoFixture(request => request.RequestUri == fileUrl
            ? new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(image) }
            : throw new InvalidOperationException("Unexpected ISO download URL.")));
        await IsoDownload.Save(fileUrl, path, new Progress<int>(), CancellationToken.None, http, source: IsoSource.Proszczok);
        Assert(File.ReadAllBytes(path).SequenceEqual(image), "proszczok.pl ISO saved through the common downloader");
        using var redirect = new HttpClient(new IsoFixture(request => request.RequestUri == fileUrl
            ? new(System.Net.HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://download.microsoft.com/Windows.iso") } }
            : throw new InvalidOperationException("The proszczok.pl download followed an external redirect.")));
        try
        {
            await IsoDownload.Save(fileUrl, path, new Progress<int>(), CancellationToken.None, redirect, source: IsoSource.Proszczok);
            throw new InvalidOperationException("The proszczok.pl download accepted an external redirect.");
        }
        catch (SetupException)
        {
            Assert(File.ReadAllBytes(path).SequenceEqual(image), "proszczok.pl rejects redirects to other sources and leaves the existing image");
        }
    }

    private static async Task VerifyLiveProszczokCatalog()
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        var files = await ProszczokIsoCatalog.Load(CancellationToken.None);
        if (files.Length == 0)
            throw new InvalidOperationException("The live catalog contained no recognized ISO images.");
        await Parallel.ForEachAsync(files, async (file, cancel) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, file.Uri);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 65535);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(cancel);
            byte[] bytes = new byte[65536];
            await stream.ReadExactlyAsync(bytes, cancel);
            if (!IsoDownload.HasIsoSignature(bytes))
                throw new InvalidDataException("Invalid ISO signature for " + file.FileName);
            Console.WriteLine($"{file.Target.Name} | {file.FileName} | HTTP {(int)response.StatusCode} | valid ISO signature");
        });
    }

    private sealed class IsoFixture(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(reply(request));
    }

    private static void VerifyUsb(string folder)
    {
        var profile = new SetupProfile { TargetName = "Windows 10" };
        Assert(UsbMedia.RequiredFirmware(profile) == "Gpt", "Carry the UEFI mode through to USB with an interactive Windows 10 layout");
        profile.DiskMode = DiskMode.SystemAndWindows;
        profile.Firmware = Firmware.Mbr;
        Assert(UsbMedia.RequiredFirmware(profile) == "Mbr", "Match the fixed BIOS layout in the XML");
        profile.Advanced["PE"] = "Generated";
        profile.Advanced["DetectFirmware"] = "1";
        Assert(UsbMedia.RequiredFirmware(profile) == "Any", "Allow both USB modes with WinPE firmware detection");
        Assert(UsbMedia.RequiredFirmware(new SetupProfile()) == "Gpt", "Use UEFI for standard Windows 11 media");

        static string Resource(System.Reflection.Assembly assembly, string name)
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            return reader.ReadToEnd();
        }
        string storage = Resource(typeof(UsbMedia).Assembly, "XMLator.UsbMedia.ps1");
        string fixture = Resource(typeof(Checks).Assembly, "XMLator.Checks.UsbStorageFixture.ps1");
        foreach (var (test, firmware, command, expected) in new[]
        {
            ("cancel", "Gpt", "", ""),
            ("changed", "Gpt", "WRITE", ""),
            ("on-target", "Gpt", "WRITE", ""),
            ("missing-efi", "Gpt", "WRITE", ""),
            ("conflicting-extra", "Gpt", "WRITE", ""),
            ("corrupt-extra", "Gpt", "WRITE", "clear,initialize-GPT,format-FAT32"),
            ("gpt", "Gpt", "WRITE", "clear,initialize-GPT,format-FAT32,type-{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}"),
            ("gpt-dism", "Gpt", "WRITE", "clear,initialize-GPT,format-FAT32,type-{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}"),
            ("mbr", "Mbr", "WRITE", "clear,initialize-MBR,format-NTFS,bootsect"),
            ("auto-mbr", "Gpt", "WRITE", "clear,convert-GPT,format-FAT32,type-{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}"),
            ("empty-mbr", "Mbr", "WRITE", "format-NTFS,bootsect")
        })
        {
            string work = Directory.CreateDirectory(Path.Combine(folder, test)).FullName;
            string isoTree = Directory.CreateDirectory(Path.Combine(work, "iso")).FullName;
            string target = Directory.CreateDirectory(Path.Combine(work, "usb")).FullName;
            string overlay = Directory.CreateDirectory(Path.Combine(work, "overlay")).FullName;
            string Write(string root, string name, string contents)
            {
                string path = Path.Combine(root, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, contents);
                return path;
            }
            Write(isoTree, "sources\\install.wim", "mock install image");
            File.WriteAllBytes(Write(isoTree, "sources\\boot.wim", "mock PE image"), new byte[3 * 1024 * 1024]);
            Write(isoTree, "setup.exe", "mock Setup");
            Write(isoTree, "bootmgr", "mock BIOS loader");
            Write(isoTree, "boot\\bcd", "mock BIOS configuration");
            if (test != "missing-efi")
                Write(isoTree, "efi\\boot\\bootx64.efi", "mock UEFI loader");
            Write(isoTree, AnswerFile.DefaultName, "old answer");
            string payload = Path.Combine(work, "payload");
            string winPe = Path.GetDirectoryName(Write(payload, "pe\\storage.inf", "WinPE driver"))!;
            string installed = Path.GetDirectoryName(Write(payload, "system\\device.inf", "Installed Windows driver"))!;
            string files = Path.GetDirectoryName(Write(payload, "root\\example.txt", "OEM file"))!;
            var usbProfile = new SetupProfile { AcceptEula = true, KeyMode = KeyMode.Skip, WinPeDrivers = winPe, InstalledDrivers = installed, SystemFiles = files };
            usbProfile.Components.Add(new CustomComponent("specialize", "Microsoft-Windows-Shell-Setup", "<RegisteredOwner>OverlayFixture</RegisteredOwner>"));
            usbProfile.EmbeddedScripts.Add(new EmbeddedScript("Write-Output 'Embedded fixture'", "System", "Ps1"));
            foreach (var phase in Enum.GetValues<OemPhase>())
                usbProfile.OemFiles.Add(new OemFile(Write(payload, $"{phase}.cmd", "@echo off\r\nrem " + phase), phase));
            if (test == "gpt-dism")
            {
                usbProfile.Advanced["PE"] = "Generated";
                usbProfile.WinPePackages = Path.GetDirectoryName(Write(payload, "packages\\test.cab", "Offline Windows package"))!;
            }
            overlay = MediaPackage.StageUsb(overlay, AnswerFile.Build(usbProfile), usbProfile);
            if (test == "conflicting-extra")
                Write(isoTree, "sources\\$OEM$\\$1\\example.txt", "Original OEM file with different content");
            string xml = Write(work, "customer.xml", "separate saved XML");
            string iso = Write(work, "installation.iso", "synthetic ISO metadata");
            Write(work, "bootsect.exe", "alias fixture only");
            var options = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command", "& ([ScriptBlock]::Create($env:XMLATOR_USB_TEST))" })
                options.ArgumentList.Add(arg);
            options.Environment["XMLATOR_USB_TEST"] = fixture;
            options.Environment["XMLATOR_USB_SCRIPT"] = storage;
            options.Environment["XMLATOR_USB_OPERATION"] = "write";
            options.Environment["XMLATOR_USB_CASE"] = test;
            options.Environment["XMLATOR_USB_FIXTURE"] = work;
            options.Environment["XMLATOR_USB_SYSTEM"] = work;
            options.Environment["XMLATOR_USB_DISK"] = """{"Number":71,"Size":8589934592,"Path":"test-usb","UniqueId":"test-id"}""";
            options.Environment["XMLATOR_USB_FIRMWARE"] = firmware;
            options.Environment["XMLATOR_USB_ISO"] = iso;
            options.Environment["XMLATOR_USB_MEDIA"] = isoTree;
            options.Environment["XMLATOR_USB_LABEL"] = "11_26H2_10_2026";
            options.Environment["XMLATOR_USB_OVERLAY"] = overlay;
            options.Environment["XMLATOR_USB_SAVED_XML"] = xml;
            options.Environment["XMLATOR_USB_EXE"] = Application.ExecutablePath;
            options.Environment["XMLATOR_USB_EFI"] = "x64";
            using var process = System.Diagnostics.Process.Start(options)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (command.Length > 0)
                process.StandardInput.WriteLine(command);
            process.StandardInput.Close();
            if (!process.WaitForExit(30000))
            {
                process.Kill();
                throw new TimeoutException("USB fixture timed out: " + test);
            }
            var reply = output.GetAwaiter().GetResult().Trim();
            var error = errors.GetAwaiter().GetResult();
            string traceFile = Path.Combine(work, "trace.txt");
            string trace = File.Exists(traceFile) ? string.Join(',', File.ReadAllLines(traceFile)) : "";
            Assert(trace == expected, $"USB fixture {test}: {trace}\n{error}");
            var lines = reply.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (test is "gpt" or "gpt-dism" or "mbr" or "auto-mbr" or "empty-mbr")
            {
                Assert(process.ExitCode == 0 && lines.Where(line => !UsbProgress.TryParse(line, out _)).SequenceEqual(["READY", "DONE"]),
                    "Write the USB with the full media package: " + test);
                foreach (string extra in Directory.EnumerateFiles(overlay, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(overlay, extra);
                    Assert(File.ReadAllBytes(Path.Combine(target, relative)).SequenceEqual(File.ReadAllBytes(extra)), $"Read back USB {test}: {relative}");
                }
                string oem = test == "gpt-dism" ? "$OEM$" : @"sources\$OEM$";
                Assert(File.Exists(Path.Combine(target, "$WinPEDriver$", "storage.inf")) &&
                    File.Exists(Path.Combine(target, oem, "$1", "XMLator", "Drivers", "device.inf")) &&
                    File.Exists(Path.Combine(target, oem, "$$", "Setup", "Scripts", "SetupComplete.cmd")),
                    "Put PE drivers, Windows drivers and scripts in their documented paths: " + test);
                Assert((test == "gpt-dism") == File.Exists(Path.Combine(target, "$WinPEPackage$", "test.cab")), "Keep the optional DISM packages on USB");
                var updates = lines.Where(line => UsbProgress.TryParse(line, out _)).Select(line => { UsbProgress.TryParse(line, out var update); return update; }).ToArray();
                var copyPercents = updates.Where(update => update.Stage == UsbStage.Copy).Select(update => update.Percent!.Value).ToArray();
                Assert(updates[0].Stage == UsbStage.Checking && updates[^1].Stage == UsbStage.Finalizing && copyPercents is [0, _, .., 100] &&
                    copyPercents.SequenceEqual(copyPercents.Distinct().Order()) && updates.Any(update => update.Stage == UsbStage.Verify),
                    "Report numeric progress within large files and send Verification before DONE: " + test);
                string label = File.ReadAllText(Path.Combine(work, "volume-label.txt")).Trim();
                Assert(label == (firmware == "Gpt" ? "11_26H2_10_" : "11_26H2_10_2026") && File.ReadAllText(iso) == "synthetic ISO metadata" &&
                    File.ReadAllBytes(Path.Combine(target, "sources", "boot.wim")).SequenceEqual(File.ReadAllBytes(Path.Combine(isoTree, "sources", "boot.wim"))),
                    "Use the ISO volume label and copy all bytes: " + test);
            }
            else if (test == "corrupt-extra")
                Assert(process.ExitCode != 0 && !lines.Contains("DONE") && error.Contains("USB copy failed verification"),
                    "Fail when a copied extra is different from the source");
            else
                Assert(Directory.EnumerateFileSystemEntries(target).Any() == false && (process.ExitCode == 0) == (test == "cancel"),
                    $"Leave the USB fixture untouched after {test}: {error}");
        }
    }

    private static IEnumerable<Control> Descendants(Control owner) =>
        owner.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));

    private static void Assert(bool result, string name)
    {
        if (!result)
            throw new InvalidOperationException($"Check failed: {name}");
        passed++;
    }

    private static void AssertThrows<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { Assert(true, name); return; }
        Assert(false, name);
    }

    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { passed++; return; }
        throw new InvalidOperationException($"Check failed: {name}");
    }
}
