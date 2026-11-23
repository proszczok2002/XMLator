namespace XMLator;

internal sealed partial class Wizard
{
    private void RegionChoices(Fields f)
    {
        var details = new Fields();
        ComboBox? country = null, formats = null, olderApps = null, zone = null;
        var keyboardControls = new ComboBox[3];
        var stored = profile.Keyboard.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string[] keyboards = [stored.ElementAtOrDefault(0) ?? "", stored.ElementAtOrDefault(1) ?? "", stored.ElementAtOrDefault(2) ?? ""];
        bool applyingDefaults = false;
        var languages = new[] { C("", "Domyślny język z nośnika", "Use the media's default language") }
            .Concat(WithSavedChoice(LocaleCatalog.Languages(UiLanguage), profile.UiLocale));
        Explain(f.Select(L("&Język instalatora i Windows", "Setup and Windows &language"), profile.SetLocale ? profile.UiLocale : "", languages,
            language =>
            {
                profile.SetLocale = language.Length > 0;
                details.Table.Visible = profile.SetLocale;
                if (!profile.SetLocale)
                    return;
                LocaleCatalog.ApplyLanguage(profile, language);
                applyingDefaults = true;
                try
                {
                    SelectValue(country, profile.GeoId);
                    SelectValue(formats, profile.UserLocale);
                    SelectValue(olderApps, profile.SystemLocale);
                    SelectValue(zone, profile.TimeZone);
                    keyboards[0] = profile.Keyboard;
                    keyboards[1] = keyboards[2] = "";
                    for (int i = 0; i < keyboardControls.Length; i++)
                        SelectValue(keyboardControls[i], keyboards[i]);
                }
                finally
                {
                    applyingDefaults = false;
                }
            }), "Wybierz język dostępny na pendrivie. Kreator dopasuje klawiaturę, formaty oraz region, które możesz potem zmienić.",
            "Choose a language available on your USB drive. The wizard matches the keyboard, formats and region, and you can change them below.");

        if (profile.Target.ModernClient)
        {
            country = Explain(details.Select(L("&Kraj lub region", "&Country or region"), profile.GeoId,
                new[] { C("", "Dobierz do formatów regionalnych", "Match regional formats") }.Concat(WithSavedChoice(LocaleCatalog.Countries(UiLanguage), profile.GeoId)),
                value => profile.GeoId = value), "Określa kraj używany przez Windows i aplikacje. Możesz na przykład ustawić Polskę przy angielskim interfejsie.",
                "Sets the country used by Windows and apps. For example, you can use Poland with an English interface.");
        }
        var layouts = LocaleCatalog.Keyboards(UiLanguage);
        (string Pl, string En)[] keyboardNames =
        [
            ("Główna &klawiatura", "Primary &keyboard"), ("Druga klawiatura", "Second keyboard"), ("Trzecia klawiatura", "Third keyboard")
        ];
        for (int i = 0; i < keyboardControls.Length; i++)
        {
            int position = i;
            var options = WithSavedChoice(layouts, keyboards[i]);
            if (i > 0)
                options = new[] { C("", "Nie dodawaj", "Do not add") }.Concat(options);
            keyboardControls[i] = Explain(details.Select(L(keyboardNames[i].Pl, keyboardNames[i].En), keyboards[i], options,
                value =>
                {
                    if (applyingDefaults)
                        return;
                    keyboards[position] = value;
                    profile.Keyboard = string.Join(';', keyboards.Where(layout => layout.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
                }),
                i == 0 ? "Ten układ będzie aktywny jako pierwszy. Polski (programisty) pozwala wpisywać polskie znaki prawym Alt." :
                    "Dodatkowy układ przydaje się przy pisaniu w innym języku. Po instalacji przełączysz układy skrótem Windows+Spacja.",
                i == 0 ? "This layout is active first. Polish (Programmers) uses Right Alt to type Polish characters." :
                    "Use an extra layout for another language. After Setup, press Windows+Space to switch layouts.");
        }

        var regionalFormats = LocaleCatalog.Formats(UiLanguage);
        formats = Explain(details.Select(L("&Formaty dat, godzin i liczb", "&Date, time and number formats"), profile.UserLocale,
                WithSavedChoice(regionalFormats, profile.UserLocale), value => profile.UserLocale = value),
            "Na przykład formaty polskie używają przecinka jako separatora dziesiętnego.", "For example, Polish formats use a comma as the decimal separator.");
        olderApps = Explain(details.Select(L("Język dla &starszych programów", "Language for &older programs"), profile.SystemLocale,
                WithSavedChoice(regionalFormats, profile.SystemLocale), value => profile.SystemLocale = value),
            "Dotyczy starszych aplikacji, które nie obsługują Unicode. Zwykle wybierz język używanych starszych programów.",
            "Applies to older applications that do not support Unicode. Choose the language those programs use.");
        details.Table.Visible = profile.SetLocale;
        f.Section(details);
        var zones = new[] { C("", "Dobierz automatycznie w Windows", "Let Windows choose automatically") }
            .Concat(WithSavedChoice(LocaleCatalog.TimeZones(UiLanguage), profile.TimeZone));
        zone = Explain(f.Select(L("&Strefa czasowa", "&Time zone"), profile.TimeZone, zones, value => profile.TimeZone = value),
            "Wybierz strefę według miasta i przesunięcia od UTC. Dla Polski wybierz pozycję zawierającą Warszawę.",
            "Choose a time zone by its city and UTC offset. For Poland, choose the entry that includes Warsaw.");
    }

    private IEnumerable<Choice<string>> WithSavedChoice(Choice<string>[] choices, string current) =>
        current.Length == 0 ? choices :
        choices.Any(choice => choice.Value.Equals(current, StringComparison.OrdinalIgnoreCase))
            ? choices.Select(choice => choice.Value.Equals(current, StringComparison.OrdinalIgnoreCase) ? choice with { Value = current } : choice)
            : choices.Append(C(current, "Ustawienie zapisane w profilu", "Setting saved in the profile"));

    private static void SelectValue<T>(ComboBox? control, T value)
    {
        if (control is null)
            return;
        foreach (Choice<T> choice in control.Items)
        {
            if (value is string text && choice.Value is string candidate
                ? candidate.Equals(text, StringComparison.OrdinalIgnoreCase)
                : EqualityComparer<T>.Default.Equals(choice.Value, value))
            {
                control.SelectedItem = choice;
                break;
            }
        }
    }

    private Action FirstRunPageOption(Fields f)
    {
        var custom = new Fields();
        var choices = new[]
        {
            C(FirstRunMode.WindowsDefault, "Przejdź przez standardową konfigurację Windows", "Use the standard Windows first-run screens",
                "Przy pierwszym uruchomieniu samodzielnie przejdziesz przez standardowe ekrany tej edycji Windows.",
                "On first startup, you go through the standard setup screens for this Windows edition."),
            C(FirstRunMode.SkipSupported, "Pomiń obsługiwane ekrany konfiguracji", "Skip the supported setup screens",
                "Użyj dodanego konta lokalnego i pomiń ekrany konta Microsoft, Wi-Fi, rejestracji u producenta i ustawień zalecanych.",
                "Use the added local account and skip the Microsoft account, Wi-Fi, manufacturer registration and recommended settings screens."),
            C(FirstRunMode.Custom, "Wybierz, które ekrany pominąć", "Choose which setup screens to skip",
                "Poniżej pojawią się osobne opcje dla konta Microsoft, Wi-Fi, rejestracji i ustawień zalecanych.",
                "Separate controls for the Microsoft account, Wi-Fi, registration and recommended settings appear below."),
            C(FirstRunMode.Offline, "Skonfiguruj bez internetu z kontem lokalnym", "Set up without internet using a local account",
                "Użyj dodanego konta lokalnego. Pomiń ekrany sieci i konta Microsoft. Pozostałe ekrany zależą od wersji i edycji Windows.",
                "Use the added local account and skip the network and Microsoft account screens. The other screens depend on the Windows release and edition.")
        };
        bool updatingChoices = false;
        var firstRun = Explain(f.Select(L("Jak przeprowadzić p&ierwszą konfigurację Windows?", "How should &initial Windows setup run?"), profile.SelectedFirstRun, choices,
            value =>
            {
                if (!updatingChoices)
                    profile.FirstRun = value;
                custom.Table.Visible = value == FirstRunMode.Custom;
            }),
            "Dodaj lokalne konto administratora, aby udostępnić konfigurację bez internetu i gotowy zestaw pomijania ekranów.",
            "Add a local administrator account to enable setup without internet and the preset that skips setup screens.");
        Explain(custom.Check(L("Pomiń pytanie o konto Microsoft", "Skip the Microsoft account screen"),
                profile.HideOnline, value => profile.HideOnline = value),
            "Użyj z kontem lokalnym. Dostępność zależy od wersji i edycji Windows.", "Use with a local account. Availability depends on the Windows release and edition.");
        Explain(custom.Check(L("Pomiń pytanie o Wi-Fi", "Skip the Wi-Fi question"),
                profile.HideWireless, value => profile.HideWireless = value),
            "Ukrywa ekran wyboru sieci. Przydaje się z kablem Ethernet. Windows może nadal wymagać internetu.",
            "Hides the network selection screen. This is useful with an Ethernet cable. Windows may still require internet access.");
        Explain(custom.Check(L("Pomiń rejestrację u producenta komputera", "Skip device manufacturer registration"),
                profile.HideOemRegistration, value => profile.HideOemRegistration = value),
            "Pomija ekran rejestracji producenta, jeśli jest na nośniku instalacyjnym.", "Skips the manufacturer registration screen if your installation media includes one.");
        ExpressPageOption(custom);
        custom.Table.Visible = profile.SelectedFirstRun == FirstRunMode.Custom;
        f.Section(custom);
        if (profile.Target.Windows11)
        {
            Explain(f.Check(L("Udostępnij ręczną konfigurację bez internetu (BypassNRO)", "Offer manual setup without internet (BypassNRO)"),
                    profile.Has("BypassNetworkCheck"), value => profile.Advanced["BypassNetworkCheck"] = value ? "1" : "0"),
                "Dodatkowa metoda dla kompilacji Windows 11, które obsługują wpis BypassNRO. Odłącz internet, aby mogła się pojawić opcja dalszej konfiguracji bez połączenia.",
                "An additional method for Windows 11 builds that honor the BypassNRO registry setting. Disconnect from the internet to make the offline setup option available on those builds.");
            f.Note(L("Działanie BypassNRO zależy od kompilacji Windows 11.", "BypassNRO depends on the Windows 11 build."));
        }
        UpdateChoices();
        return UpdateChoices;

        void UpdateChoices()
        {
            bool hasAdministrator = profile.FirstLocalAdministrator is not null;
            if (!hasAdministrator && profile.SelectedFirstRun is FirstRunMode.SkipSupported or FirstRunMode.Offline)
                profile.FirstRun = FirstRunMode.WindowsDefault;
            var available = choices.Where(choice => hasAdministrator || choice.Value is not (FirstRunMode.SkipSupported or FirstRunMode.Offline));
            if (!available.SequenceEqual(firstRun.Items.OfType<Choice<FirstRunMode>>()))
            {
                updatingChoices = true;
                try { Fields.UpdateChoices(firstRun, profile.SelectedFirstRun, available); }
                finally { updatingChoices = false; }
            }
            custom.Table.Visible = profile.SelectedFirstRun == FirstRunMode.Custom;
        }
    }

    private void ExpressPageOption(Fields f) => Explain(f.Select(
        L("Ustawienia &zalecane i prywatność", "&Recommended settings and privacy"), profile.Express,
        [C("Interactive", "Zapytaj podczas pierwszego uruchomienia", "Ask on first startup",
            "Windows pozwoli Ci wybrać ustawienia na ekranie konfiguracji.", "Windows will let you choose the settings on the setup screen."),
         C("EnableAll", "Pomiń pytanie i włącz ustawienia zalecane", "Skip the question and turn recommended settings on",
            "Windows zastosuje zalecany zestaw. Jego zakres zależy od wersji Windows.", "Windows applies its recommended settings. The included settings depend on the release."),
         C("DisableAll", "Pomiń pytanie i wyłącz ustawienia zalecane", "Skip the question and turn recommended settings off",
            "Windows odrzuci zestaw ustawień zalecanych. Pozostałe ustawienia prywatności zależą od wersji Windows.",
            "Windows declines the recommended settings. Other privacy settings depend on the release.")], value => profile.Express = value),
        "Zdecyduj, czy wybrać ustawienia zalecane z góry, czy odpowiadać podczas konfiguracji Windows.",
        "Choose the recommended settings in advance or answer the question during Windows setup.");
}
