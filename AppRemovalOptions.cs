namespace XMLator;

internal static class AppRemovalOptions
{
    private static readonly HashSet<string> Recommended = new([
        "Bing Search", "Clipchamp", "Copilot", "Cortana", "Dev Home", "Family", "Feedback Hub", "Game Assist",
        "Get Help", "Mail and Calendar", "Maps", "Mixed Reality", "Movies & TV", "News", "OneNote", "Outlook for Windows",
        "People", "Power Automate", "Skype", "Solitaire Collection", "Teams", "Tips", "To Do", "Wallet", "Weather", "Xbox Apps",
        "Your Phone / Phone Link", "3D Viewer", "Paint 3D"
    ], StringComparer.Ordinal);

    private static readonly HashSet<string> Extended = new([
        "Camera", "Clock", "Photos", "Sticky Notes", "Voice Recorder", "Windows Media Player (modern)"
    ], StringComparer.Ordinal);

    public static bool InPreset(string name, AppRemoval mode) => mode switch
    {
        AppRemoval.Recommended => Recommended.Contains(name),
        AppRemoval.Extended => Recommended.Contains(name) || Extended.Contains(name),
        _ => false
    };

    public static void Apply(SetupProfile profile, AppRemoval mode)
    {
        profile.RemoveApps = mode;
        if (mode == AppRemoval.Custom)
            return;
        foreach (var app in ModernOptions.Engine.Bloatwares.Values)
            profile.Advanced["App." + app.Id] = InPreset(app.DisplayName, mode) ? "1" : "0";
    }
}
