using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Udf;
using System.Xml;

namespace XMLator;

internal static class OpticalImage
{
    private static readonly string[] InstallImages = [@"sources\install.wim", @"sources\install.esd", @"sources\install.swm"];

    internal static void ValidateWindows(string iso, SetupProfile profile)
    {
        using var source = File.OpenRead(iso);
        using var image = Open(source);
        WindowsImageCatalog.Validate(profile, ReadWindowsImages(image));
    }

    internal static string Extract(string iso, string destination, long capacity, Action<int>? progress = null, SetupProfile? profile = null)
    {
        using var source = File.OpenRead(iso);
        bool udf = UdfReader.Detect(source);
        using DiscFileSystem image = Open(source, udf);
        string install = FindInstallImage(image);
        if (profile is not null)
            WindowsImageCatalog.Validate(profile, ReadWindowsImages(image, install));

        var directories = new List<string>();
        var files = new List<(string Source, string Destination, long Length)>();
        var pending = new Queue<(string Path, int Depth)>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue(("\\", 0));
        while (pending.TryDequeue(out var folder))
        {
            foreach (string entry in image.GetFileSystemEntries(folder.Path))
            {
                bool isDirectory = image.DirectoryExists(entry);
                // ISO 9660 can represent a file without an extension as "filename.".
                string entryPath = SafePath(!udf && !isDirectory && entry.EndsWith('.') ? entry[..^1] : entry);
                if (!isDirectory)
                {
                    files.Add((entry, entryPath, image.GetFileLength(entry)));
                    continue;
                }
                if (folder.Depth == 64)
                    throw new InvalidDataException("The ISO directory structure is too deeply nested.");
                directories.Add(entryPath);
                pending.Enqueue((entry, folder.Depth + 1));
            }
        }
        long total = files.Sum(file => file.Length);
        if (total > capacity)
            throw new SetupException("Pliki instalatora są większe niż wybrany pendrive.", "The installer files exceed the size of the selected USB drive.");
        if (total > new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace)
            throw new SetupException("Za mało miejsca na wypakowanie ISO w folderze tymczasowym Windows.",
                "There is not enough free space in the Windows temporary folder to extract the ISO.");

        Directory.CreateDirectory(destination);
        foreach (string directory in directories)
            Directory.CreateDirectory(directory);

        var buffer = new byte[1024 * 1024];
        long copied = 0;
        int previous = 0;
        progress?.Invoke(0);
        foreach (var file in files)
        {
            using (var input = image.OpenFile(file.Source, FileMode.Open, FileAccess.Read))
            using (var output = new FileStream(file.Destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    output.Write(buffer, 0, read);
                    copied += read;
                    int percent = Math.Min(99, (int)(100.0 * copied / total));
                    if (percent > previous)
                    {
                        progress?.Invoke(percent);
                        previous = percent;
                    }
                }
                if (output.Length != file.Length)
                    throw new IOException("The ISO file is incomplete: " + file.Source);
            }
            File.SetLastWriteTimeUtc(file.Destination, image.GetLastWriteTimeUtc(file.Source));
        }
        progress?.Invoke(100);
        return image.VolumeLabel.TrimEnd('\0', ' ');

        string SafePath(string entry)
        {
            string path = DestinationPath(destination, entry);
            if (paths.Count >= 100_000 || !paths.Add(path))
                throw new InvalidDataException("The ISO has too many entries or contains duplicate Windows paths.");
            return path;
        }
    }

    private static DiscFileSystem Open(Stream source, bool? udf = null) => (udf ?? UdfReader.Detect(source))
        ? new UdfReader(source) : new CDReader(source, joliet: true, hideVersions: true);

    private static string FindInstallImage(DiscFileSystem image)
    {
        string? install = InstallImages.FirstOrDefault(image.FileExists);
        if (install is null || !image.FileExists(@"sources\boot.wim") || !image.FileExists("setup.exe"))
            throw new SetupException("Wybierz instalacyjny obraz ISO Windows.", "Choose a Windows installation ISO image.");
        return install;
    }

    private static WindowsImage[] ReadWindowsImages(DiscFileSystem image, string? install = null)
    {
        install ??= FindInstallImage(image);
        try
        {
            using var stream = image.OpenFile(install, FileMode.Open, FileAccess.Read);
            return WindowsImageCatalog.Read(stream);
        }
        catch (Exception error) when (error is XmlException or IOException or InvalidDataException or FormatException or OverflowException)
        {
            throw SetupException.Format(
                $"Nie udało się odczytać katalogu edycji z pliku {Path.GetFileName(install)} w ISO. Wybierz sprawny obraz instalacyjny Windows.",
                $"Could not read the image catalog from {Path.GetFileName(install)} in the ISO. Choose a working Windows installation image.");
        }
    }

    private static string DestinationPath(string root, string entry)
    {
        string relative = entry.TrimStart('\\');
        foreach (string part in relative.Split('\\'))
        {
            string name = part.Split('.')[0].TrimEnd().ToUpperInvariant();
            if (part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) &&
                    "0123456789¹²³".Contains(name[3])))
                throw new InvalidDataException("The ISO contains an unsupported Windows path: " + entry);
        }
        return Path.Combine(root, relative);
    }
}
