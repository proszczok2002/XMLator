using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using XMLator;

internal static class UsbLiveAudit
{
    internal static async Task Run(string worker, string iso, string evidence, string? previousOverlay = null, string? driverFolder = null)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("The physical USB audit needs Windows administrator rights.");
        Directory.CreateDirectory(evidence);
        using var logStream = new FileStream(Path.Combine(evidence, "result.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var errorStream = new FileStream(Path.Combine(evidence, "errors.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var log = new StreamWriter(logStream) { AutoFlush = true };
        using var errors = new StreamWriter(errorStream) { AutoFlush = true };
        Console.SetOut(log);
        Console.SetError(errors);
        const string validate = """
            $ErrorActionPreference = 'Stop'
            $disk = Get-Disk -Number 2
            if ($disk.BusType -ne 'USB' -or $disk.FriendlyName -ne 'KIOXIA TransMemory' -or
                $disk.SerialNumber.Trim() -ne 'BE076BE0B3F1' -or $disk.Size -ne 30979129344 -or
                $disk.IsBoot -or $disk.IsSystem) {
                throw 'The specified KIOXIA USB drive is unavailable.'
            }
            if ($env:XMLATOR_AUDIT_PREVIOUS) {
                if ($disk.NumberOfPartitions -ne 1 -or $disk.PartitionStyle -ne 'GPT') { throw 'The previous test layout is missing.' }
                $volume = Get-Partition -DiskNumber $disk.Number | Get-Volume
                if (-not $volume.DriveLetter -or $volume.FileSystemLabel -ne '11_26H2_10_') { throw 'The previous test volume is missing.' }
                [string]$volume.DriveLetter + ':\'
            }
            elseif ($disk.NumberOfPartitions -ne 0) { throw 'Expected the blank KIOXIA test drive.' }
            """;
        string currentDrive;
        using (var check = RunPowerShell(validate, options => options.Environment["XMLATOR_AUDIT_PREVIOUS"] = previousOverlay ?? ""))
        {
            var details = check.StandardError.ReadToEndAsync();
            var output = check.StandardOutput.ReadToEndAsync();
            await check.WaitForExitAsync();
            if (check.ExitCode != 0)
            {
                errors.WriteLine((await details).Trim());
                Environment.ExitCode = 1;
                return;
            }
            currentDrive = (await output).Trim();
        }
        var fixture = (await UsbMedia.ListDisks("en")).Single(device => device.Number == 2 &&
            device.Name.Trim().Equals("KIOXIA TransMemory", StringComparison.OrdinalIgnoreCase) && device.Size == 30_979_129_344L);
        var workspace = Directory.CreateTempSubdirectory("XMLator-physical-audit-");
        try { await WriteAndVerify(); }
        finally { workspace.Delete(recursive: true); }

        async Task WriteAndVerify()
        {
            string media = Path.Combine(workspace.FullName, "media");
            string label = OpticalImage.Extract(iso, media, fixture.Size, percent =>
            {
                if (percent % 20 == 0)
                    log.WriteLine($"ISO extraction {percent}%");
            });
            string overlay = Path.Combine(evidence, "usb-overlay");
            Directory.CreateDirectory(overlay);
            string script = Path.Combine(evidence, "OemProbe.cmd");
            File.WriteAllText(script, "@echo off\r\nrem XMLator USB copy test\r\n");
            if (previousOverlay is not null)
            {
                VerifyPreviousTestMedia(currentDrive, media, previousOverlay);
                Console.WriteLine("Every file on the previous KIOXIA test volume matched the fixture or ISO.");
            }
            foreach (var mode in previousOverlay is null ? new[] { Firmware.Mbr, Firmware.Gpt } : [Firmware.Gpt])
            {
                var devices = await UsbMedia.ListDisks("en");
                var disk = devices.Single(device => device.Number == fixture.Number && device.Path == fixture.Path &&
                    device.UniqueId == fixture.UniqueId && device.Size == fixture.Size);
                Console.WriteLine($"{mode}: verified {disk.Label("en")} ({disk.Size} bytes)");
                var pack = Path.Combine(overlay, mode.ToString());
                Directory.CreateDirectory(pack);
                var profile = new SetupProfile { AcceptEula = true, ImageMode = ImageMode.Edition, PresetEdition = "Professional",
                    CreateUsers = true, FirstRun = FirstRunMode.SkipSupported, KeyMode = KeyMode.Skip,
                    WinPeDrivers = driverFolder ?? "", InstalledDrivers = driverFolder ?? "" };
                if (mode == Firmware.Mbr) profile.Advanced["BypassRequirements"] = "true";
                profile.Users.Add(new LocalUser { Name = "XmlatorTest" });
                foreach (var phase in Enum.GetValues<OemPhase>())
                    profile.OemFiles.Add(new OemFile(script, phase));
                profile.Components.Add(new CustomComponent("specialize", "Microsoft-Windows-Shell-Setup", "<RegisteredOwner>XMLatorAudit</RegisteredOwner>"));
                profile.EmbeddedScripts.Add(new EmbeddedScript("Write-Output 'XMLator audit'", "System", "Ps1"));
                string root = MediaPackage.StageUsb(pack, AnswerFile.Build(profile), profile);
                var job = new UsbJob(disk, iso, media, label, root, mode, "x64", false);
                try
                {
                    bool finished = await UsbWorker.Prepare(job, () =>
                    {
                        Console.WriteLine($"{mode}: released XMLator worker returned READY after media preflight");
                        return Task.FromResult(true);
                    }, worker, progress =>
                    {
                        if (progress.Percent is not int percent || percent % 10 == 0)
                            log.WriteLine($"{mode}: {progress.Stage} {progress.Percent}".TrimEnd());
                    });
                    if (!finished)
                        throw new IOException("USB write canceled.");
                    Console.WriteLine($"{mode}: released XMLator worker completed format, copy and answer-file hash verification.");
                    await ReadBack(disk.Number, root, mode);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"{mode}: {e}");
                    Environment.ExitCode = 1;
                }
            }
        }
    }

    private static void VerifyPreviousTestMedia(string drive, string media, string overlay)
    {
        if (drive.Length != 3 || !char.IsAsciiLetter(drive[0]) || drive[1..] != ":\\")
            throw new IOException("Could not verify the existing test volume.");
        var originals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in new[] { media, overlay })
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                originals[Path.GetRelativePath(root, file)] = file;
        var actual = Directory.EnumerateFileSystemEntries(drive)
            .Where(path => !Path.GetFileName(path).Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories) : [path]).ToArray();
        if (!actual.Select(file => Path.GetRelativePath(drive, file)).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(originals.Keys))
            throw new IOException("The existing volume contains files that are different from the previous test.");
        foreach (string file in actual)
        {
            using var previous = File.OpenRead(file);
            using var expected = File.OpenRead(originals[Path.GetRelativePath(drive, file)]);
            if (previous.Length != expected.Length || !SHA256.HashData(previous).SequenceEqual(SHA256.HashData(expected)))
                throw new IOException("The previous test file was changed: " + Path.GetRelativePath(drive, file));
        }
    }

    private static async Task ReadBack(int number, string overlay, Firmware mode)
    {
        const string readBack = """
            $ErrorActionPreference = 'Stop'
            $disk = Get-Disk -Number $env:XMLATOR_AUDIT_DISK
            if ($disk.BusType -ne 'USB' -or $disk.FriendlyName -notmatch 'KIOXIA TransMemory' -or $disk.Size -ne 30979129344) {
                throw 'Unexpected disk on readback.'
            }
            $p = @(Get-Partition -DiskNumber $disk.Number)
            if ($disk.PartitionStyle -ne $env:XMLATOR_AUDIT_MODE -or $p.Count -ne 1) { throw 'Unexpected partition layout.' }
            $volume = $p[0] | Get-Volume
            $gpt = $disk.PartitionStyle -eq 'GPT'
            if ($gpt) {
                if ($volume.FileSystem -ne 'FAT32' -or $p[0].GptType -ne '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}') { throw 'Wrong UEFI partition type.' }
            } elseif ($volume.FileSystem -ne 'NTFS' -or -not $p[0].IsActive) { throw 'Wrong BIOS partition type.' }
            $path = if ($volume.DriveLetter) { [string]$volume.DriveLetter + ':\' } else { $volume.Path }
            $expected = Get-ChildItem -LiteralPath $env:XMLATOR_AUDIT_FILES -File -Recurse -Force
            foreach ($file in $expected) {
                $name = $file.FullName.Substring($env:XMLATOR_AUDIT_FILES.Length).TrimStart('\')
                $other = [IO.Path]::Combine($path, $name)
                $stream = [IO.File]::OpenRead($other)
                try { $hash = [Convert]::ToBase64String(([Security.Cryptography.SHA256]::Create()).ComputeHash($stream)) }
                finally { $stream.Dispose() }
                $data = [IO.File]::ReadAllBytes($file.FullName)
                if ($hash -ne [Convert]::ToBase64String(([Security.Cryptography.SHA256]::Create()).ComputeHash($data))) {
                    throw "Readback hash mismatch: $name"
                }
            }
            foreach ($file in 'setup.exe','sources\boot.wim','sources\install.swm','sources\install2.swm','sources\install3.swm',$(if ($gpt) {'efi\boot\bootx64.efi'} else {'bootmgr'})) {
                if (-not [IO.File]::Exists([IO.Path]::Combine($path, $file))) { throw "Missing on USB: $file" }
            }
            "$($disk.PartitionStyle), $($volume.FileSystem): answer and OEM hashes matched, installer and boot files present."
            """;
        using var process = RunPowerShell(readBack, start =>
        {
            start.Environment["XMLATOR_AUDIT_DISK"] = number.ToString();
            start.Environment["XMLATOR_AUDIT_FILES"] = overlay;
            start.Environment["XMLATOR_AUDIT_MODE"] = mode.ToString();
        });
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new IOException(await error);
        Console.WriteLine((await output).Trim());
    }

    private static Process RunPowerShell(string command, Action<ProcessStartInfo>? configure = null)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", command })
            start.ArgumentList.Add(argument);
        configure?.Invoke(start);
        return Process.Start(start) ?? throw new IOException("Could not verify USB content.");
    }
}
