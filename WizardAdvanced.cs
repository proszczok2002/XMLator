using Schneegans.Unattend;

namespace XMLator;

internal sealed partial class Wizard
{
    private void AdvancedPage(Fields f)
    {
        if (profile.Target.ModernClient)
        {
            CategoryButton(f, L("&Eksplorator, Start i pasek zadań…", "&Explorer, Start and taskbar…"), ExplorerOptions);
            CategoryButton(f, L("&System, zabezpieczenia i aktualizacje…", "&System, security and updates…"), SystemOptions);
            CategoryButton(f, L("&Sieć Wi-Fi i maszyny wirtualne…", "&Wi-Fi and virtual machines…"), NetworkOptions);
            CategoryButton(f, L("&Personalizacja i ułatwienia dostępu…", "&Personalization and accessibility…"), AppearanceOptions);
            CategoryButton(f, L("Zasady &haseł i blokady konta…", "Account &password and lockout policies…"), AccountOptions);
            CategoryButton(f, L("&Aplikacje i funkcje do usunięcia…", "&Apps and features to remove…"), RemovalOptions);
        }
        else
            f.Note(L("Dodatkowe ustawienia dla tego systemu można podać w składnikach XML. Skrypty jako osobne pliki dodasz na stronie OEM.",
                "For this system, add extra settings in XML components. Add scripts as separate files on the OEM page."));
        f.Button(L("Własne skła&dniki XML…", "&Custom XML components…"), () =>
        {
            var fields = new Fields();
            var items = profile.Components.ToList();
            EditableList(fields, L("Składniki i przebiegi", "Components and passes"), items,
                item => $"{item.Pass} — {item.Name}", ComponentEditor);
            if (Ask(L("Własne składniki XML", "Custom XML components"), fields))
                profile.Components = items;
        });
    }

    private void CategoryButton(Fields f, string name, Action<Fields> build, Action? onAccept = null) => f.Button(name, () =>
    {
        var before = new Dictionary<string, string>(profile.Advanced);
        var secrets = new Dictionary<string, string>(profile.SessionOptions);
        AppRemoval apps = profile.RemoveApps;
        bool darkWindows = profile.DarkInstalledWindows;
        var fields = new Fields();
        build(fields);
        if (Ask(name.Replace("&", "").TrimEnd('…'), fields))
        {
            onAccept?.Invoke();
            return;
        }
        profile.Advanced = before;
        profile.SessionOptions.Clear();
        foreach (var entry in secrets)
            profile.SessionOptions.Add(entry.Key, entry.Value);
        profile.RemoveApps = apps;
        profile.DarkInstalledWindows = darkWindows;
    });

    private void Flag(Fields f, string key, string pl, string en)
    {
        var control = f.Check(L(pl, en), profile.Has(key), value => profile.Advanced[key] = value ? "1" : "0");
        var (helpPl, helpEn) = key switch
        {
            "ShowFileExtensions" => ("Eksplorator pokaże końcówki nazw plików, na przykład .txt albo .exe.", "File Explorer shows extensions such as .txt or .exe."),
            "ClassicContextMenu" => ("Po pierwszym logowaniu skrypt zastosuje starszy i nowszy wpis dla pełnego menu Eksploratora. Działanie zależy od kompilacji Windows 11.",
                "On first sign-in the script sets the old and new entries for the full File Explorer menu. The result depends on the Windows 11 build."),
            "UseNarrator" => ("Uruchamia wbudowany czytnik ekranu Windows, jeśli używany nośnik udostępnia go podczas instalacji.", "Starts the built-in screen reader if available on the installation media."),
            "CleanSensitive" => ("Po pierwszym logowaniu usuwa kopie pliku odpowiedzi z dysku systemowego. Wymaga automatycznego logowania konta z uprawnieniami administratora.", "Deletes cached answer files after the first sign-in. Requires automatic sign-in to a local administrator account."),
            "DisableWindowsUpdate" => ("Wstrzymuje aktualizacje przez zaplanowane zadanie. Wpływa również na dostarczanie poprawek zabezpieczeń.", "Pauses updates through a scheduled task. This also affects delivery of security fixes."),
            "DisableCoreIsolation" => ("Wyłącza część zabezpieczeń wykorzystujących wirtualizację. Zastosuj, jeśli wymaga tego oprogramowanie na docelowym komputerze.", "Disables some virtualization-based security features. Use it when required by the destination computer's software."),
            _ => ("Zaznacz, aby zastosować tę opcję na komputerze podczas wdrażania Windows.", "Check the option to apply it on the destination computer during Windows deployment.")
        };
        Explain(control, helpPl, helpEn);
    }

    private void Flags(Fields f, params (string Key, string Pl, string En)[] values)
    {
        foreach (var (key, pl, en) in values)
            Flag(f, key, pl, en);
    }

    private void StringOption(Fields f, string key, string pl, string en, string fallback = "", bool multi = false, bool secret = false)
    {
        var options = secret ? profile.SessionOptions : profile.Advanced;
        f.Entry(L(pl, en), profile.Option(key, fallback), value => options[key] = multi || secret ? value : value.Trim(), secret, multi);
    }

    private void NumOption(Fields f, string key, string pl, string en, int initial, int min, int max) =>
        f.Number(L(pl, en), profile.Number(key, initial), min, max,
            value => profile.Advanced[key] = value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private ComboBox Mode(Fields f, string key, string pl, string en, string initial, Choice<string>[] choices, Action<string>? show = null) =>
        f.Select(L(pl, en), profile.Option(key, initial), choices,
            value => { profile.Advanced[key] = value; show?.Invoke(value); });

    private void ExplorerOptions(Fields f)
    {
        Mode(f, "HideFiles", "Które pliki ukrywać?", "Which files should be hidden?", "Hidden",
            [C("Hidden", "Domyślne", "Default"), C("HiddenSystem", "Chronione pliki systemowe", "Protected system files"), C("None", "Pokaż wszystkie pliki", "Show all files")]);
        Flags(f, ("ShowFileExtensions", "Pokaż rozszerzenia plików", "Show file extensions"),
            ("LaunchToThisPC", "Otwieraj Eksplorator w widoku Ten komputer", "Open File Explorer to This PC"),
            ("HideInfoTip", "Ukryj podpowiedzi o plikach i folderach", "Hide file and folder info tips"),
            ("HideTaskViewButton", "Ukryj przycisk widoku zadań", "Hide the Task View button"),
            ("ShowAllTrayIcons", "Pokaż wszystkie ikony w obszarze powiadomień", "Show all notification-area icons"),
            ("DisableBingResults", "Ukryj wyniki Bing z wyszukiwania", "Hide Bing search results"));
        if (profile.Target.Windows11)
            Flags(f, ("ClassicContextMenu", "Przywróć pełne menu kontekstowe Eksploratora", "Restore the full File Explorer context menu"),
                ("LeftTaskbar", "Wyrównaj ikony paska zadań do lewej", "Left-align taskbar icons"),
                ("DisableWidgets", "Wyłącz widżety", "Disable widgets"),
                ("ShowEndTask", "Pokaż polecenie Zakończ zadanie na pasku", "Show End task on the taskbar"));
        Mode(f, "TaskbarSearch", "Wyszukiwanie na pasku zadań", "Taskbar search", "Box",
            [C("Box", "Pole wyszukiwania", "Search box"), C("Label", "Ikona i opis", "Icon and label"), C("Icon", "Ikona", "Icon"), C("Hide", "Ukryj", "Hide")]);
        LayoutOption(f, "TaskbarIcons", "TaskbarXml", "Ikony na pasku zadań", "Taskbar icons", "XML");
        LayoutOption(f, "StartLayout", "StartLayoutText", "Układ Start", "Start layout", profile.Target.Windows11 ? "JSON" : "XML");
    }

    private void LayoutOption(Fields f, string key, string markup, string pl, string en, string format)
    {
        var detail = new Fields();
        Mode(f, key, pl, en, "Default",
            [C("Default", "Domyślny", "Default"), C("Empty", "Usuń przypięte pozycje", "Remove pinned items"),
             new("Custom", LF($"Własny układ {format}", $"Custom {format} layout"))],
            value => detail.Table.Visible = value == "Custom");
        string content = LF($"Treść układu {format}", $"{format} layout content");
        StringOption(detail, markup, content, content, multi: true);
        detail.Table.Visible = profile.Option(key) == "Custom";
        f.Section(detail);
    }

    private void SystemOptions(Fields f)
    {
        Flags(f, ("EnableLongPaths", "Włącz obsługę długich ścieżek", "Enable long paths"),
            ("EnableRemoteDesktop", "Włącz Pulpit zdalny", "Enable Remote Desktop"),
            ("HardenSystemDriveAcl", "Zaostrz uprawnienia do katalogu głównego C:", "Harden permissions on the C: root"),
            ("DeleteJunctions", "Usuń starsze połączenia katalogów systemowych", "Remove legacy system junctions"),
            ("DisableUac", "Wyłącz UAC", "Disable UAC"), ("DisableSmartScreen", "Wyłącz SmartScreen", "Disable SmartScreen"),
            ("DisableSystemRestore", "Wyłącz przywracanie systemu", "Disable System Restore"),
            ("DisableCoreIsolation", "Wyłącz izolację rdzenia", "Disable core isolation"),
            ("DisableFastStartup", "Wyłącz szybkie uruchamianie", "Disable Fast Startup"),
            ("DisableWindowsUpdate", "Cyklicznie wstrzymuj aktualizacje", "Pause Windows Update repeatedly"),
            ("PreventAutomaticReboot", "Zapobiegaj restartom Windows Update", "Prevent Windows Update restarts"),
            ("DisableAppSuggestions", "Wyłącz automatyczne sugestie aplikacji", "Disable app suggestions"),
            ("AllowPowerShellScripts", "Ustaw zasady PowerShell na RemoteSigned", "Set PowerShell execution policy to RemoteSigned"),
            ("DisableLastAccess", "Wyłącz zapis czasu ostatniego dostępu", "Disable last access timestamps"),
            ("HideEdgeFre", "Ukryj pierwsze uruchomienie Edge", "Hide Edge first run"),
            ("DisableEdgeStartupBoost", "Wyłącz start Edge w tle", "Disable Edge Startup Boost and background mode"),
            ("MakeEdgeUninstallable", "Zezwól na odinstalowanie Edge", "Make Edge uninstallable"),
            ("DeleteWindowsOld", "Usuń pusty folder Windows.old", "Delete the empty Windows.old folder"),
            ("DisableAutomaticRestartSignOn", "Wyłącz automatyczne logowanie po restarcie", "Disable automatic sign-in after restarting"),
            ("DisableWpbt", "Wyłącz wykonywanie tabeli WPBT", "Disable WPBT execution"),
            ("PreventDeviceApps", "Blokuj automatyczne aplikacje urządzeń", "Prevent automatic hardware-associated apps"),
            ("Audit", "Rejestruj tworzenie procesów", "Audit process creation"),
            ("AuditCommand", "Zapisuj wiersze poleceń w zdarzeniach tworzenia procesów", "Include command lines in process creation events"));
        if (profile.Target.Windows11)
            Flags(f, ("DisableSac", "Wyłącz Smart App Control", "Disable Smart App Control"),
                ("PreventDeviceEncryption", "Zapobiegaj automatycznemu szyfrowaniu urządzenia", "Prevent automatic device encryption"));
        var xml = new Fields();
        f.Check(L("Skonfiguruj AppLocker z pliku XML", "Configure AppLocker from XML"), profile.Has("AppLocker"),
            value => { profile.Advanced["AppLocker"] = value ? "1" : "0"; xml.Table.Visible = value; });
        StringOption(xml, "AppLockerXml", "Zasady AppLocker w XML", "AppLocker policy XML", multi: true);
        xml.Table.Visible = profile.Has("AppLocker");
        f.Section(xml);
    }

    private void NetworkOptions(Fields f)
    {
        var ssid = new Fields();
        var password = new Fields();
        var xml = new Fields();
        void Refresh(string mode)
        {
            ssid.Table.Visible = mode is "Open" or "WPA2PSK" or "WPA3SAE";
            password.Table.Visible = mode is "WPA2PSK" or "WPA3SAE";
            xml.Table.Visible = mode == "XML";
        }
        Explain(Mode(f, "WifiMode", "Jak skonfigurować Wi-Fi?", "How should Wi-Fi be configured?", "Default",
            [C("Default", "Bez gotowego profilu sieci", "Leave the Wi-Fi profile unset",
                "Pominięcie ekranu Wi-Fi ustawisz w kroku Użytkownicy i pierwsze logowanie.",
                "To skip the Wi-Fi screen, use the Users and first sign-in page."),
             C("Open", "Sieć otwarta", "Open network"), C("WPA2PSK", "WPA2-Personal", "WPA2-Personal"),
             C("WPA3SAE", "WPA3-Personal", "WPA3-Personal"), C("XML", "Profil WLAN w XML", "WLAN profile XML")], Refresh),
            "Windows użyje gotowego profilu sieci. Do połączenia może być potrzebny sterownik karty Wi-Fi.",
            "Windows uses a preconfigured network profile. Connecting may require a Wi-Fi adapter driver.");
        StringOption(ssid, "Ssid", "Nazwa sieci (SSID)", "Network name (SSID)");
        Flag(ssid, "WifiHidden", "Połącz również z siecią ukrytą", "Connect even if the network is hidden");
        StringOption(password, "WifiPassword", "Hasło Wi-Fi", "Wi-Fi password", secret: true);
        StringOption(xml, "WifiXml", "Treść profilu XML WLAN (tylko na tę sesję)", "WLAN profile XML content (this session only)", multi: true, secret: true);
        Refresh(profile.Option("WifiMode", "Default"));
        f.Section(ssid);
        f.Section(password);
        f.Section(xml);
        Flags(f, ("VBoxGuestAdditions", "Zainstaluj VirtualBox Guest Additions", "Install VirtualBox Guest Additions"),
            ("VMwareTools", "Zainstaluj VMware Tools", "Install VMware Tools"),
            ("VirtIoGuestTools", "Zainstaluj sterowniki, narzędzia i agenta VirtIO", "Install VirtIO drivers, tools and guest agent"),
            ("ParallelsTools", "Zainstaluj Parallels Tools", "Install Parallels Tools"));
    }

    private void AppearanceOptions(Fields f)
    {
        Flags(f, ("UseNarrator", "Uruchom Narratora podczas instalacji (gdy dostępny)", "Start Narrator during installation (where available)"),
            ("DeleteEdgeDesktopIcon", "Usuń skrót Edge z pulpitu", "Delete the Edge desktop shortcut"),
            ("TurnOffSystemSounds", "Wyłącz dźwięki systemu", "Turn off system sounds"),
            ("DisablePointerPrecision", "Wyłącz zwiększanie precyzji wskaźnika", "Disable enhanced pointer precision"));
        var effect = new Fields();
        Mode(f, "Effects", "Efekty wizualne", "Visual effects", "Default",
            [C("Default", "Domyślne", "Default"), C("Performance", "Najlepsza wydajność", "Best performance"),
                C("Appearance", "Najlepszy wygląd", "Best appearance"), C("Custom", "Własne ustawienia", "Custom")],
            value => effect.Table.Visible = value == "Custom");
        Flags(effect, ("Effect.ControlAnimations", "Animuj elementy okien", "Animate controls in windows"),
            ("Effect.AnimateMinMax", "Animuj minimalizowanie i maksymalizowanie", "Animate minimizing and maximizing"),
            ("Effect.TaskbarAnimations", "Animuj pasek zadań", "Animate the taskbar"),
            ("Effect.DWMAeroPeekEnabled", "Włącz funkcję Peek", "Enable Peek"),
            ("Effect.MenuAnimation", "Animuj menu", "Animate menus"), ("Effect.TooltipAnimation", "Animuj podpowiedzi", "Animate tooltips"),
            ("Effect.SelectionFade", "Wygaszaj elementy menu po kliknięciu", "Fade out selected menu items"),
            ("Effect.DWMSaveThumbnailEnabled", "Zapisuj miniatury paska zadań", "Save taskbar thumbnail previews"),
            ("Effect.CursorShadow", "Pokaż cień kursora", "Show pointer shadows"),
            ("Effect.ListviewShadow", "Pokaż cienie etykiet ikon pulpitu", "Show shadows for desktop icon labels"),
            ("Effect.ThumbnailsOrIcon", "Pokaż miniatury zamiast ikon", "Show thumbnails instead of icons"),
            ("Effect.ListviewAlphaSelect", "Pokaż przezroczyste zaznaczenie", "Show a translucent selection rectangle"),
            ("Effect.DragFullWindows", "Pokaż zawartość okien przy przeciąganiu", "Show window contents while dragging"),
            ("Effect.ComboBoxAnimation", "Animuj listy rozwijane", "Animate combo boxes"),
            ("Effect.FontSmoothing", "Wygładzaj krawędzie czcionek", "Smooth font edges"),
            ("Effect.ListBoxSmoothScrolling", "Płynnie przewijaj listy", "Smooth-scroll lists"),
            ("Effect.DropShadow", "Pokaż cienie pod oknami", "Show shadows under windows"));
        effect.Table.Visible = profile.Option("Effects") == "Custom";
        f.Section(effect);
        BooleanSection(f, "DesktopIcons", "Ustaw własny zestaw ikon pulpitu", "Set custom desktop icons", icons =>
        {
            foreach (var icon in ModernOptions.Engine.DesktopIcons.Values.OrderBy(i => i.DisplayName))
                Flag(icons, "Desktop." + icon.Id, TranslateLocation(icon.DisplayName), icon.DisplayName);
        });
        if (profile.Target.Windows11)
            BooleanSection(f, "StartFolders", "Wybierz foldery obok przycisku zasilania w menu Start", "Choose folders next to the Start power button", folders =>
            {
                foreach (var item in ModernOptions.Engine.StartFolders.Values.OrderBy(i => i.DisplayName))
                    Flag(folders, "Start." + item.Id, TranslateLocation(item.DisplayName), item.DisplayName);
            });
        BooleanSection(f, "LockKeys", "Ustaw stan i działanie klawiszy blokady", "Set lock-key states and behavior", keys =>
        {
            foreach (var key in new[] { "Caps", "Num", "Scroll" })
            {
                string startup = LF($"{key} Lock: włącz przy starcie", $"{key} Lock: on at startup");
                string ignore = LF($"{key} Lock: ignoruj naciśnięcie", $"{key} Lock: ignore key presses");
                Flags(keys, (key + "On", startup, startup), (key + "Ignore", ignore, ignore));
            }
        });
        var sticky = new Fields();
        Mode(f, "Sticky", "Klawisze trwałe", "Sticky Keys", "Default",
            [C("Default", "Domyślnie", "Default"), C("Disable", "Wyłącz", "Disable"), C("Custom", "Własne ustawienia", "Custom")],
            value => sticky.Table.Visible = value == "Custom");
        Flags(sticky, ("Sticky.HotKeyActive", "Przełącz klawisze trwałe po pięciu naciśnięciach Shift", "Toggle Sticky Keys after five Shift presses"),
            ("Sticky.HotKeySound", "Dźwięk przełączania funkcji", "Play a sound when toggling Sticky Keys"),
            ("Sticky.Indicator", "Pokaż ikonę klawiszy trwałych", "Show the Sticky Keys icon"),
            ("Sticky.AudibleFeedback", "Dźwięk klawiszy modyfikujących", "Play sounds for modifier keys"),
            ("Sticky.TriState", "Blokuj klawisz po dwóch naciśnięciach", "Lock a modifier after pressing it twice"),
            ("Sticky.TwoKeysOff", "Wyłącz po równoczesnym naciśnięciu dwóch klawiszy", "Turn off when two keys are pressed together"));
        sticky.Table.Visible = profile.Option("Sticky") == "Custom";
        f.Section(sticky);
        var themeModes = new Fields();
        f.Check(L("Ustaw &ciemny motyw systemu i aplikacji", "Set a &dark theme for Windows and apps"), profile.DarkInstalledWindows, value =>
        {
            profile.DarkInstalledWindows = value;
            themeModes.Table.Visible = !value;
        });
        BooleanSection(f, "ColorTheme", "Ustaw własne kolory", "Set custom colors", theme =>
        {
            Flags(themeModes, ("DarkSystem", "Ciemny motyw systemu", "Dark system theme"),
                ("DarkApps", "Ciemny motyw aplikacji", "Dark app theme"));
            themeModes.Table.Visible = !profile.DarkInstalledWindows;
            theme.Section(themeModes);
            Flags(theme, ("Transparent", "Włącz przezroczystość", "Enable transparency"),
                ("AccentStart", "Akcent na pasku i w Start", "Show accent on Start and taskbar"),
                ("AccentBorders", "Akcent na paskach tytułu i obramowaniach", "Show accent on title bars and borders"));
            StringOption(theme, "Accent", "Kolor akcentu (#RRGGBB)", "Accent color (#RRGGBB)", "#0078D4");
        });
        var solid = new Fields();
        var wallpaper = new Fields();
        Mode(f, "Wallpaper", "Tapeta", "Wallpaper", "Default",
            [C("Default", "Domyślna", "Default"), C("Solid", "Jednolity kolor", "Solid color"), C("Script", "Skrypt PowerShell", "PowerShell script")],
            value => { solid.Table.Visible = value == "Solid"; wallpaper.Table.Visible = value == "Script"; });
        StringOption(solid, "WallpaperColor", "Kolor tapety (#RRGGBB)", "Wallpaper color (#RRGGBB)", "#000000");
        StringOption(wallpaper, "WallpaperScript", "Skrypt zwracający bajty obrazu tapety", "Script returning wallpaper image bytes", multi: true);
        solid.Table.Visible = profile.Option("Wallpaper") == "Solid";
        wallpaper.Table.Visible = profile.Option("Wallpaper") == "Script";
        f.Section(solid);
        f.Section(wallpaper);
        BooleanSection(f, "LockScreen", "Ustaw obraz ekranu blokady", "Set a lock screen image", section =>
            StringOption(section, "LockScreenScript", "Skrypt zwracający bajty obrazu", "Script returning image bytes", multi: true));
    }

    private static string TranslateLocation(string name) => name switch
    {
        "Control Panel" => "Panel sterowania", "Desktop" => "Pulpit", "Documents" => "Dokumenty",
        "Downloads" => "Pobrane", "Gallery" => "Galeria", "Home" => "Strona główna", "Music" => "Muzyka",
        "Network" => "Sieć", "Pictures" => "Obrazy", "Recycle bin" => "Kosz", "This PC" => "Ten komputer",
        "User's Files" => "Pliki użytkownika", "Videos" => "Wideo", "File Explorer" => "Eksplorator plików",
        "Personal Folder" => "Folder osobisty", "Settings" => "Ustawienia", _ => name
    };

    private void BooleanSection(Fields f, string key, string pl, string en, Action<Fields> setup)
    {
        var detail = new Fields();
        f.Check(L(pl, en), profile.Has(key), value =>
        {
            profile.Advanced[key] = value ? "1" : "0";
            detail.Table.Visible = value;
        });
        setup(detail);
        detail.Table.Visible = profile.Has(key);
        f.Section(detail);
    }

    private void AccountOptions(Fields f)
    {
        var expiry = new Fields();
        Mode(f, "Expiry", "Wygasanie haseł", "Password expiration", "Default",
            [C("Default", "Domyślne ustawienie Windows", "Windows default"), C("Never", "Hasła nie wygasają", "Passwords do not expire"),
                C("Custom", "Własny okres ważności", "Custom maximum password age")], value => expiry.Table.Visible = value == "Custom");
        NumOption(expiry, "ExpiryDays", "Ważność hasła w dniach", "Maximum password age in days", 42, 1, 999);
        expiry.Table.Visible = profile.Option("Expiry") == "Custom";
        f.Section(expiry);
        var lockout = new Fields();
        Mode(f, "Lockout", "Zasady blokady konta", "Account lockout policy", "Default",
            [C("Default", "Domyślne ustawienie Windows", "Windows default"), C("Disable", "Wyłącz blokadę konta", "Disable account lockout"),
                C("Custom", "Własne zasady", "Custom lockout policy")], value => lockout.Table.Visible = value == "Custom");
        NumOption(lockout, "LockThreshold", "Liczba nieudanych prób", "Number of failed attempts", 10, 1, 999);
        NumOption(lockout, "LockWindow", "Okres zliczania w minutach", "Counter window in minutes", 10, 1, 99999);
        NumOption(lockout, "LockDuration", "Czas blokady w minutach", "Lockout duration in minutes", 10, 1, 99999);
        lockout.Table.Visible = profile.Option("Lockout") == "Custom";
        f.Section(lockout);
    }

    private void RemovalOptions(Fields f)
    {
        var items = ModernOptions.Engine.Bloatwares.Values.OrderBy(b => b.DisplayName).ToArray();
        var checks = new List<(CheckBox Check, string Key)>(items.Length);
        bool applyingPreset = false;
        string instruction = L("Zaznaczona aplikacja zostanie usunięta. Odznaczona zostanie zachowana.",
            "A checked app will be removed. An unchecked app will be kept.");
        f.Note(instruction);
        f.Note(L("Użyj Tab, aby przejść do następnej aplikacji, i Spacji, aby zmienić zaznaczenie.",
            "Use Tab to move to the next app and Space to change its checked state."));
        f.Note(L("Zalecamy zachować Microsoft Store oraz mowę i głosy Windows.",
            "Keep Microsoft Store and Windows speech and voices for the recommended setup."));
        var preset = Explain(f.Select(L("&Zestaw aplikacji do usunięcia", "&App removal preset"), profile.RemoveApps,
            [C(AppRemoval.Custom, "Wybieram ręcznie", "Choose manually"),
             C(AppRemoval.Recommended, "Dodatki i gry", "Extras and games",
                 "Zaznacza dodatki i gry, w tym Clipchamp i aplikacje Xbox. Możesz ręcznie odznaczyć dowolną pozycję.",
                 "Checks extras and games including Clipchamp and the Xbox apps. You can uncheck any entry."),
             C(AppRemoval.Extended, "Dodatki, gry i część narzędzi", "Extras, games and some utilities",
                 "Zaznacza też aplikacje Kamera, Zegar, Zdjęcia, Karteczki, Rejestrator dźwięku i nowy Media Player.",
                 "Also checks Camera, Clock, Photos, Sticky Notes, Sound Recorder and the modern Media Player."),
             C(AppRemoval.Keep, "Zachowaj wszystkie aplikacje", "Keep all apps",
                 "Czyści zaznaczenia na liście. Potem możesz zaznaczyć pojedyncze pozycje.", "Clears the list. You can then select individual items.")],
            value =>
            {
                AppRemovalOptions.Apply(profile, value);
                if (value == AppRemoval.Custom)
                    return;
                applyingPreset = true;
                try
                {
                    foreach (var (check, key) in checks)
                        check.Checked = profile.Has(key);
                }
                finally
                {
                    applyingPreset = false;
                }
            }), "Zestaw zaznacza pozycje na liście. Możesz potem zmienić każdą z osobna.",
            "A preset checks the matching entries. You can then change each app individually.");
        foreach (var item in items)
        {
            string key = "App." + item.Id;
            var check = f.Check($"{AppRemovalName(item)} — {AppRemovalLabel(item)}", profile.Has(key), value =>
            {
                if (applyingPreset)
                    return;
                profile.Advanced[key] = value ? "1" : "0";
                SelectValue(preset, AppRemoval.Custom);
            });
            string help = AppRemovalHelp(item);
            string description = $"{instruction} {help}";
            Explain(check, description, description);
            checks.Add((check, key));
        }
    }

    private string AppRemovalName(Bloatware app) => app.Id switch
    {
        "RemoveSpeech" => L("Mowa i głosy (wszystkie języki)", app.DisplayName),
        "RemoveHandwriting" => L("Rozpoznawanie pisma odręcznego", app.DisplayName),
        "RemoveWindowsHello" => L("Rozpoznawanie twarzy w Windows Hello", app.DisplayName),
        "RemoveMediaFeatures" => L("Funkcje multimedialne Windows", app.DisplayName),
        "RemoveZuneMusic" => L("Odtwarzacz multimedialny (nowy)", app.DisplayName),
        "RemovePhotos" => L("Zdjęcia", app.DisplayName),
        "RemoveCamera" => L("Kamera", app.DisplayName),
        "RemoveCalculator" => L("Kalkulator", app.DisplayName),
        "RemoveClock" => L("Zegar", app.DisplayName),
        "RemoveStickyNotes" => L("Karteczki", app.DisplayName),
        "RemoveVoiceRecorder" => L("Rejestrator dźwięku", app.DisplayName),
        "RemoveSnippingTool" => L("Narzędzie Wycinanie", app.DisplayName),
        "RemoveQuickAssist" => L("Szybka pomoc", app.DisplayName),
        "RemoveNotepad" => L("Notatnik (nowy)", app.DisplayName),
        _ => UiText.Get(UiLanguage, app.DisplayName)
    };

    private string AppRemovalLabel(Bloatware app)
    {
        if (app.Id is "RemoveStore" or "RemoveSpeech")
            return L("zalecamy zachować", "recommended to keep");
        if (AppRemovalOptions.InPreset(app.DisplayName, AppRemoval.Recommended))
            return L("w zestawie Dodatki i gry", "in Extras and games");
        if (AppRemovalOptions.InPreset(app.DisplayName, AppRemoval.Extended))
            return L("w rozszerzonym zestawie", "in the extended preset");
        return L("zachowaj, jeśli używasz", "keep if you use it");
    }

    private string AppRemovalHelp(Bloatware app) => app.Id switch
    {
        "RemoveStore" => L("Zachowaj Microsoft Store, jeśli pobierasz lub aktualizujesz przez niego aplikacje. Przywrócenie Sklepu może być trudne.",
            "Keep Microsoft Store if you use it to download or update apps. Restoring the Store can be difficult."),
        "RemoveSpeech" => L("Zalecamy zachować. Usunięcie obejmuje składniki rozpoznawania i syntezy mowy we wszystkich językach, w tym głosy używane przez część czytników ekranu.",
            "We recommend keeping this. Removal affects speech recognition and text-to-speech in every language, including voices used by some screen readers."),
        "RemoveWindowsHello" => L("Zachowaj, jeśli logujesz się twarzą. Usunięcie dotyczy rozpoznawania twarzy.",
            "Keep this if you sign in with your face. Removal affects facial recognition."),
        "RemoveMediaFeatures" => L("Zachowaj, jeśli korzystasz z aplikacji używających systemowych funkcji odtwarzania lub nagrywania audio i wideo.",
            "Keep this if you use apps that depend on Windows audio and video playback or recording features."),
        "RemoveOneDrive" => L("Zachowaj, jeśli ten komputer ma synchronizować pliki z OneDrive.", "Keep this if the computer will sync files with OneDrive."),
        "RemoveXboxApps" => L("Usuwa grupę aplikacji Xbox i składniki używane przez część gier z Microsoft Store i PC Game Pass.",
            "Removes the Xbox apps and components used by some Microsoft Store and PC Game Pass games."),
        _ => AppRemovalLabel(app) + "."
    };

    private void PeOptions(Fields f)
    {
        var generated = new Fields();
        var custom = new Fields();
        var normal = new Fields();
        Explain(f.Select(L("&Metoda instalacji", "Installation &method"), profile.InstallationMethod,
            InstallationMethods(), value =>
            {
                profile.Advanced["PE"] = value;
                ShowMethod();
            }),
            "Windows Setup obsługuje zwykłą instalację. DISM nakłada obraz bezpośrednio w Windows PE.",
            "Windows Setup runs a standard installation. DISM applies the image directly in Windows PE.");
        if (profile.Target.Windows11 && !profile.UsesLegacyWindows11)
        {
            Flag(normal, "BypassRequirements", "Pomiń sprawdzenie zgodności sprzętu z Windows 11", "Skip Windows 11 hardware compatibility checks");
            normal.Note(L("Dotyczy TPM, Secure Boot i RAM. Działanie zależy od kompilacji Windows 11.",
                "Applies to TPM, Secure Boot and RAM. The result depends on the Windows 11 build."));
        }
        else if (profile.UsesLegacyWindows11)
            normal.Note(L("W trybie Legacy XML włącza pominięcie sprawdzania zgodności sprzętu z Windows 11.",
                "In Legacy mode, the XML enables the Windows 11 hardware compatibility bypass."));
        Flag(f, "HidePowerShellWindows", "Ukryj okna uruchamianych skryptów PowerShell", "Hide PowerShell script windows");
        StringOption(custom, "PEText", "Treść skryptu CMD WinPE", "WinPE CMD script content", multi: true);
        normal.Note(L("Microsoft Windows Setup odczyta plik XML i sam uruchomi standardowe etapy instalacji. W razie problemu może wyświetlić ekran wymagający odpowiedzi.",
            "Microsoft Windows Setup reads the XML and runs the standard installation stages. An issue can bring up a prompt that needs an answer."));
        generated.Note(L("Skrypt ze strony schneegans.de nakłada obraz przez DISM i tworzy pliki rozruchowe zgodnie z wybranym układem i trybem rozruchu.",
            "The script from schneegans.de applies the image with DISM and creates boot files for the selected layout and boot mode."));
        generated.Note(L("Jeśli chcesz podać klucz w tym trybie, użyj osobnego pola klucza aktywacji w kroku Edycja i klucz.",
            "To supply a key in this mode, use the separate activation key field on the Edition and key page."));
        custom.Note(L("Podejmujesz decyzje o partycjach i instalacji w treści skryptu CMD. W wersji standardowej WinPE PowerShell może być niedostępny.",
            "The CMD script is responsible for partitioning and installation. PowerShell may be unavailable in standard WinPE."));
        var automaticLayout = new Fields();
        var diskChecks = new Fields();
        var diskScript = new Fields();
        void UpdateDisk(string value)
        {
            diskChecks.Table.Visible = value == "Checked";
            diskScript.Table.Visible = value == "Script";
        }
        Mode(automaticLayout, "TargetDisk", "Wybór dysku w trybie DISM", "Target disk selection in DISM mode", "Fixed",
            [C("Fixed", "Numer dysku z kreatora", "Disk number from the wizard"), C("Interactive", "Zapytaj w WinPE", "Ask in WinPE"),
                C("Checked", "Znajdź dysk według warunków", "Select a disk by criteria"), C("Script", "Własny skrypt VBScript", "Custom VBScript")], UpdateDisk);
        StringOption(diskScript, "TargetDiskScript", "Skrypt VBScript zwracający numer dysku", "VBScript returning a disk number", multi: true);
        NumOption(diskChecks, "DiskMinGiB", "Minimalna pojemność dysku w GiB", "Minimum disk size in GiB", 64, 1, 999999);
        NumOption(diskChecks, "DiskMaxGiB", "Maksymalna pojemność dysku w GiB", "Maximum disk size in GiB", 4096, 1, 999999);
        Flags(diskChecks, ("DiskAnyIndex", "Dopuść dowolny numer dysku", "Allow any disk index"),
            ("AllowExistingPartitions", "Dopuść dyski z partycjami", "Allow disks with existing partitions"),
            ("DiskInterface", "Ogranicz typ interfejsu", "Restrict the interface type"),
            ("DiskMedia", "Ogranicz typ nośnika", "Restrict the media type"));
        UpdateDisk(profile.Option("TargetDisk", "Fixed"));
        automaticLayout.Section(diskChecks);
        automaticLayout.Section(diskScript);
        var customDisk = new Fields();
        generated.Check(L("Użyj własnego skryptu DiskPart", "Use a custom DiskPart script"), profile.Has("CustomDiskPart"), value =>
        {
            profile.Advanced["CustomDiskPart"] = value ? "1" : "0";
            UpdateDiskPart();
        });
        StringOption(customDisk, "DiskPartText", "Treść skryptu DiskPart (S: system, W: Windows)", "DiskPart script content (S: system, W: Windows)", multi: true);
        generated.Section(customDisk);
        generated.Section(automaticLayout);
        UpdateDiskPart();
        Flags(generated, ("DisableDefender", "Wyłącz Microsoft Defender w obrazie offline", "Disable Microsoft Defender in the offline image"),
            ("Disable8Dot3", "Wyłącz nazwy plików 8.3", "Disable 8.3 file names"),
            ("PauseFormat", "Zatrzymaj przed formatowaniem", "Pause before formatting"),
            ("PauseReboot", "Zatrzymaj przed ponownym uruchomieniem", "Pause before rebooting"),
            ("CompactOs", "Użyj Compact OS", "Use Compact OS"),
            ("SkipIntegrity", "Pomiń dodatkowe sprawdzanie obrazu przez DISM", "Skip additional DISM image integrity checks"));
        var paging = new Fields();
        Mode(generated, "Paging", "Plik stronicowania", "Paging file", "Default",
            [C("Default", "Domyślny", "Default"), C("None", "Wyłącz", "Disable"), C("Custom", "Własne rozmiary", "Custom sizes")],
            value => paging.Table.Visible = value == "Custom");
        NumOption(paging, "PagingInitial", "Rozmiar początkowy w MiB", "Initial size in MiB", 2048, 16, 16777216);
        NumOption(paging, "PagingMax", "Rozmiar maksymalny w MiB", "Maximum size in MiB", 4096, 16, 16777216);
        paging.Table.Visible = profile.Option("Paging") == "Custom";
        generated.Section(paging);
        f.Section(normal);
        f.Section(generated);
        f.Section(custom);
        ShowMethod();

        void ShowMethod()
        {
            string method = profile.InstallationMethod;
            normal.Table.Visible = method == "Normal";
            generated.Table.Visible = method == "Generated";
            custom.Table.Visible = method == "Custom";
        }

        void UpdateDiskPart()
        {
            customDisk.Table.Visible = profile.Has("CustomDiskPart");
            automaticLayout.Table.Visible = !profile.Has("CustomDiskPart") && profile.WipesDisk;
        }
    }

    private CustomComponent? ComponentEditor(CustomComponent? old)
    {
        string pass = old?.Pass ?? "specialize";
        string component = old?.Name ?? "";
        string markup = old?.Markup ?? "";
        var f = new Fields();
        f.Select(L("&Przebieg konfiguracji", "Configuration &pass"), pass,
            Enum.GetNames<Schneegans.Unattend.Pass>().Select(p => new Choice<string>(p, p)), value => pass = value);
        var name = f.Entry(L("&Nazwa składnika (np. Microsoft-Windows-Shell-Setup)", "Component &name (e.g. Microsoft-Windows-Shell-Setup)"),
            component, value => component = value.Trim());
        f.Entry(L("&Ustawienia XML wewnątrz składnika", "XML &settings inside the component"), markup, value => markup = value, multiline: true);
        return Ask(L("Składnik XML", "XML component"), f, name, validate: () =>
        {
            if (component.Length == 0 || string.IsNullOrWhiteSpace(markup))
                throw new SetupException("Wpisz nazwę i ustawienia składnika.", "Enter the component name and settings.");
        }) ? new CustomComponent(pass, component, markup) : null;
    }

    private void EditEmbeddedScripts()
    {
        var fields = new Fields();
        var list = profile.EmbeddedScripts.ToList();
        EditableList(fields, L("Skrypty w kolejności dodania", "Scripts in the order added"), list,
            script => $"{ScriptPhaseLabel(script.Phase)} — {script.Type} — {script.Text.Split('\n')[0].Trim()[..Math.Min(54, script.Text.Split('\n')[0].Trim().Length)]}",
            ScriptEditor);
        bool restart = profile.Has("RestartExplorer");
        fields.Check(L("Uruchom ponownie Eksplorator po skryptach użytkownika", "Restart Explorer after user scripts"), restart, value => restart = value);
        if (Ask(L("Skrypty osadzone w XML", "Scripts embedded in XML"), fields))
        {
            profile.EmbeddedScripts = list;
            profile.Advanced["RestartExplorer"] = restart ? "1" : "0";
        }
    }

    private string ScriptPhaseLabel(string phase) => phase switch
    {
        "System" => L("Konfiguracja systemu (specialize)", "System configuration (specialize)"),
        "FirstLogon" => L("Pierwsze logowanie po instalacji", "First sign-in after Setup"),
        "UserOnce" => L("Pierwsze logowanie każdego użytkownika", "First sign-in for each user"),
        _ => L("Domyślny profil użytkownika", "Default user profile")
    };

    private EmbeddedScript? ScriptEditor(EmbeddedScript? old)
    {
        string phase = old?.Phase ?? "System";
        string type = old?.Type ?? "Ps1";
        string content = old?.Text ?? "";
        var fields = new Fields();
        fields.Select(L("&Kiedy uruchomić skrypt?", "&When should the script run?"), phase,
            Enum.GetNames<ScriptPhase>().Select(p => new Choice<string>(p, ScriptPhaseLabel(p))), value => phase = value);
        fields.Select(L("&Rodzaj skryptu", "&Script type"), type,
            Enum.GetNames<ScriptType>().Select(p => new Choice<string>(p, p.ToUpperInvariant())), value => type = value);
        fields.Entry(L("&Treść skryptu", "Script &content"), content, value => content = value, multiline: true);
        return Ask(L("Skrypt osadzony", "Embedded script"), fields, validate: () =>
        {
            if (string.IsNullOrWhiteSpace(content))
                throw new SetupException("Wpisz treść skryptu.", "Enter the script content.");
            if (phase == "DefaultUser" && type == "Vbs")
                throw new SetupException("Dla domyślnego profilu wybierz CMD, PS1 lub REG.", "Choose CMD, PS1 or REG for the default user profile.");
        }) ? new EmbeddedScript(content, phase, type) : null;
    }
}
