using System.Globalization;

namespace XMLator;

internal sealed class InterfacePreferences
{
    private readonly string? path;
    private readonly string windowsLanguage;

    internal InterfacePreferences(string? path = null, CultureInfo? windowsCulture = null)
    {
        this.path = path;
        windowsLanguage = UiText.FromWindows((windowsCulture ?? CultureInfo.CurrentUICulture).Name);
        if (path is null)
            return;
        try
        {
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length <= 65536)
                    Read(path);
            }
            else
                Save(createOnly: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal string Language { get; set; } = "auto";
    internal string EffectiveLanguage => Language == "auto" ? windowsLanguage : Language;
    internal AppTheme Theme { get; set; }
    internal bool AutomaticUpdates { get; set; } = true;
    internal int UpdateIntervalDays { get; set; }
    internal DateTimeOffset? LastUpdateCheck { get; set; }

    internal bool ShouldCheckForUpdates(DateTimeOffset now) => AutomaticUpdates &&
        (LastUpdateCheck is not DateTimeOffset last || last > now || now - last >= TimeSpan.FromDays(UpdateIntervalDays));

    internal bool IsConfigurationFile(string file) => path is not null &&
        Path.GetFullPath(file).Equals(path, StringComparison.OrdinalIgnoreCase);

    private void Read(string file)
    {
        string section = "";
        foreach (string entry in File.ReadLines(file))
        {
            string line = entry.Trim();
            if (line.StartsWith('['))
            {
                section = line.ToLowerInvariant();
                continue;
            }
            int equal = line.IndexOf('=');
            if (equal < 1)
                continue;
            string key = line[..equal].Trim();
            string value = line[(equal + 1)..].Trim();
            if (section == "[updates]")
            {
                switch (key.ToLowerInvariant())
                {
                    case "automatic" when bool.TryParse(value, out bool enabled):
                        AutomaticUpdates = enabled;
                        break;
                    case "intervaldays" when int.TryParse(value, out int days) && days is 0 or 1 or 7 or 30:
                        UpdateIntervalDays = days;
                        break;
                    case "lastcheckutc" when DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var checkedAt):
                        LastUpdateCheck = checkedAt.ToUniversalTime();
                        break;
                }
                continue;
            }
            if (section != "[interface]")
                continue;
            if (key.Equals("Language", StringComparison.OrdinalIgnoreCase))
                Language = UiText.PreferenceCode(value);
            if (key.Equals("Theme", StringComparison.OrdinalIgnoreCase))
                Theme = value.ToLowerInvariant() switch
                {
                    "light" => AppTheme.Light,
                    "dark" => AppTheme.Dark,
                    _ => AppTheme.System
                };
        }
    }

    internal void Save(bool createOnly = false)
    {
        if (path is null)
            return;
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllLines(temporary, ["[Interface]", $"Language={Language}", $"Theme={Theme.ToString().ToLowerInvariant()}",
                "", "[Updates]", $"Automatic={AutomaticUpdates.ToString().ToLowerInvariant()}",
                $"IntervalDays={UpdateIntervalDays}", $"LastCheckUtc={LastUpdateCheck?.ToString("O", CultureInfo.InvariantCulture)}"]);
            File.Move(temporary, path, overwrite: !createOnly);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
