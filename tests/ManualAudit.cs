using System.Net;
using XMLator;

internal static class ManualAudit
{
    internal static void Export(string applicationFolder)
    {
        Directory.CreateDirectory(applicationFolder);
        foreach (var language in UiText.Languages)
        {
            string path = UserManual.Save(language.Value, "XMLator old edition", applicationFolder);
            string current = UserManual.Save(language.Value, Wizard.ProductCaption, applicationFolder);
            string html = File.ReadAllText(current);
            string title = WebUtility.HtmlEncode(UiText.Get(language.Value, "User manual"));
            if (path != current || Path.GetFileName(current) != language.Value + ".html" ||
                !html.Contains($"<html lang=\"{language.Value}\">", StringComparison.Ordinal) ||
                !html.Contains($"<h1>{title}</h1>", StringComparison.Ordinal) ||
                !html.Contains(Wizard.ProductCaption, StringComparison.Ordinal) ||
                html.Contains("XMLator old edition", StringComparison.Ordinal) ||
                html.Contains("@@", StringComparison.Ordinal))
                throw new InvalidOperationException($"Incorrect or stale manual for {language.Value}.");
            Console.WriteLine($"{language.Value}: localized title, language, version and atomic replacement verified.");
        }
        string fallback = UserManual.Save("unsupported", Wizard.ProductCaption, applicationFolder);
        if (Path.GetFileName(fallback) != "en.html")
            throw new InvalidOperationException("The manual did not fall back to English.");
    }
}
