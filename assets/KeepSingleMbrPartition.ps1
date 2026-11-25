$ErrorActionPreference = 'Stop'

$windows = Get-Partition -DriveLetter $env:SystemDrive.TrimEnd(':')
$disk = Get-Disk -Number $windows.DiskNumber
if ($disk.Number -ne ${ExpectedDisk} -or $disk.PartitionStyle -ne 'MBR' -or
    -not $windows.IsBoot -or -not $windows.IsActive -or $windows.PartitionNumber -ne 1) {
    throw 'The installed Windows partition is outside the confirmed single-partition MBR layout.'
}

$partitions = @(Get-Partition -DiskNumber $disk.Number)
if ($partitions.Count -eq 1) { return }

$recovery = $partitions | Where-Object PartitionNumber -eq 2
if ($partitions.Count -ne 2 -or -not $recovery -or $recovery.MbrType -ne 0x27 -or
    $recovery.DriveLetter -match '[A-Z]' -or $recovery.IsActive -or $recovery.IsSystem -or
    $recovery.Offset -ne ($windows.Offset + $windows.Size) -or $recovery.Size -gt 4GB) {
    throw 'The additional partition does not match the Windows Setup recovery partition.'
}

$agent = Join-Path $env:SystemRoot 'System32\reagentc.exe'
$location = '\device\harddisk{0}\partition2\Recovery\WindowsRE' -f $disk.Number
$status = & $agent /info
if ($LASTEXITCODE -ne 0 -or -not ($status | Select-String -SimpleMatch $location)) {
    throw 'Windows RE is not registered on the additional partition.'
}

$directory = Join-Path $env:SystemRoot 'System32\Recovery'
$image = Join-Path $directory 'Winre.wim'
& $agent /disable
if ($LASTEXITCODE -ne 0) { throw 'Windows RE could not be safely moved back to Windows.' }
try {
    if (-not (Test-Path -LiteralPath $image -PathType Leaf)) {
        throw 'Windows RE files were not moved back to the Windows partition.'
    }
    $commands = [IO.Path]::GetTempFileName()
    try {
        "select disk $($disk.Number)", 'select partition 2', 'delete partition override' |
            Set-Content -LiteralPath $commands -Encoding Ascii
        & diskpart.exe /s $commands
        if ($LASTEXITCODE -ne 0 -or @(Get-Partition -DiskNumber $disk.Number).Count -ne 1) {
            throw 'DiskPart could not remove the now unneeded recovery partition.'
        }
    }
    finally { Remove-Item -LiteralPath $commands -Force }
    $size = Get-PartitionSupportedSize -InputObject $windows
    Resize-Partition -InputObject $windows -Size $size.SizeMax
}
finally {
    & $agent /setreimage /path $directory
    & $agent /enable
}
