$ErrorActionPreference = 'Stop'
$target = $env:XMLATOR_UPDATE_TARGET
$stage = $env:XMLATOR_UPDATE_STAGE
$version = [Version]$env:XMLATOR_UPDATE_VERSION
$expectedHash = $env:XMLATOR_UPDATE_HASH
$size = [long]$env:XMLATOR_UPDATE_SIZE
$parentId = [int]$env:XMLATOR_UPDATE_PARENT
$parentStarted = [long]$env:XMLATOR_UPDATE_STARTED
$message = $env:XMLATOR_UPDATE_MESSAGE
$mutexName = 'Local\XMLator.Update.' + $env:XMLATOR_UPDATE_MUTEX
Get-ChildItem Env:XMLATOR_UPDATE_* | ForEach-Object { Remove-Item -LiteralPath ('Env:' + $_.Name) }

function Start-XMLator([string]$arguments) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $target
    $start.Arguments = $arguments
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($target)
    $start.UseShellExecute = $false
    return [Diagnostics.Process]::Start($start)
}

function Remove-UpdateFile([string]$file) {
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        try { [IO.File]::Delete($file); return }
        catch [IO.IOException] { Start-Sleep -Milliseconds 150 }
        catch [UnauthorizedAccessException] { Start-Sleep -Milliseconds 150 }
    }
}

$backup = $stage + '.previous'
$mutex = $null
$ready = $null
$child = $null
$locked = $false
$parentExited = $false
$replaced = $false
$keepBackup = $false
try {
    $parentProcess = Get-Process -Id $parentId -ErrorAction SilentlyContinue
    if ($null -ne $parentProcess) {
        try {
            if ($parentProcess.StartTime.ToUniversalTime().Ticks -eq $parentStarted -and
                !$parentProcess.WaitForExit(90000)) { throw 'XMLator did not close.' }
        }
        catch [InvalidOperationException] {
            if (!$parentProcess.HasExited) { throw }
        }
        finally { $parentProcess.Dispose() }
    }
    $parentExited = $true

    $mutex = [Threading.Mutex]::new($false, $mutexName)
    try { $locked = $mutex.WaitOne(45000) }
    catch [Threading.AbandonedMutexException] { $locked = $true }
    if (!$locked) { throw 'An update is already running.' }

    $previous = [Diagnostics.FileVersionInfo]::GetVersionInfo($target)
    $installed = [Version]::new($previous.FileMajorPart, $previous.FileMinorPart, $previous.FileBuildPart, $previous.FilePrivatePart)
    if ($previous.ProductName -ne 'XMLator') { throw 'The destination is not XMLator.' }
    if ($installed -ge $version) {
        $child = Start-XMLator ''
        return
    }
    if ((Get-Item -LiteralPath $stage).Length -ne $size -or
        (Get-FileHash -LiteralPath $stage -Algorithm SHA256).Hash -ine $expectedHash) {
        throw 'The staged update failed verification.'
    }

    $token = [Guid]::NewGuid().ToString('N')
    $ready = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, ('Local\XMLator.Update.Ready.' + $token))
    [IO.File]::Replace($stage, $target, $backup)
    $replaced = $true
    $child = Start-XMLator ('--xmlator-update-ready ' + $token)
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while (!$ready.WaitOne(200)) {
        if ($child.HasExited -or $deadline.Elapsed.TotalSeconds -ge 30) { throw 'The updated XMLator did not start.' }
    }
}
catch {
    try {
        if ($replaced) {
            $keepBackup = $true
            if ($null -ne $child -and !$child.HasExited) {
                $child.Kill()
                if (!$child.WaitForExit(10000)) { throw 'The updated process could not be stopped.' }
            }
            [IO.File]::Replace($backup, $target, $stage)
            $keepBackup = $false
        }
        if (!$parentExited) { throw }
        $restarted = Start-XMLator '--xmlator-update-failed'
        $restarted.Dispose()
    }
    catch {
        Add-Type -AssemblyName System.Windows.Forms
        if ($keepBackup) { $message += "`r`n`r`n" + $backup }
        [void][Windows.Forms.MessageBox]::Show($message, 'XMLator', 'OK', 'Error')
    }
    exit 1
}
finally {
    if ($null -ne $child) { $child.Dispose() }
    if ($null -ne $ready) { $ready.Dispose() }
    try {
        Remove-UpdateFile $stage
        if (!$keepBackup) { Remove-UpdateFile $backup }
    }
    finally {
        if ($locked) { $mutex.ReleaseMutex() }
        if ($null -ne $mutex) { $mutex.Dispose() }
    }
}
