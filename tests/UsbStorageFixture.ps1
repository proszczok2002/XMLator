$ErrorActionPreference = 'Stop'
$fixture = $env:XMLATOR_USB_FIXTURE
$state = $env:XMLATOR_USB_CASE
$disk = [pscustomobject]@{ Number = 71; Size = 8GB; Path = 'test-usb'; UniqueId = 'test-id'; BusType = 'USB'
    NumberOfPartitions = 1; PartitionStyle = 'MBR'; FriendlyName = 'Test USB'; IsBoot = $false; IsSystem = $false }
if ($state -eq 'empty-mbr') { $disk.NumberOfPartitions = 0 }
$isoDrive = New-PSDrive -Name IsoFixture -PSProvider FileSystem -Root (Join-Path $fixture 'iso')
$letter = 90..68 | ForEach-Object { [string][char]$_ } | Where-Object { -not (Get-PSDrive -Name $_ -ErrorAction Ignore) } | Select-Object -First 1
$targetDrive = New-PSDrive -Name $letter -PSProvider FileSystem -Root (Join-Path $fixture 'usb')
$script:reads = 0

function Trace-Step($step) { Add-Content -LiteralPath (Join-Path $fixture 'trace.txt') -Value $step }
function Get-Disk($Number) {
    if ($Number -ne 71) { throw "Unexpected disk number $Number" }
    $script:reads++
    if ($state -eq 'changed' -and $script:reads -gt 2) { $disk.UniqueId = 'replacement' }
    $disk
}
function Get-Partition($DiskNumber, $DriveLetter) {
    if ($DriveLetter) { return [pscustomobject]@{ DiskNumber = 71; PartitionNumber = 1 } }
    [pscustomobject]@{ AccessPaths = @('test-target'); DriveLetter = 'X' }
}
function Get-DiskImage($ImagePath) { [pscustomobject]@{ Attached = $false } }
function Mount-DiskImage($ImagePath, $StorageType, $Access, [switch]$PassThru) { Trace-Step 'mount'; [pscustomobject]@{ Attached = $true } }
function Dismount-DiskImage($ImagePath, $ErrorAction) { Trace-Step 'dismount' }
function Get-Volume {
    param($FilePath, [Parameter(ValueFromPipeline)]$Image)
    process {
        if ($Image) { return [pscustomobject]@{ DriveLetter = 'IsoFixture'; FileSystemLabel = '11_26H2_10_2026' } }
        [pscustomobject]@{ UniqueId = $(if ($state -eq 'on-target') { 'test-target' } else { 'test-source' }) }
    }
}
function Clear-Disk($InputObject, [switch]$RemoveData, [switch]$RemoveOEM, [switch]$Confirm, $ErrorAction) {
    Trace-Step 'clear'
    $InputObject.PartitionStyle = if ($state -eq 'auto-mbr') { 'MBR' } else { 'RAW' }
    $InputObject.NumberOfPartitions = 0
}
function Initialize-Disk($InputObject, $PartitionStyle, $ErrorAction) {
    if ($InputObject.PartitionStyle -ne 'RAW') { throw 'Disk already initialized' }
    Trace-Step "initialize-$PartitionStyle"
    $InputObject.PartitionStyle = $PartitionStyle
}
function Set-Disk($InputObject, $PartitionStyle, $ErrorAction) {
    if ($InputObject.NumberOfPartitions -ne 0) { throw 'Unexpected partitions before conversion' }
    Trace-Step "convert-$PartitionStyle"
    $InputObject.PartitionStyle = $PartitionStyle
}
function New-Partition($InputObject, $Size, $MbrType, [switch]$AssignDriveLetter, [switch]$UseMaximumSize, [switch]$IsActive, $ErrorAction) {
    if ($MbrType -and (-not $IsActive -or $MbrType -ne 'IFS')) { throw 'Invalid BIOS partition' }
    [pscustomobject]@{ DriveLetter = $letter; PartitionNumber = 1 }
}
function Format-Volume { param([Parameter(ValueFromPipeline)]$Partition, $FileSystem, $NewFileSystemLabel, [switch]$Confirm, [switch]$Force)
    process {
        Trace-Step "format-$FileSystem"
        Set-Content -LiteralPath (Join-Path $fixture 'volume-label.txt') -Value $NewFileSystemLabel
    }
}
function Set-Partition { param([Parameter(ValueFromPipeline)]$Partition, $GptType)
    process { Trace-Step "type-$GptType" }
}
function Get-AuthenticodeSignature($LiteralPath) { [pscustomobject]@{ Status = 'Valid'; SignerCertificate = @{ Subject = 'CN=Test, O=Microsoft Corporation, C=US' } } }
function Get-Item($LiteralPath) {
    if ($LiteralPath -eq (Join-Path $fixture 'bootsect.exe')) { return [pscustomobject]@{ VersionInfo = @{ OriginalFilename = 'bootsect.exe' } } }
    Microsoft.PowerShell.Management\Get-Item -LiteralPath $LiteralPath
}
function Test-Bootsect { Trace-Step 'bootsect'; $global:LASTEXITCODE = 0 }
function Get-FileHash($LiteralPath) {
    Microsoft.PowerShell.Utility\Get-FileHash -LiteralPath (Convert-Path -LiteralPath $LiteralPath)
}
function Copy-MediaFixture($from, $to, [Parameter(ValueFromRemainingArguments)]$flags) {
    $target = Convert-Path -LiteralPath $to
    $source = Convert-Path -LiteralPath $from
    & (Join-Path ([Environment]::SystemDirectory) 'robocopy.exe') $source $target @flags
    if ($state -eq 'corrupt-extra' -and (Test-Path -LiteralPath (Join-Path $source '$WinPEDriver$'))) {
        Set-Content -LiteralPath (Join-Path $target '$WinPEDriver$\storage.inf') -Value 'Corrupted fixture'
    }
}
Set-Alias -Name (Join-Path $fixture 'robocopy.exe') -Value Copy-MediaFixture
Set-Alias -Name (Join-Path $fixture 'bootsect.exe') -Value Test-Bootsect

& ([ScriptBlock]::Create($env:XMLATOR_USB_SCRIPT))
