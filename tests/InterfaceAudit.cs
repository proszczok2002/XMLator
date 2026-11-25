using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using XMLator;

internal static class InterfaceAudit
{
    private const BindingFlags Members = BindingFlags.NonPublic | BindingFlags.Instance;

    internal static void Run(string? languages = null, AppTheme? theme = null)
    {
        var folder = Directory.CreateTempSubdirectory("XMLator-interface-");
        try
        {
            CheckSettings(folder.FullName);
            CheckCatalogs();
            CheckForms(folder.FullName, languages, theme);
        }
        finally { folder.Delete(recursive: true); }
    }

    private static void CheckSettings(string folder)
    {
        string path = Path.Combine(folder, "config.ini");
        var initial = new InterfacePreferences(path, CultureInfo.GetCultureInfo("pl-PL"));
        Require(initial.Language == "auto" && initial.EffectiveLanguage == "pl" && initial.Theme == AppTheme.System &&
            File.Exists(path), "Default config.ini follows the Windows UI language and theme.");
        var now = DateTimeOffset.UtcNow;
        Require(initial is { AutomaticUpdates: true, UpdateIntervalDays: 0 } && initial.ShouldCheckForUpdates(now),
            "A new config enables update checks at every startup.");
        initial.LastUpdateCheck = now;
        foreach (var (culture, language) in new (string, string)[]
        {
            ("pl-PL", "pl"), ("de-AT", "de"), ("en-GB", "en"), ("ru-RU", "ru"), ("hr-HR", "hr"),
            ("fr-CA", "fr"), ("es-MX", "es"), ("zh-CN", "zh-Hans"), ("zh-TW", "zh-Hans"), ("ja-JP", "en"), ("it-IT", "en")
        })
            Require(new InterfacePreferences(path, CultureInfo.GetCultureInfo(culture)).EffectiveLanguage == language, $"Windows {culture} → {language}");
        initial.Language = "en";
        initial.Theme = AppTheme.Light;
        initial.Save();
        var restarted = new InterfacePreferences(path, CultureInfo.GetCultureInfo("pl-PL"));
        Require(restarted.Language == "en" && restarted.Theme == AppTheme.Light, "Explicit settings survive restart on Polish Windows.");
        Require(restarted.LastUpdateCheck == now && restarted.ShouldCheckForUpdates(now.AddSeconds(1)),
            "The previous check does not postpone the next startup check.");
        restarted.Language = "zh-Hans";
        restarted.Theme = AppTheme.Dark;
        restarted.Save();
        restarted = new InterfacePreferences(path, CultureInfo.GetCultureInfo("fr-FR"));
        Require(restarted.EffectiveLanguage == "zh-Hans" && restarted.Theme == AppTheme.Dark, "Unicode language and dark theme survive restart.");
        File.WriteAllText(path, "[Interface]\nLanguage=not-a-language\nTheme=37\n");
        var recovered = new InterfacePreferences(path, CultureInfo.GetCultureInfo("es-ES"));
        Require(recovered.Language == "auto" && recovered.EffectiveLanguage == "es" && recovered.Theme == AppTheme.System,
            "Invalid entries fall back independently.");
        Require(recovered is { AutomaticUpdates: true, UpdateIntervalDays: 0 },
            "A config without an update schedule defaults to checking at every startup.");
        Require(!Directory.GetFiles(folder, "*.tmp").Any(), "Atomic config saves leave no temporary file.");
        Console.WriteLine("Config creation, Windows-language defaults, fallback, explicit choices and restart passed.");
    }

    internal static void CheckCatalogs()
    {
        HashSet<string>? referenceKeys = null;
        var engine = ModernOptions.Engine;
        string[] layoutNames = engine.KeyboardIdentifiers.Values.Select(item => item.DisplayName).ToArray();
        string[] dynamicNames = [.. layoutNames, .. engine.TimeOffsets.Values.Select(item => item.DisplayName),
            .. engine.Bloatwares.Values.Select(item => item.DisplayName)];
        foreach (var choice in UiText.Languages.Where(choice => choice.Value != "en"))
        {
            using var source = typeof(UiText).Assembly.GetManifestResourceStream($"XMLator.Translations.{choice.Value}.json");
            Require(source is not null, choice.Label + " embedded catalog.");
            var phrases = JsonSerializer.Deserialize<Dictionary<string, string>>(source!)!;
            Require(dynamicNames.All(phrases.ContainsKey), choice.Label + " includes all time zones, keyboard layouts and removable apps.");
            Require(layoutNames.Select(name => phrases[name]).Distinct(StringComparer.OrdinalIgnoreCase).Count() == layoutNames.Length,
                choice.Label + " does not give two different keyboard layouts the same name.");
            if (choice.Value == "pl")
                continue;
            Require(phrases.Count >= 1100, choice.Label + " catalog includes the wizard, dialogs, help and diagnostics.");
            referenceKeys ??= new HashSet<string>(phrases.Keys, StringComparer.Ordinal);
            Require(referenceKeys.SetEquals(phrases.Keys), choice.Label + " has the same source keys as the other catalogs.");
            foreach (var pair in phrases)
            {
                Require(!string.IsNullOrWhiteSpace(pair.Value), $"Missing {choice.Value} text for {pair.Key}");
                Require(choice.Value == "ru" || !Regex.IsMatch(pair.Value, @"[\u0400-\u04ff]"),
                    $"Unexpected Cyrillic in the {choice.Value} translation of {pair.Key}");
                Require(choice.Value == "zh-Hans" || !Regex.IsMatch(pair.Value, @"[\u4e00-\u9fff]"),
                    $"Unexpected Han characters in the {choice.Value} translation of {pair.Key}");
                if (pair.Key.Contains("|*.", StringComparison.Ordinal))
                    Require(pair.Key.Split('|').Where((_, index) => index % 2 == 1).SequenceEqual(
                        pair.Value.Split('|').Where((_, index) => index % 2 == 1)), $"Preserve file patterns in {choice.Value} for {pair.Key}");
                foreach (Match path in Regex.Matches(pair.Key, @"[A-Za-z0-9_$-]+(?:\\[A-Za-z0-9_.$-]+)+"))
                    Require(pair.Value.Contains(path.Value.TrimEnd('.'), StringComparison.OrdinalIgnoreCase),
                        $"Preserve the literal path {path.Value} in {choice.Value} for {pair.Key}");
                if (pair.Key.Contains("MiB", StringComparison.Ordinal))
                    Require(Regex.IsMatch(pair.Value, @"(?:MiB|Mio|МиБ)(?![a-z])", RegexOptions.IgnoreCase),
                        $"Preserve the mebibyte unit in {choice.Value} for {pair.Key}");
                if (pair.Key.Contains("GiB", StringComparison.Ordinal))
                    Require(Regex.IsMatch(pair.Value, @"(?:GiB|Gio|ГиБ)(?![a-z])", RegexOptions.IgnoreCase),
                        $"Preserve the gibibyte unit in {choice.Value} for {pair.Key}");
                if (!Regex.IsMatch(pair.Key, @"\{\d"))
                    continue;
                var sourceFormat = System.Text.CompositeFormat.Parse(pair.Key);
                var translated = System.Text.CompositeFormat.Parse(pair.Value);
                Require(sourceFormat.MinimumArgumentCount == translated.MinimumArgumentCount,
                    $"Placeholder count in {choice.Value} for {pair.Key}");
                var arguments = Enumerable.Range(0, sourceFormat.MinimumArgumentCount).Select(i => (object)(i + 12345)).ToArray();
                string formatted = string.Format(CultureInfo.InvariantCulture, translated, arguments);
                Require(arguments.All(value => formatted.Contains(value.ToString()!)), $"Preserve each value in {choice.Value} for {pair.Key}");
            }
            var error = SetupException.Format($"Partycja EFI musi mieć co najmniej {100} MiB. Dla dysków 4Kn użyj co najmniej 300 MiB.",
                $"The EFI partition must be at least {100} MiB. For 4Kn drives, use at least 300 MiB.");
            Require(error.Translate(choice.Value) != error.Message && error.Translate(choice.Value).Contains("100"), choice.Label + " formatted error.");
            var diagnostic = JsonSerializer.Deserialize<Dictionary<string, string>>(UiText.UsbDiagnostics(choice.Value))!;
            Require(diagnostic.Count >= 25 && diagnostic.Values.All(value => value.Length > 0), choice.Label + " USB diagnostic catalog.");
            Console.WriteLine($"{choice.Label}: {phrases.Count} entries and all composite-format placeholders verified.");
        }
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
            Require(IcuLocaleNames.Country("DE", "fr") == "Allemagne" &&
                LocaleCatalog.Languages("de").Single(choice => choice.Value == "pl-PL").Label.Contains("Polnisch", StringComparison.Ordinal),
                "Windows ICU returns names in the application's selected language.");
        CheckTimeZones();
        foreach (var choice in UiText.Languages)
        {
            var layouts = LocaleCatalog.Keyboards(choice.Value).ToDictionary(item => item.Value, item => item.Label, StringComparer.OrdinalIgnoreCase);
            Require(layouts["0404:00000404"] != layouts["0804:00000804"], choice.Label + " distinguishes Traditional Chinese and Simplified Chinese US layouts.");
            Require(layouts["041f:0001041f"] != layouts["041f:0000041f"], choice.Label + " distinguishes the Turkish F and Q layouts.");
        }
    }

    private static void CheckTimeZones()
    {
        var offsets = TimeZoneInfo.GetSystemTimeZones().ToDictionary(zone => zone.Id, zone => zone.BaseUtcOffset);
        foreach (var choice in UiText.Languages)
        {
            var zones = LocaleCatalog.TimeZones(choice.Value);
            Require(zones.Length == offsets.Count && zones.Select(zone => zone.Value).Distinct().Count() == zones.Length,
                choice.Label + " keeps one choice for each Windows time zone identifier.");
            foreach (var zone in zones)
            {
                var offset = offsets[zone.Value];
                string utc = offset == TimeSpan.Zero ? "(UTC)" :
                    FormattableString.Invariant($"(UTC{(offset < TimeSpan.Zero ? '-' : '+')}{offset.Duration():hh\\:mm})");
                Require(zone.Label.StartsWith(utc, StringComparison.Ordinal), $"{choice.Label}: use the Windows UTC offset for {zone.Value}.");
            }
        }
        foreach (var (language, city1, city2) in new (string, string, string)[]
        {
            ("en", "Belgrade", "Bratislava"), ("pl", "Belgrad", "Bratysława"), ("de", "Belgrad", "Bratislava"),
            ("fr", "Belgrade", "Bratislava"), ("es", "Belgrado", "Bratislava"), ("hr", "Beograd", "Bratislava"),
            ("ru", "Белград", "Братислава"), ("zh-Hans", "贝尔格莱德", "布拉迪斯拉发")
        })
        {
            string name = LocaleCatalog.TimeZones(language).Single(zone => zone.Value == "Central Europe Standard Time").Label;
            Require(name.Contains(city1, StringComparison.Ordinal) && name.Contains(city2, StringComparison.Ordinal),
                $"{language}: retain both Belgrade and Bratislava in the Central Europe time zone.");
        }
    }

    private static void CheckForms(string folder, string? languages, AppTheme? requestedTheme)
    {
        string config = Path.Combine(folder, "config.ini");
        File.Delete(config);
        var preferences = new InterfacePreferences(config, CultureInfo.GetCultureInfo("pl-PL"));
        string legacy = Path.Combine(folder, "legacy.ini");
        File.WriteAllText(legacy, "[XMLator]\nVersion=1\n\n[Setup]\nInterfaceLanguage=\"pl\"\nInterfaceTheme=\"Dark\"\nTargetName=\"Windows 10\"\n");
        using var wizard = new Wizard(null, preferences)
        {
            FindMicrosoftIsos = (_, _, _, _, _, _) => Task.FromResult(Array.Empty<IsoProduct>()),
            FindSiteIsos = (_, _, _) => Task.FromResult(Array.Empty<ProszczokIso>())
        };
        var type = typeof(Wizard);
        type.GetField("usbDisks", Members)!.SetValue(wizard, Array.Empty<UsbDisk>());
        var step = type.GetField("step", Members)!;
        var show = type.GetMethod("ShowStep", Members)!;
        var change = type.GetMethod("ChangeLanguage", Members)!;
        var theme = type.GetMethod("ChangeTheme", Members)!;
        var usbDisks = type.GetField("usbDisks", Members)!;
        var usbPath = type.GetField("usbIsoPath", Members)!;
        var sampleDisk = new UsbDisk(999, 32L << 30, "XMLator test fixture", "XMLator test ID", "Test USB", "");
        using var temporaryIso = new TemporaryIso();
        wizard.Show();
        wizard.Size = wizard.MinimumSize;
        var headings = new HashSet<string>();
        var profile = (SetupProfile)type.GetField("profile", Members)!.GetValue(wizard)!;
        profile.CreateUsers = true;
        profile.Users.Add(new LocalUser { Name = "ThemeTest" });
        profile.Logon = LogonMode.FirstAdministrator;
        int reviewCombinations = 0;
        var requested = languages?.Split(',');
        foreach (var choice in UiText.Languages.Where(choice => requested is null || requested.Contains(choice.Value)))
        {
            step.SetValue(wizard, 0);
            change.Invoke(wizard, [choice.Value]);
            show.Invoke(wizard, [true]);
            string settings = choice.Value == "pl" ? "Ustawienia…" : UiText.Get(choice.Value, "&Settings…").Replace("&", "");
            string target = choice.Value == "pl" ? "Który Windows chcesz zainstalować?" :
                UiText.Get(choice.Value, "&Which version of Windows do you want to install?").Replace("&", "");
            Require(Descendants(wizard).OfType<ComboBox>().Any(box => box.AccessibleName == target && box.Focused),
                $"{choice.Label}: focus starts on the Windows version list.");
            Require(headings.Add(wizard.Text), $"{choice.Label} has unique step-one text.");
            string keyboardHelp = (string)type.GetMethod("KeyboardHelp", Members)!.Invoke(wizard, null)!;
            foreach (string caption in choice.Value == "pl" ? new[] { "&Wstecz", "&Dalej" }
                     : new[] { UiText.Get(choice.Value, "&Back"), UiText.Get(choice.Value, "&Next") })
            {
                char key = caption.Select(char.ToUpperInvariant).First(letter => Control.IsMnemonic(letter, caption));
                Require(keyboardHelp.Contains($"Alt+{key}", StringComparison.Ordinal), $"{choice.Label}: help matches navigation shortcut Alt+{key}.");
            }
            foreach (AppTheme mode in Enum.GetValues<AppTheme>().Where(mode => requestedTheme is null || mode == requestedTheme))
            {
                theme.Invoke(wizard, [mode]);
                for (int i = 0; i < 9; i++)
                {
                    step.SetValue(wizard, i);
                    show.Invoke(wizard, [true]);
                    Application.DoEvents();
                    var unnamed = Descendants(wizard).Where(control => control.Visible &&
                        control is ComboBox or TextBox or Button or CheckBox or ListBox &&
                        string.IsNullOrWhiteSpace(control.AccessibleName)).Select(control => $"{control.GetType().Name} '{control.Text}'").ToArray();
                    Require(unnamed.Length == 0 && wizard.ActiveControl is not null,
                        $"{choice.Label}/{mode} screen {i + 1}: named native controls and active control. Unnamed: {string.Join(", ", unnamed)}.");
                    var navigation = (FlowLayoutPanel)type.GetField("navigation", Members)!.GetValue(wizard)!;
                    Require(navigation.Controls.Cast<Control>().All(button => navigation.ClientRectangle.Contains(button.Bounds)),
                        $"{choice.Label}/{mode} screen {i + 1}: all buttons fit at the minimum window size.");
                    Require(Descendants(wizard).OfType<Button>().Count(button => button.Visible && button.AccessibleName == settings)
                        == (i == 0 ? 1 : 0), $"{choice.Label}/{mode}: Settings is available only on the first screen.");
                    if (i == 5)
                    {
                        var signIn = Descendants(wizard).OfType<ComboBox>().Single(combo =>
                            combo.SelectedItem is Choice<LogonMode>);
                        Require(signIn.Visible && signIn.Items.Count == 2 && signIn.SelectedItem is Choice<LogonMode> selected &&
                            selected.Value == LogonMode.FirstAdministrator, $"{choice.Label}/{mode}: preserve automatic sign-in with a blank password.");
                    }
                    if (i == 8)
                    {
                        string account = profile.Users[0].Name;
                        string expected = choice.Value == "pl" ? $"Jednorazowe logowanie — {account}." :
                            UiText.Format(choice.Value, $"One-time sign-in — {account}.");
                        Require(Descendants(wizard).OfType<ListBox>().Any(list => list.Items.Contains(expected)),
                            $"{choice.Label}/{mode}: review names the selected local administrator.");
                        string restart = choice.Value == "pl" ? "Windows 11 może sam zalogować jedyne konto bez hasła także po restarcie." :
                            UiText.Get(choice.Value, "Windows 11 may also sign in to the only account without a password after a restart.");
                        Require(Descendants(wizard).OfType<ListBox>().Any(list => list.Items.Contains(restart)),
                            $"{choice.Label}/{mode}: review explains Windows 11's later startup behavior for the only account without a password.");
                        CheckUsbSelection(choice.Value, mode);
                    }
                }
                Console.WriteLine($"{choice.Label}/{mode}: 9 screens with named controls and native keyboard focus.");
            }
        }
        change.Invoke(wizard, ["en"]);
        theme.Invoke(wizard, [AppTheme.Light]);
        type.GetMethod("LoadProfile", Members)!.Invoke(wizard, [legacy]);
        var legacyProfile = (SetupProfile)type.GetField("profile", Members)!.GetValue(wizard)!;
        Require(legacyProfile.TargetName == "Windows 10" && preferences.Language == "en" && preferences.Theme == AppTheme.Light,
            "Opening a legacy installation profile leaves the application preferences in config.ini.");
        string profilePath = Path.Combine(folder, "new.ini");
        IniProfile.Save(profilePath, legacyProfile);
        Require(!File.ReadAllText(profilePath).Contains("InterfaceLanguage") && !File.ReadAllText(profilePath).Contains("InterfaceTheme"),
            "New installation profiles do not duplicate application preferences.");

        Console.WriteLine($"{reviewCombinations} Windows target, architecture, edition, interface language and theme combinations verified on the final screen.");

        void CheckUsbSelection(string language, AppTheme mode)
        {
            var previousTarget = profile.TargetName;
            var previousArchitecture = profile.Architecture;
            var previousImageMode = profile.ImageMode;
            var previousEdition = profile.PresetEdition;
            var targetField = type.GetField("usbTarget", Members)!;
            usbDisks.SetValue(wizard, new[] { sampleDisk });
            targetField.SetValue(wizard, null);
            string filesName = language == "pl" ? "Zapis plików" : UiText.Get(language, "Save files");
            string usbName = language == "pl" ? "Pendrive instalacyjny" : UiText.Get(language, "Installation USB drive");
            string createName = language == "pl" ? "Utwórz bootowalny pendrive" :
                UiText.Get(language, "&Create a bootable USB drive").Replace("&", "");

            foreach (var target in Target.All)
            foreach (string arch in target.Architectures)
            foreach (var edition in EditionCatalog.For(target, arch))
            {
                profile.TargetName = target.Name;
                profile.Architecture = arch;
                profile.ImageMode = ImageMode.Edition;
                profile.PresetEdition = edition.Id;
                show.Invoke(wizard, [false]);
                var groups = Descendants(wizard).OfType<GroupBox>().ToArray();
                var files = groups.Single(group => group.AccessibleName == filesName);
                var usb = groups.Single(group => group.AccessibleName == usbName);
                var drive = Descendants(usb).OfType<ComboBox>().Single(list => list.SelectedItem is Choice<UsbDisk?>);
                var iso = Descendants(usb).OfType<TextBox>().Single();
                var create = Descendants(usb).OfType<Button>().Single(button => button.AccessibleName == createName);
                var firmware = Descendants(usb).OfType<ComboBox>().SingleOrDefault(list => list.SelectedItem is Choice<Firmware>);
                var saveButtons = Descendants(files).OfType<Button>().ToArray();
                string variant = $"{target.Name}/{arch}/{edition.Label} in {language}/{mode}";
                Require(files.Bottom <= usb.Top && files.TabIndex < usb.TabIndex && saveButtons.Length == 2 &&
                    saveButtons.All(button => button.Enabled && button.Visible && button.Right <= button.Parent!.ClientSize.Width) &&
                    create.AccessibilityObject.Role == AccessibleRole.PushButton && create.Visible &&
                    create.Right <= create.Parent!.ClientSize.Width && iso.Visible && drive.TabIndex < iso.Parent!.TabIndex &&
                    iso.Parent.TabIndex < create.TabIndex && !create.Enabled && wizard.AcceptButton is null,
                    $"File and USB groups, keyboard order and readiness for {variant}.");
                Require(firmware is null, $"USB inherits the installer boot mode for {variant}.");
                drive.SelectedIndex = 1;
                Require(!create.Enabled, $"A drive alone does not enable USB creation for {variant}.");
                drive.SelectedIndex = 0;
                reviewCombinations++;
            }

            var readyControls = Descendants(wizard).ToArray();
            var selectDrive = readyControls.OfType<ComboBox>().Single(list => list.SelectedItem is Choice<UsbDisk?>);
            var selectIso = readyControls.OfType<TextBox>().Single();
            var prepare = readyControls.OfType<Button>().Single(button => button.AccessibleName == createName);
            selectIso.Text = temporaryIso.Path;
            Require(!prepare.Enabled, $"ISO alone is insufficient in {language}/{mode}.");
            selectDrive.SelectedIndex = 1;
            Require(TemporaryIso.PumpUntil(() => prepare.Enabled), $"Enable creation with both inputs in {language}/{mode}.");
            selectIso.Clear();
            Require(!prepare.Enabled, $"Clearing the ISO disables creation in {language}/{mode}.");
            usbPath.SetValue(wizard, "");
            targetField.SetValue(wizard, null);
            profile.TargetName = previousTarget;
            profile.Architecture = previousArchitecture;
            profile.ImageMode = previousImageMode;
            profile.PresetEdition = previousEdition;
        }
    }

    private static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>()
        .SelectMany(child => new[] { child }.Concat(Descendants(child)));

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

internal sealed class TemporaryIso : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("XMLator-iso-selection-");
    internal string Path { get; }

    internal TemporaryIso()
    {
        Path = System.IO.Path.Combine(directory.FullName, "fixture.iso");
        File.WriteAllBytes(Path, []);
    }

    public void Dispose() => directory.Delete(recursive: true);

    internal static bool PumpUntil(Func<bool> ready) => SpinWait.SpinUntil(() =>
    {
        Application.DoEvents();
        return ready();
    }, 4000);
}
