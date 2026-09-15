Set-StrictMode -Version Latest

function Get-AutoSwitchInstallRoot {
    [CmdletBinding()]
    param()

    return (Join-Path $env:LOCALAPPDATA 'INZONE-AutoSwitch')
}

function Get-AutoSwitchTaskName {
    [CmdletBinding()]
    param()

    return 'INZONE Auto Switch'
}

function Stop-AutoSwitchProcess {
    [CmdletBinding()]
    param()

    try {
        $processes = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction Stop |
            Where-Object { $_.CommandLine -like '*INZONE-AutoSwitch.ps1*' })

        foreach ($process in $processes) {
            Write-Host "実行中の INZONE Auto Switch を停止します (PID $($process.ProcessId))"
            Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
        }
    }
    catch {
        Write-Warning "実行中プロセスの停止に失敗しました: $($_.Exception.Message)"
    }
}
