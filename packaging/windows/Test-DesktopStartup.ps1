param(
    [Parameter(Mandatory = $true)]
    [string] $Executable
)

$ErrorActionPreference = "Stop"
$resolvedExecutable = (Resolve-Path $Executable).Path
$process = Start-Process -FilePath $resolvedExecutable -PassThru
$deadline = [DateTime]::UtcNow.AddSeconds(20)

try {
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) {
            throw "Desktop process exited before opening a window. Exit code: $($process.ExitCode)"
        }

        if ($process.MainWindowHandle -ne 0) {
            if ($process.MainWindowTitle -ne "Captioner") {
                throw "Desktop process opened an unexpected window: '$($process.MainWindowTitle)'."
            }

            if (-not $process.Responding) {
                throw "Desktop window was created but is not responding."
            }

            Write-Host "Captioner desktop window opened with handle $($process.MainWindowHandle)."
            return
        }
    }

    throw "Desktop process stayed alive but did not create a main window within 20 seconds."
}
catch {
    $logPath = Join-Path $env:LOCALAPPDATA "Captioner/logs/desktop-startup.log"
    if (Test-Path $logPath) {
        Write-Host "Desktop startup log:"
        Get-Content $logPath -Tail 80
    }

    throw
}
finally {
    $process.Refresh()
    if (-not $process.HasExited) {
        [void] $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) {
            Stop-Process -Id $process.Id -Force
        }
    }

    $process.Dispose()
}
