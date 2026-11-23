$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$pl = $env:XMLATOR_USB_LANGUAGE -eq 'pl'
$messages = if ($env:XMLATOR_USB_MESSAGES) { ConvertFrom-Json -InputObject $env:XMLATOR_USB_MESSAGES }
$fatLimit = 4GB - 1
$system = $env:XMLATOR_USB_SYSTEM

function Fail($polish, $english, $value) {
    $text = $english
    if ($pl) { $text = $polish }
    elseif ($messages -and $messages.PSObject.Properties[$english]) { $text = $messages.PSObject.Properties[$english].Value }
    if ($PSBoundParameters.ContainsKey('value')) { $text = $text -f $value }
    throw $text
}

function Send-Line($line) {
    [Console]::WriteLine($line)
    [Console]::Out.Flush()
}

function CanUse($disk) {
    $disk.BusType -eq 'USB' -and -not $disk.IsBoot -and -not $disk.IsSystem -and
    -not $disk.IsOffline -and -not $disk.IsReadOnly -and $disk.Size -gt 0 -and
    -not [string]::IsNullOrWhiteSpace($disk.Path) -and -not [string]::IsNullOrWhiteSpace($disk.UniqueId)
}

function Selected-Disk($requested) {
    $disk = Get-Disk -Number $requested.Number
    if (-not (CanUse $disk) -or $disk.Size -ne $requested.Size -or
        $disk.Path -ne $requested.Path -or $disk.UniqueId -ne $requested.UniqueId) {
        Fail 'Wybrany pendrive odłączono lub zmieniła się jego identyfikacja.' 'The selected USB drive was removed or its identity changed.'
    }
    $disk
}

function Assert-SourcesOutside($disk, $paths) {
    $partitions = @()
    if ($disk.NumberOfPartitions -gt 0) { $partitions = @(Get-Partition -DiskNumber $disk.Number) }
    foreach ($path in $paths) {
        $source = Get-Volume -FilePath $path
        if (-not $source -or [string]::IsNullOrWhiteSpace($source.UniqueId)) {
            Fail 'Przenieś ten plik na dysk lokalny: {0}' 'Move this file to a local disk: {0}' $path
        }
        if ($partitions.AccessPaths -contains $source.UniqueId -or
            ($source.DriveLetter -and ($partitions.DriveLetter -contains $source.DriveLetter))) {
            Fail 'Ten plik znajduje się na wybranym pendrivie. Przenieś go przed formatowaniem: {0}' 'Move this file off the selected USB drive before formatting: {0}' $path
        }
    }
}

function Invoke-Robocopy($from, $to, $extra) {
    $robocopy = Join-Path $system 'robocopy.exe'
    $output = & $robocopy $from $to /E /COPY:DT /DCOPY:DT /R:1 /W:1 /XJ /NFL /NDL /NJH /NJS /NP @extra
    if ($LASTEXITCODE -ge 8) {
        Fail 'Kopiowanie zakończyło się błędem. {0}' 'Copy failed. {0}' ($output -join ' ')
    }
}

function Get-MediaLabel([string]$name, [bool]$fat) {
    $name = ($name -replace '[\x00-\x1f\\/:*?"<>|]', '_').TrimEnd()
    if (-not $fat) {
        if ($name.Length -gt 32) {
            $name = $name.Substring(0, 32)
            if ([char]::IsHighSurrogate($name[31])) { $name = $name.Substring(0, 31) }
        }
        return $name.TrimEnd()
    }
    $name = ($name -replace '[+,.;=\[\]]', '_').ToUpperInvariant()
    $encoding = [Text.Encoding]::GetEncoding([Globalization.CultureInfo]::CurrentCulture.TextInfo.OEMCodePage,
        [Text.EncoderReplacementFallback]::new('_'), [Text.DecoderFallback]::ReplacementFallback)
    $name = $encoding.GetString($encoding.GetBytes($name))
    while ($encoding.GetByteCount($name) -gt 11) { $name = $name.Substring(0, $name.Length - 1) }
    $name.TrimEnd()
}

function Copy-Installer($from, $to, $items, $copy) {
    $source = (Convert-Path -LiteralPath $from).TrimEnd('\') + '\'
    $target = Convert-Path -LiteralPath $to
    foreach ($directory in Get-ChildItem -LiteralPath $from -Directory -Force -Recurse) {
        [IO.Directory]::CreateDirectory((Join-Path $target $directory.FullName.Substring($source.Length))) | Out-Null
    }
    foreach ($file in $items) {
        if ($copy.Clock.ElapsedMilliseconds -ge 2000) {
            Verify-Volume $letter $disk $partition
            $copy.Clock.Restart()
        }
        $destinationFile = Join-Path $target $file.FullName.Substring($source.Length)
        $inputStream = [IO.File]::OpenRead($file.FullName)
        try {
            $outputStream = [IO.File]::Open($destinationFile, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try {
                while (($read = $inputStream.Read($copy.Buffer, 0, $copy.Buffer.Length)) -gt 0) {
                    $outputStream.Write($copy.Buffer, 0, $read)
                    $copy.Written += $read
                    $percent = [int][Math]::Min(99, [Math]::Floor(100.0 * $copy.Written / $copy.Total))
                    if ($percent -gt $copy.Percent) {
                        $copy.Percent = $percent
                        Send-Line "PROGRESS Copy $percent"
                    }
                }
                if ($outputStream.Length -ne $file.Length) { throw "Incomplete copy: $($file.Name)" }
            } finally { $outputStream.Dispose() }
        } finally { $inputStream.Dispose() }
        $year = $file.LastWriteTimeUtc.Year
        if (-not $uefi -or ($year -ge 1980 -and $year -le 2107)) {
            [IO.File]::SetLastWriteTimeUtc($destinationFile, $file.LastWriteTimeUtc)
        }
    }
}

function Verify-Volume($driveLetter, $disk, $partition) {
    $current = Get-Partition -DriveLetter $driveLetter
    $owner = Get-Disk -Number $current.DiskNumber
    if ($current.PartitionNumber -ne $partition.PartitionNumber -or
        -not (CanUse $owner) -or $owner.Path -ne $disk.Path -or $owner.UniqueId -ne $disk.UniqueId) {
        Fail "Utracono dostęp do wybranego pendrive’a." 'Access to the selected USB drive was lost.'
    }
}

function New-WorkDirectory($prefix) {
    Assert-SourcesOutside $disk @($tempRoot)
    $path = Join-Path $tempRoot ($prefix + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($path) | Out-Null
    $path
}

function Remove-WorkDirectory($path, $prefix) {
    if ($path -and [IO.Directory]::Exists($path) -and
        [IO.Path]::GetFileName($path).StartsWith($prefix, [StringComparison]::Ordinal) -and
        [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($path)).TrimEnd('\') -eq $tempRoot.TrimEnd('\')) {
        [IO.Directory]::Delete($path, $true)
    }
}

if ($env:XMLATOR_USB_OPERATION -eq 'list') {
    $drives = @(Get-Disk | Where-Object { CanUse $_ } | ForEach-Object {
        $letters = @(Get-Partition -DiskNumber $_.Number -ErrorAction SilentlyContinue |
            Where-Object DriveLetter | ForEach-Object { [string]$_.DriveLetter + ':' })
        [pscustomobject]@{ Number = $_.Number; Size = $_.Size; Path = $_.Path; UniqueId = $_.UniqueId
            Name = [string]$_.FriendlyName; Letters = $letters -join ', ' }
    })
    Send-Line (ConvertTo-Json -InputObject $drives -Compress)
    exit
}

$requested = ConvertFrom-Json $env:XMLATOR_USB_DISK
$iso = $env:XMLATOR_USB_ISO
$overlay = $env:XMLATOR_USB_OVERLAY
$answer = Join-Path $overlay 'Autounattend.xml'
$uefi = $env:XMLATOR_USB_FIRMWARE -eq 'Gpt'
$root = $env:XMLATOR_USB_MEDIA
$sources = @($iso, $root, $answer, $env:XMLATOR_USB_SAVED_XML, $env:XMLATOR_USB_EXE)
$disk = Selected-Disk $requested
Assert-SourcesOutside $disk $sources
$splitDirectory = $null
$efiDirectory = $null
$mountedWim = $false
$fallbackEfi = $null
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$dism = Join-Path $system 'dism.exe'
try {
    Send-Line 'PROGRESS Checking'
    $volumeLabel = Get-MediaLabel $env:XMLATOR_USB_LABEL $uefi
    $formatLabel = @{}
    if ($volumeLabel) { $formatLabel.NewFileSystemLabel = $volumeLabel }
    $install = @('sources\install.wim', 'sources\install.esd', 'sources\install.swm' | ForEach-Object {
        Join-Path $root $_
    } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ($install.Count -ne 1 -or -not (Test-Path -LiteralPath (Join-Path $root 'sources\boot.wim') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $root 'setup.exe') -PathType Leaf)) {
        Fail 'Wybierz instalacyjne ISO Windows z boot.wim i jednym zestawem plików install.' 'Choose a Windows Setup ISO with boot.wim and one install image set.'
    }
    if ($uefi) {
        $efi = 'efi\boot\boot' + $env:XMLATOR_USB_EFI + '.efi'
        if (-not (Test-Path -LiteralPath (Join-Path $root $efi) -PathType Leaf)) {
            $wim = Join-Path $root 'sources\install.wim'
            if ($env:XMLATOR_USB_EFI -ne 'x64' -or -not (Test-Path -LiteralPath $wim -PathType Leaf) -or
                -not (Test-Path -LiteralPath (Join-Path $root 'efi\microsoft\boot\bcd') -PathType Leaf)) {
                Fail 'W obrazie brakuje programu rozruchowego UEFI {0}.' 'The ISO is missing the UEFI boot file {0}.' $efi
            }
            # Windows 7 and Server 2008 R2 DVD media can omit the removable-media loader.
            Send-Line 'PROGRESS ExtractingBoot'
            $efiDirectory = New-WorkDirectory 'XMLator-efi-'
            $mount = Join-Path $efiDirectory 'mount'
            [IO.Directory]::CreateDirectory($mount) | Out-Null
            $loader = Join-Path $mount 'Windows\Boot\EFI\bootmgfw.efi'
            $output = & $dism /English /Mount-Image "/ImageFile:$wim" /Index:1 "/MountDir:$mount" /ReadOnly /Optimize
            if ($LASTEXITCODE -ne 0) {
                Fail 'Nie udało się odczytać obrazu WIM dla rozruchu UEFI. {0}' 'Could not read the WIM to find the UEFI loader. {0}' ($output -join ' ')
            }
            $mountedWim = $true
            if (-not (Test-Path -LiteralPath $loader -PathType Leaf)) {
                Fail "W obrazie WIM brakuje pliku rozruchowego UEFI." 'The WIM is missing the UEFI boot file.'
            }
            $fallbackEfi = Join-Path $efiDirectory 'bootx64.efi'
            Copy-Item -LiteralPath $loader -Destination $fallbackEfi
            $output = & $dism /English /Unmount-Image "/MountDir:$mount" /Discard
            if ($LASTEXITCODE -ne 0) {
                Fail 'Nie udało się odłączyć obrazu WIM. {0}' 'Could not unmount the WIM. {0}' ($output -join ' ')
            }
            $mountedWim = $false
            $sources += $fallbackEfi
        }
    } else {
        $bootsect = Join-Path $system 'bootsect.exe'
        if (-not (Test-Path -LiteralPath $bootsect -PathType Leaf) -and ${env:ProgramFiles(x86)}) {
            $bootsect = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\amd64\BCDBoot\bootsect.exe'
        }
        if (-not (Test-Path -LiteralPath $bootsect -PathType Leaf)) {
            $bootsect = Join-Path $root 'boot\bootsect.exe'
        }
        if (-not (Test-Path -LiteralPath (Join-Path $root 'bootmgr') -PathType Leaf) -or
            -not (Test-Path -LiteralPath (Join-Path $root 'boot\bcd') -PathType Leaf)) {
            Fail 'Obraz nie ma plików potrzebnych do rozruchu BIOS.' 'The ISO is missing the BIOS boot files.'
        }
        if (-not (Test-Path -LiteralPath $bootsect -PathType Leaf)) {
            Fail 'Brak Bootsect w obrazie ISO, Windows i Windows ADK. Wybierz UEFI lub użyj obrazu z Bootsect.' 'Bootsect is missing from the ISO, Windows and ADK. Use UEFI or choose media that includes Bootsect.'
        }
        $signature = Get-AuthenticodeSignature -LiteralPath $bootsect
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=Microsoft Corporation(?:,|$)') {
            Fail 'Do rozruchu BIOS potrzebny jest plik bootsect.exe z ważnym podpisem Microsoftu.' 'BIOS boot requires bootsect.exe with a valid Microsoft signature.'
        }
        if ((Get-Item -LiteralPath $bootsect).VersionInfo.OriginalFilename -notin @('bootsect.exe', 'bootsect.exe.mui')) {
            Fail 'Wybrany plik nie jest narzędziem Bootsect.' 'The selected file is not the Bootsect tool.'
        }
    }

    $partitionSize = [Math]::Min([long]($disk.Size - 4MB), [long](32GB - 1MB))
    $installerFiles = @(Get-ChildItem -LiteralPath $root -File -Force -Recurse)
    $extraFiles = @(Get-ChildItem -LiteralPath $overlay -File -Force -Recurse)
    foreach ($directory in Get-ChildItem -LiteralPath $overlay -Directory -Force -Recurse) {
        $relative = $directory.FullName.Substring($overlay.Length).TrimStart('\')
        if (Test-Path -LiteralPath (Join-Path $root $relative) -PathType Leaf) {
            Fail 'Na ISO jest plik pod ścieżką folderu dodatków: {0}.' 'The ISO contains a file at the path of an extra folder: {0}.' $relative
        }
    }
    foreach ($extra in $extraFiles) {
        $relative = $extra.FullName.Substring($overlay.Length).TrimStart('\')
        $inIso = Join-Path $root $relative
        if ($relative -ne 'Autounattend.xml' -and (Test-Path -LiteralPath $inIso)) {
            if (-not (Test-Path -LiteralPath $inIso -PathType Leaf) -or
                (Get-FileHash -LiteralPath $inIso).Hash -ne (Get-FileHash -LiteralPath $extra.FullName).Hash) {
                Fail 'Na ISO i w dodatkach jest różna zawartość pod tą samą ścieżką: {0}.' 'The ISO and the extra files have different contents at the same path: {0}.' $relative
            }
        }
    }
    $files = $installerFiles + $extraFiles
    if ($fallbackEfi) { $files += Get-Item -LiteralPath $fallbackEfi }
    $splitSource = $null
    $splitFiles = @()
    if ($uefi) {
        $large = @($files | Where-Object Length -gt $fatLimit)
        $wim = Join-Path $root 'sources\install.wim'
        if ($large.Count -gt 0) {
            if ($large.Count -ne 1 -or $large[0].FullName -ne $wim) {
                Fail 'FAT32 nie zmieści pliku większego niż 4 GiB. Automatyczny podział obsługuje sources\install.wim.' 'FAT32 cannot hold a file larger than 4 GiB. Automatic splitting supports sources\install.wim.'
            }
            if ((($files | Measure-Object Length -Sum).Sum + 64MB) -gt $partitionSize) {
                Fail 'Pliki instalacji przekraczają pojemność partycji FAT32.' 'The installation files exceed the FAT32 partition capacity.'
            }
            Send-Line 'PROGRESS Splitting'
            $splitDirectory = New-WorkDirectory 'XMLator-swm-'
            $splitSource = Join-Path $splitDirectory 'install.swm'
            $output = & $dism /English /Split-Image "/ImageFile:$wim" "/SWMFile:$splitSource" /FileSize:3800
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $splitSource -PathType Leaf)) {
                Fail 'Nie udało się podzielić WIM. {0}' 'Could not split the WIM. {0}' ($output -join ' ')
            }
            $splitFiles = @(Get-ChildItem -LiteralPath $splitDirectory -File -Filter '*.swm')
            if ($splitFiles.Length -eq 0 -or ($splitFiles | Where-Object Length -gt $fatLimit)) {
                Fail 'Składnik obrazu po podziale wciąż przekracza limit FAT32.' 'A split image segment still exceeds the FAT32 limit.'
            }
            $files = @($files | Where-Object FullName -ne $wim) + $splitFiles
            $installerFiles = @($installerFiles | Where-Object FullName -ne $wim)
            $sources += $splitSource
        }
    }
    $capacity = $disk.Size - 4MB
    if ($uefi) { $capacity = $partitionSize }
    $needed = [long](($files | Measure-Object Length -Sum).Sum) + [long]$files.Count * 64KB + 32MB
    if ($needed -gt $capacity) {
        Fail 'Na wybranym pendrivie jest za mało miejsca na instalator i dodatkowe pliki.' 'The selected USB drive is too small for Setup and the extra files.'
    }
    $disk = Selected-Disk $requested
    Assert-SourcesOutside $disk $sources

    Send-Line 'READY'
    if ([Console]::ReadLine() -cne 'WRITE') { exit }

    Send-Line 'PROGRESS Formatting'
    $disk = Selected-Disk $requested
    Assert-SourcesOutside $disk $sources
    $style = if ($uefi) { 'GPT' } else { 'MBR' }
    if ($disk.PartitionStyle -ne 'RAW' -and $disk.NumberOfPartitions -gt 0) {
        Clear-Disk -InputObject $disk -RemoveData -RemoveOEM -Confirm:$false -ErrorAction Stop
        $disk = Selected-Disk $requested
    }
    if ($disk.NumberOfPartitions -ne 0) {
        Fail "System Windows nie usunął wszystkich partycji z pendrive’a." 'Windows did not clear all partitions from the USB drive.'
    }
    # Windows can immediately present a cleared removable drive as an empty MBR disk.
    if ($disk.PartitionStyle -eq 'RAW') {
        Initialize-Disk -InputObject $disk -PartitionStyle $style -ErrorAction Stop
    } elseif ($disk.PartitionStyle -ne $style) {
        Set-Disk -InputObject $disk -PartitionStyle $style -ErrorAction Stop
    }
    $disk = Selected-Disk $requested
    if ($disk.PartitionStyle -ne $style) {
        Fail 'Windows nie ustawił wybranego układu partycji na pendrivie.' 'Windows could not apply the selected partition style to the USB drive.'
    }
    if ($uefi) {
        $partition = New-Partition -InputObject $disk -Size $partitionSize -AssignDriveLetter -ErrorAction Stop
        $partition | Format-Volume -FileSystem FAT32 @formatLabel -Confirm:$false -Force -ErrorAction Stop | Out-Null
    } else {
        $partition = New-Partition -InputObject $disk -UseMaximumSize -AssignDriveLetter -MbrType IFS -IsActive -ErrorAction Stop
        $partition | Format-Volume -FileSystem NTFS @formatLabel -Confirm:$false -Force -ErrorAction Stop | Out-Null
    }
    $letter = [string]$partition.DriveLetter
    if ($letter -notmatch '^[A-Za-z]$') { throw 'Missing USB drive letter.' }
    $destination = $letter + ':\'
    Verify-Volume $letter $disk $partition
    $copy = @{
        Total = [Math]::Max(1, [long](($installerFiles + $splitFiles | Measure-Object Length -Sum).Sum))
        Written = [long]0; Percent = 0; Clock = [Diagnostics.Stopwatch]::StartNew(); Buffer = [byte[]]::new(1MB)
    }
    Send-Line 'PROGRESS Copy 0'
    Copy-Installer $root $destination $installerFiles $copy
    if ($splitSource) {
        Verify-Volume $letter $disk $partition
        Copy-Installer $splitDirectory (Join-Path $destination 'sources') $splitFiles $copy
    }
    Verify-Volume $letter $disk $partition
    Send-Line 'PROGRESS Copy 100'
    Send-Line 'PROGRESS Extras'
    Invoke-Robocopy $overlay $destination @('/IS', '/IT')
    Verify-Volume $letter $disk $partition
    if ($fallbackEfi) {
        $bootFile = Join-Path $destination $efi
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($bootFile)) | Out-Null
        Copy-Item -LiteralPath $fallbackEfi -Destination $bootFile -Force
    }
    $usbAnswer = Join-Path $destination 'Autounattend.xml'
    Copy-Item -LiteralPath $answer -Destination $usbAnswer -Force
    Send-Line 'PROGRESS Verify'
    foreach ($extra in $extraFiles) {
        $relative = $extra.FullName.Substring($overlay.Length).TrimStart('\')
        $written = Join-Path $destination $relative
        if (-not (Test-Path -LiteralPath $written -PathType Leaf) -or
            (Get-FileHash -LiteralPath $written).Hash -ne (Get-FileHash -LiteralPath $extra.FullName).Hash) {
            Fail 'Nie udało się zweryfikować kopii na pendrivie: {0}.' 'USB copy failed verification: {0}.' $relative
        }
    }
    Verify-Volume $letter $disk $partition
    Send-Line 'PROGRESS Boot'
    if ($uefi) {
        # Copy using the Basic Data partition's drive letter, then mark it as an EFI System Partition.
        $partition | Set-Partition -GptType '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}' -ErrorAction Stop | Out-Null
    } else {
        $output = & $bootsect /nt60 ($letter + ':') /mbr
        if ($LASTEXITCODE -ne 0) {
            Fail 'Nie udało się zapisać kodu rozruchowego BIOS. {0}' 'Could not write the BIOS boot code. {0}' ($output -join ' ')
        }
    }
    Send-Line 'PROGRESS Finalizing'
    Send-Line 'DONE'
} finally {
    if ($mountedWim) {
        $null = & $dism /English /Unmount-Image "/MountDir:$mount" /Discard
        $mountedWim = $LASTEXITCODE -ne 0
    }
    Remove-WorkDirectory $splitDirectory 'XMLator-swm-'
    if (-not $mountedWim) { Remove-WorkDirectory $efiDirectory 'XMLator-efi-' }
}
