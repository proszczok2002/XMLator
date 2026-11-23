using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XMLator;

internal record Target(string Name, bool Server, string[] Architectures)
{
    public bool ModernClient => Name is "Windows 10" or "Windows 11";
    public bool ClientOobeNetwork => ModernClient || Name is "Windows 8" or "Windows 8.1";
    public bool Windows11 => Name == "Windows 11";
    public bool Vista => Name.StartsWith("Windows Vista", StringComparison.Ordinal);
    public bool Server2008 => Name is "Windows Server 2008" or "Windows Server 2008 SP2" or "Windows Server 2008 R2";
    public bool RequiresLocalAccountPassword => Name is "Windows Server 2008" or "Windows Server 2008 SP2";
    public bool HyperV => Name.StartsWith("Microsoft Hyper-V Server", StringComparison.Ordinal);
    public bool ServerChannel => Name.StartsWith("Windows Server, version ", StringComparison.Ordinal);
    public bool OldMsr => Vista || Server2008 || Name is "Windows 7" or "Windows Thin PC" or "Windows 8" or "Windows 8.1" or
        "Windows Server 2012" or "Windows Server 2012 R2" or "Microsoft Hyper-V Server 2008" or "Microsoft Hyper-V Server 2008 R2" or
        "Microsoft Hyper-V Server 2012" or "Microsoft Hyper-V Server 2012 R2";
    public bool ModernPnP => ModernClient || (Server && !OldMsr);
    public bool CanUseGpt(string architecture) => Name is not ("Windows Vista" or "Windows Thin PC") &&
        (architecture != "x86" || !(Vista || Server2008 || Name == "Windows 7"));

    public static readonly Target[] All =
    [
        new("Windows 11", false, ["amd64", "arm64"]),
        new("Windows 10", false, ["amd64", "x86", "arm64"]),
        new("Windows 8.1", false, ["amd64", "x86"]),
        new("Windows 8", false, ["amd64", "x86"]),
        new("Windows 7", false, ["amd64", "x86"]),
        new("Windows Thin PC", false, ["x86"]),
        new("Windows Vista SP2", false, ["amd64", "x86"]),
        new("Windows Vista SP1", false, ["amd64", "x86"]),
        new("Windows Vista", false, ["amd64", "x86"]),
        new("Windows Server 2025", true, ["amd64"]),
        new("Windows Server, version 23H2", true, ["amd64"]),
        new("Windows Server 2022", true, ["amd64"]),
        new("Windows Server, version 20H2", true, ["amd64"]),
        new("Windows Server, version 2004", true, ["amd64"]),
        new("Windows Server, version 1909", true, ["amd64"]),
        new("Windows Server, version 1903", true, ["amd64"]),
        new("Windows Server, version 1809", true, ["amd64"]),
        new("Windows Server 2019", true, ["amd64"]),
        new("Windows Server, version 1803", true, ["amd64"]),
        new("Windows Server, version 1709", true, ["amd64"]),
        new("Windows Server 2016", true, ["amd64"]),
        new("Windows Server 2012 R2", true, ["amd64"]),
        new("Windows Server 2012", true, ["amd64"]),
        new("Windows Server 2008 R2", true, ["amd64", "ia64"]),
        new("Windows Server 2008 SP2", true, ["amd64", "x86", "ia64"]),
        new("Windows Server 2008", true, ["amd64", "x86", "ia64"]),
        new("Microsoft Hyper-V Server 2019", true, ["amd64"]),
        new("Microsoft Hyper-V Server 2016", true, ["amd64"]),
        new("Microsoft Hyper-V Server 2012 R2", true, ["amd64"]),
        new("Microsoft Hyper-V Server 2012", true, ["amd64"]),
        new("Microsoft Hyper-V Server 2008 R2", true, ["amd64"]),
        new("Microsoft Hyper-V Server 2008", true, ["amd64"])
    ];
}

internal enum ImageMode { Interactive, ExactName, Index, Edition }
internal enum KeyMode { Default, Enter, Firmware, Ask, Generic, Skip }
internal enum DiskMode { Interactive, Existing, Single, SystemAndWindows, SystemWindowsData, Available, FormatExisting, Custom }
internal enum Firmware { Gpt, Mbr }
internal enum SetupPrompt { OnError, Always, Never }
internal enum LogonMode { Manual, FirstAdministrator }
internal enum FirstRunMode { WindowsDefault, SkipSupported, Custom, Offline }
internal enum OemPhase { Copy, Specialize, SetupComplete, FirstLogon }
internal enum AppTheme { System, Light, Dark }
internal enum AppRemoval { Recommended, Extended, Keep, Custom }

internal sealed class LocalUser
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Administrator { get; set; } = true;
    public bool AskName { get; set; }
    public bool AskPassword { get; set; }
    [JsonIgnore] public string Password { get; set; } = "";

    public LocalUser Copy() => (LocalUser)MemberwiseClone();
}

internal sealed record OemFile(string Path, OemPhase Phase);
internal sealed record EmbeddedScript(string Text, string Phase, string Type);
internal sealed record CustomComponent(string Pass, string Name, string Markup);
internal sealed record DataPartition(string Label, string Letter, int SizeMiB, string Format = "NTFS");

internal sealed class SetupProfile
{
    public AppRemoval RemoveApps { get; set; } = AppRemoval.Custom;
    public bool DarkInstalledWindows { get; set; }
    public string TargetName { get; set; } = "Windows 11";
    [JsonIgnore] public Target Target => Target.All.Single(t => t.Name == TargetName);
    public string Architecture { get; set; } = "amd64";
    public List<string> ExtraArchitectures { get; set; } = [];
    public bool AcceptEula { get; set; }
    public ImageMode ImageMode { get; set; }
    public string PresetEdition { get; set; } = "";
    [JsonIgnore] public MediaEdition? SelectedEdition => EditionCatalog.For(Target, Architecture).FirstOrDefault(e => e.Id == PresetEdition);
    public string ImageName { get; set; } = "";
    public int ImageIndex { get; set; } = 1;
    public KeyMode KeyMode { get; set; }
    public string GenericEdition { get; set; } = "pro";
    [JsonIgnore] public string SetupKey { get; set; } = "";
    public bool UseSetupKeyForActivation { get; set; } = true;
    public bool UseActivationKey { get; set; }
    [JsonIgnore] public string ActivationKey { get; set; } = "";
    private Firmware firmware;
    public Firmware Firmware
    {
        get => DiskMode == DiskMode.Single ? Firmware.Mbr : firmware;
        set => firmware = value;
    }
    public DiskMode DiskMode { get; set; }
    public int DiskNumber { get; set; }
    public int PartitionNumber { get; set; } = 1;
    public int EfiMiB { get; set; } = 300;
    public int BiosMiB { get; set; } = 550;
    public bool Recovery { get; set; } = true;
    public int RecoveryMiB { get; set; } = 1024;
    public int WindowsMiB { get; set; } = 131072;
    public int DataMiB { get; set; }
    public string DataLetter { get; set; } = "D";
    public SetupPrompt DiskPrompt { get; set; }
    public SetupPrompt ImagePrompt { get; set; }
    public int CustomWindowsMiB { get; set; }
    public string WindowsLabel { get; set; } = "Windows";
    public bool SeparateBiosBoot { get; set; } = true;
    public List<DataPartition> DataPartitions { get; set; } = [];
    [JsonIgnore] public bool WipesDisk => DiskMode is DiskMode.Single or DiskMode.SystemAndWindows or DiskMode.SystemWindowsData or DiskMode.Custom;
    [JsonIgnore] public bool FormatsPartition => DiskMode == DiskMode.FormatExisting;
    [JsonIgnore] public bool NeedsDiskNumber => WipesDisk || DiskMode is DiskMode.Existing or DiskMode.FormatExisting;
    [JsonIgnore] public bool RequiresDiskConsent => WipesDisk || FormatsPartition || RequiresCustomPeConsent;
    [JsonIgnore] public bool RequiresCustomPeConsent => Target.ModernClient &&
        (Option("PE") == "Custom" || Option("PE") == "Generated" && Has("CustomDiskPart"));
    [JsonIgnore] public bool DetectsDiskFirmware => Target.ModernClient && Option("PE") == "Generated" &&
        DiskMode == DiskMode.SystemAndWindows && !Has("CustomDiskPart") && Has("DetectFirmware") &&
        SupportsLayoutFirmware(Firmware.Gpt) && SupportsLayoutFirmware(Firmware.Mbr);

    internal bool SupportsLayoutFirmware(Firmware mode) => ExtraArchitectures.Prepend(Architecture).All(
        architecture => SupportsFirmware(mode, architecture));

    internal bool SupportsFirmware(Firmware mode, string architecture) => mode == Firmware.Gpt
        ? Target.CanUseGpt(architecture)
        : architecture is not ("arm64" or "ia64") && (!Target.Windows11 || Has("BypassRequirements"));
    private string? confirmedDiskLayout;
    [JsonIgnore]
    public bool ConfirmWipe
    {
        get => confirmedDiskLayout is not null && confirmedDiskLayout == DiskLayoutIdentity();
        set => confirmedDiskLayout = value ? DiskLayoutIdentity() : null;
    }

    private string DiskLayoutIdentity() => JsonSerializer.Serialize(new
    {
        TargetName, Architecture, AdditionalArchitectures = ExtraArchitectures.Order().ToArray(),
        DiskMode, DiskNumber, PartitionNumber, Firmware, EfiMiB, BiosMiB, Recovery, RecoveryMiB, WindowsMiB, DataMiB, DataLetter,
        CustomWindowsMiB, WindowsLabel, SeparateBiosBoot, DataPartitions,
        PeOptions = new[] { "PE", "PEText", "CustomDiskPart", "DiskPartText", "TargetDisk", "TargetDiskScript",
            "DiskMinGiB", "DiskMaxGiB", "DiskAnyIndex", "AllowExistingPartitions", "DiskInterface", "DiskMedia",
            "DetectFirmware", "BypassRequirements" }.Select(key => Option(key)).ToArray()
    });

    public bool SetLocale { get; set; }
    public string UiLocale { get; set; } = "pl-PL";
    public string UserLocale { get; set; } = "pl-PL";
    public string SystemLocale { get; set; } = "pl-PL";
    public string Keyboard { get; set; } = "0415:00000415";
    public string GeoId { get; set; } = "";
    public string TimeZone { get; set; } = "";
    public string ComputerName { get; set; } = "";
    public string ComputerNameMode { get; set; } = "default";
    public string ComputerNameScript { get; set; } = "";
    public string RegisteredOwner { get; set; } = "";
    public string RegisteredOrganization { get; set; } = "";
    public string Workgroup { get; set; } = "";

    public bool CreateUsers { get; set; }
    public bool SetServerAdministratorPassword { get; set; }
    public List<LocalUser> Users { get; set; } = [];
    [JsonIgnore] public LocalUser? FirstLocalAdministrator => CreateUsers ? Users.FirstOrDefault(user => user.Administrator) : null;
    public LogonMode Logon { get; set; }
    public bool ObscurePasswords { get; set; } = true;
    public FirstRunMode? FirstRun { get; set; }
    public bool HideOnline { get; set; }
    public bool HideWireless { get; set; }
    public bool HideOemRegistration { get; set; }
    public string Express { get; set; } = "Interactive";
    [JsonIgnore] public FirstRunMode SelectedFirstRun => FirstRun ??
        (HideOnline || HideWireless || HideOemRegistration || Express is "EnableAll" or "DisableAll"
            ? FirstRunMode.Custom : FirstRunMode.WindowsDefault);
    [JsonIgnore] public bool SkipWifi => Target.ModernClient && Option("WifiMode") == "Skip";

    public string WinPeDrivers { get; set; } = "";
    public string WinPePackages { get; set; } = "";
    public string InstalledDrivers { get; set; } = "";
    public string SystemFiles { get; set; } = "";
    public List<OemFile> OemFiles { get; set; } = [];
    public List<EmbeddedScript> EmbeddedScripts { get; set; } = [];
    public List<CustomComponent> Components { get; set; } = [];
    public Dictionary<string, string> Advanced { get; set; } = new(StringComparer.Ordinal);
    [JsonIgnore] public Dictionary<string, string> SessionOptions { get; } = new(StringComparer.Ordinal);

    public string Option(string name, string fallback = "") =>
        SessionOptions.GetValueOrDefault(name) ?? Advanced.GetValueOrDefault(name) ?? fallback;

    public bool Has(string name) => Option(name) == "1";
    public int Number(string name, int fallback) =>
        int.Parse(Option(name, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);

    [JsonIgnore] public bool HasMediaFiles => OemFiles.Count > 0 ||
        new[] { WinPeDrivers, WinPePackages, InstalledDrivers, SystemFiles }.Any(s => s.Length > 0);
    [JsonIgnore] public bool HasSeparateData => DiskMode == DiskMode.SystemWindowsData;
}

internal static class IniProfile
{
    private static readonly JsonSerializerOptions Json = new()
    {
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static void Save(string path, SetupProfile profile)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(profile, Json));
        var contents = new StringBuilder("[XMLator]\r\nVersion=1\r\n\r\n[Setup]\r\n");
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            string value = entry.Name == nameof(SetupProfile.Users)
                ? JsonSerializer.Serialize(profile.Users.Select(ForReuse), Json)
                : entry.Value.GetRawText();
            contents.Append(entry.Name).Append('=').Append(value).Append("\r\n");
        }

        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, contents.ToString(), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static LocalUser ForReuse(LocalUser current)
    {
        var user = current.Copy();
        if (user.AskName)
            user.Name = user.DisplayName = "";
        if (user.Password.Length > 0)
            user.AskPassword = true;
        return user;
    }

    public static SetupProfile Load(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024)
            throw new InvalidDataException("INI > 16 MiB.");

        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        string section = "";
        bool compatible = false;
        bool replaceBuiltInAccount = false;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }
            int separator = line.IndexOf('=');
            if (separator < 1)
                throw new InvalidDataException("INI: " + line);
            string name = line[..separator].Trim();
            string value = line[(separator + 1)..];
            if (section == "XMLator" && name == "Version")
                compatible = value == "1";
            if (section != "Setup")
                continue;
            if (name == "SetAdministratorPassword")
            {
                replaceBuiltInAccount |= value.Trim() == "true";
                continue;
            }
            if (name == "Logon" && value.Trim() == "\"BuiltInAdministrator\"")
            {
                replaceBuiltInAccount = true;
                value = "\"FirstAdministrator\"";
            }
            writer.WritePropertyName(name);
            writer.WriteRawValue(value);
        }
        writer.WriteEndObject();
        writer.Flush();

        if (!compatible)
            throw new InvalidDataException("XMLator INI Version=1.");

        SetupProfile profile = JsonSerializer.Deserialize<SetupProfile>(buffer.ToArray(), Json)
            ?? throw new InvalidDataException("XMLator INI.");
        if (!Target.All.Any(t => t.Name == profile.TargetName) ||
            profile.Users.Any(x => x is null) || profile.OemFiles.Any(x => x is null) ||
            profile.EmbeddedScripts.Any(x => x is null) || profile.Components.Any(x => x is null) ||
            profile.DataPartitions.Any(x => x is null))
            throw new InvalidDataException("Nieprawidłowe dane profilu / Invalid profile data.");
        profile.Advanced.Remove("WifiPassword");
        profile.Advanced.Remove("WifiXml");
        if (profile.Target.ModernClient && profile.SkipWifi)
        {
            if (profile.SelectedFirstRun is not (FirstRunMode.SkipSupported or FirstRunMode.Offline))
            {
                if (profile.SelectedFirstRun == FirstRunMode.WindowsDefault)
                {
                    profile.HideOnline = false;
                    profile.HideOemRegistration = false;
                    profile.Express = "Interactive";
                }
                profile.FirstRun = FirstRunMode.Custom;
                profile.HideWireless = true;
            }
            profile.Advanced.Remove("WifiMode");
        }
        if (replaceBuiltInAccount)
        {
            profile.CreateUsers = true;
            if (!profile.Users.Any(user => user.Administrator))
                profile.Users.Add(new LocalUser { AskName = true, AskPassword = true });
        }
        return profile;
    }
}
