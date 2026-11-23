using System.Text;
using System.Xml.Linq;

namespace XMLator;

internal static class MediaPackage
{
    private static readonly UTF8Encoding CommandEncoding = new(false);
    private const string Log = @"%SystemRoot%\Temp\XMLator-Setup.log";
    private static readonly EnumerationOptions Traverse = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false
    };

    private static string[] SourceFolders(SetupProfile profile) =>
        [profile.WinPeDrivers, profile.WinPePackages, profile.InstalledDrivers, profile.SystemFiles];

    internal static void Validate(SetupProfile profile)
    {
        foreach (string source in SourceFolders(profile).Where(s => s.Length > 0))
        {
            if (!Directory.Exists(source))
                throw SetupException.Format($"Brak folderu źródłowego „{source}”.", $"Source folder “{source}” is missing.");
        }
        if (profile.WinPePackages.Length > 0 && (!profile.Target.ModernClient || profile.Option("PE") != "Generated"))
            throw new SetupException("Pakiety aktualizacji WinPE wymagają trybu instalacji DISM.", "WinPE update packages require DISM installation mode.");
        if (profile.WinPePackages.Length > 0 && !Directory.EnumerateFiles(profile.WinPePackages, "*", Traverse)
            .Any(file => Path.GetExtension(file).ToLowerInvariant() is ".cab" or ".msu"))
            throw new SetupException("We wskazanym folderze pakietów brak pliku CAB lub MSU.", "No CAB or MSU was found in the selected package folder.");
        foreach (string source in new[] { profile.WinPeDrivers, profile.InstalledDrivers }.Where(s => s.Length > 0))
        {
            if (!Directory.EnumerateFiles(source, "*.inf", Traverse).Any())
                throw SetupException.Format($"W folderze sterowników „{source}” brak pliku INF.", $"No INF file was found in driver folder “{source}”.");
        }
        foreach (var file in profile.OemFiles)
        {
            if (!File.Exists(file.Path))
                throw SetupException.Format($"Brak pliku „{file.Path}”.", $"File “{file.Path}” is missing.");
            if (file.Phase != OemPhase.Copy)
                _ = ScriptCommand(Path.GetExtension(file.Path), "script");
        }
    }

    public static string? Export(string xmlPath, string? packageParent, XDocument answer, SetupProfile? profile)
    {
        if (!Path.GetExtension(xmlPath).Equals(".xml", StringComparison.OrdinalIgnoreCase))
            throw new SetupException("Plik odpowiedzi musi mieć rozszerzenie .xml.", "The answer file must use the .xml extension.");
        if (profile is { HasMediaFiles: true } && packageParent is null)
            throw new SetupException("Wybierz folder na dodatkowe pliki instalacji.", "Choose where to save the installation files.");
        if (profile is { HasMediaFiles: true } && InsideSourceFolder(xmlPath, profile))
            throw new SetupException("Zapisz XML poza folderami z plikami źródłowymi, aby nie trafił do ich kopii.",
                "Save the XML outside the source folders so it does not get copied with their contents.");
        string staged = xmlPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            AnswerFile.Save(answer, staged);
            string? package = packageParent is not null && profile is { HasMediaFiles: true } ? Create(packageParent, answer, profile) : null;
            try
            {
                File.Move(staged, xmlPath, overwrite: true);
            }
            catch (Exception e) when (package is not null && e is IOException or UnauthorizedAccessException)
            {
                throw SetupException.Format($"Struktura i jej XML zostały zapisane w „{package}”. Osobnego pliku nie udało się zapisać. {e.Message}",
                    $"The package and its XML were saved to “{package}”. The separate XML could not be saved. {e.Message}");
            }
            return package;
        }
        finally
        {
            File.Delete(staged);
        }
    }

    internal static string StageUsb(string folder, XDocument answer, SetupProfile profile)
    {
        if (profile.HasMediaFiles)
            return Create(folder, answer, profile);
        AnswerFile.Save(answer, Path.Combine(folder, AnswerFile.DefaultName));
        return folder;
    }

    private static string Create(string parent, XDocument answer, SetupProfile profile)
    {
        string folderName = "XMLator-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string output = Path.Combine(parent, folderName);
        for (int suffix = 2; Directory.Exists(output) || File.Exists(output); suffix++)
            output = Path.Combine(parent, $"{folderName}-{suffix}");
        string staging = Path.Combine(parent, ".xmlator-" + Guid.NewGuid().ToString("N"));
        if (InsideSourceFolder(staging, profile))
            throw new SetupException("Wybierz miejsce docelowe poza folderami źródłowymi.", "Choose an output location outside the source folders.");
        Directory.CreateDirectory(staging);
        try
        {
            AnswerFile.Save(answer, Path.Combine(staging, AnswerFile.DefaultName));
            if (profile.HasMediaFiles)
                WriteOem(staging, profile);
            Directory.Move(staging, output);
            return output;
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    private static bool InsideSourceFolder(string path, SetupProfile profile)
    {
        string resolved = Path.GetFullPath(path);
        return SourceFolders(profile).Where(source => source.Length > 0)
            .Select(source => Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)))
            .Any(source => resolved.StartsWith(Path.EndsInDirectorySeparator(source) ? source : source + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
    }

    private static void WriteOem(string folder, SetupProfile profile)
    {
        string oem = profile.Target.ModernClient && profile.Option("PE") == "Generated"
            ? Path.Combine(folder, "$OEM$") : Path.Combine(folder, "sources", "$OEM$");
        string driveRoot = Path.Combine(oem, "$1");
        string setupScripts = Path.Combine(oem, "$$", "Setup", "Scripts");
        string scriptFiles = Path.Combine(setupScripts, "XMLator");
        var commands = new Dictionary<OemPhase, List<string>>
        {
            [OemPhase.Specialize] = [], [OemPhase.SetupComplete] = [], [OemPhase.FirstLogon] = []
        };
        if (profile.WinPeDrivers.Length > 0)
            CopyTree(profile.WinPeDrivers, Path.Combine(folder, "$WinPEDriver$"));
        if (profile.WinPePackages.Length > 0)
            CopyTree(profile.WinPePackages, Path.Combine(folder, "$WinPEPackage$"));
        if (profile.SystemFiles.Length > 0)
            CopyTree(profile.SystemFiles, driveRoot);
        if (profile.InstalledDrivers.Length > 0)
        {
            CopyTree(profile.InstalledDrivers, Path.Combine(driveRoot, "XMLator", "Drivers"));
            string log = $""" >>"{Log}" 2>&1""";
            string modern = """pnputil.exe /add-driver "%SystemDrive%\XMLator\Drivers\*.inf" /subdirs /install""" + log;
            string legacy = """for /r "%SystemDrive%\XMLator\Drivers" %%I in (*.inf) do pnputil.exe -i -a "%%~fI" """ + log;
            var specialize = commands[OemPhase.Specialize];
            if (profile.Target.Name == "Windows 10")
                specialize.AddRange([
                    """pnputil.exe /? 2>nul | "%SystemRoot%\System32\findstr.exe" /i /l /c:"/add-driver" >nul""",
                    "if errorlevel 1 (", "    " + legacy, ") else (", "    " + modern, ")"
                ]);
            else
                specialize.Add(profile.Target.ModernPnP ? modern : legacy);
        }
        if (!profile.Target.ModernClient && profile.Logon == LogonMode.FirstAdministrator && profile.OemFiles.Any(f => f.Phase == OemPhase.FirstLogon))
            commands[OemPhase.FirstLogon].Add(AnswerFile.StopAutomaticLogon);
        for (int i = 0; i < profile.OemFiles.Count; i++)
        {
            var file = profile.OemFiles[i];
            if (file.Phase == OemPhase.Copy)
            {
                Copy(file.Path, Path.Combine(driveRoot, "XMLator", "Files", Path.GetFileName(file.Path)));
                continue;
            }
            string extension = Path.GetExtension(file.Path).ToLowerInvariant();
            string name = $"{i + 1:D3}{extension}";
            Copy(file.Path, Path.Combine(scriptFiles, name));
            string command = ScriptCommand(extension, $@"%~dp0XMLator\{name}");
            commands[file.Phase].Add(command + $""" >>"{Log}" 2>&1""");
        }
        foreach (var (phase, rows) in commands.Where(entry => entry.Value.Count > 0))
        {
            string name = phase == OemPhase.SetupComplete ? "SetupComplete.cmd" : $"XMLator-{phase}.cmd";
            Directory.CreateDirectory(setupScripts);
            File.WriteAllLines(Path.Combine(setupScripts, name),
                ["@echo off", "setlocal DisableDelayedExpansion", .. rows, "exit /b 0"], CommandEncoding);
        }
    }

    private static string ScriptCommand(string extension, string path) => extension.ToLowerInvariant() switch
    {
        ".cmd" or ".bat" => $"""call "{path}" """,
        ".ps1" => $"""powershell.exe -NoProfile -ExecutionPolicy Bypass -File "{path}" """,
        ".vbs" or ".js" => $"""cscript.exe //nologo "{path}" """,
        ".reg" => $"""reg.exe import "{path}" """,
        ".exe" => $""""{path}" """",
        _ => throw new SetupException("Wybierz skrypt CMD, BAT, PS1, VBS, JS, REG lub EXE.", "Select a CMD, BAT, PS1, VBS, JS, REG or EXE script.")
    };

    private static void Copy(string file, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(file, destination, overwrite: false);
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", Traverse))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.EnumerateFiles(source, "*", Traverse))
            Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
    }
}
