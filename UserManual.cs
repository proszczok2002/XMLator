using System.Net;
using System.Text;

namespace XMLator;

internal static class UserManual
{
    internal static string Save(string language, string product, string applicationFolder)
    {
        language = UiText.Languages.Any(choice => choice.Value == language) ? language : "en";
        string title = WebUtility.HtmlEncode(UiText.Get(language, "User manual"));
        string body = Read(language)
            .Replace("@@TITLE@@", title, StringComparison.Ordinal)
            .Replace("@@PRODUCT@@", WebUtility.HtmlEncode(product), StringComparison.Ordinal);
        string html = Read("layout")
            .Replace("@@LANG@@", language, StringComparison.Ordinal)
            .Replace("@@TITLE@@", title, StringComparison.Ordinal)
            .Replace("@@BODY@@", body, StringComparison.Ordinal);
        string fileName = language + ".html";
        try
        {
            return Write(Path.Combine(applicationFolder, "podrecznik"), fileName, html);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Write(Path.Combine(Path.GetTempPath(), "XMLator", "podrecznik"), fileName, html);
        }
    }

    private static string Read(string name)
    {
        using var resource = typeof(UserManual).Assembly.GetManifestResourceStream($"XMLator.Manual.{name}.html") ??
            throw new InvalidOperationException($"Missing user manual resource: {name}.");
        using var reader = new StreamReader(resource);
        return reader.ReadToEnd();
    }

    private static string Write(string folder, string fileName, string contents)
    {
        Directory.CreateDirectory(folder);
        string destination = Path.Combine(folder, fileName);
        if (File.Exists(destination) && File.ReadAllText(destination, Encoding.UTF8) == contents)
            return destination;
        string temporary = Path.Combine(folder, Path.GetRandomFileName());
        bool created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(contents);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (created)
                File.Delete(temporary);
        }
        return destination;
    }
}
