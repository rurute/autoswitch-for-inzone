<#
.SYNOPSIS
    INZONE Buds の接続／切断に追従して Windows の既定の再生デバイスを切り替える常駐ツール。
.DESCRIPTION
    INZONE Hub の ActionLog.log を監視し、状態が遷移したときだけ切り替える。
    UI は持たない。停止するにはプロセスを終了する。
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$modulesDir = Join-Path $scriptRoot 'modules'

foreach ($moduleName in @('Config', 'Logging', 'ActionLogReader', 'SonyEventProvider', 'DeviceState', 'AudioSwitcher', 'Orchestrator')) {
    Import-Module (Join-Path $modulesDir "$moduleName.psm1") -Force -DisableNameChecking
}

$appRoot = Resolve-AutoSwitchAppRoot -ScriptRoot $scriptRoot

$mutex = New-Object System.Threading.Mutex($false, 'Local\INZONE-AutoSwitch')
$hasMutex = $false

try {
    try { $hasMutex = $mutex.WaitOne(0) }
    catch [System.Threading.AbandonedMutexException] { $hasMutex = $true }

    $configResult = Read-AutoSwitchConfig -Path (Join-Path $appRoot 'config.json')
    $config = $configResult.Config

    Initialize-AutoSwitchLog `
        -Path (Join-Path $appRoot 'logs\INZONE-AutoSwitch.log') `
        -Level $config.logLevel `
        -MaxSizeKB $config.maxLogSizeKB

    if (-not $hasMutex) {
        Write-AutoSwitchLog -Level Info -Message 'INZONE Auto Switch は既に起動しています。このプロセスは終了します。'
        return
    }

    foreach ($warning in $configResult.Warnings) {
        Write-AutoSwitchLog -Level Warn -Message $warning
    }

    Write-AutoSwitchLog -Level Info -Message "INZONE Auto Switch を開始します。appRoot=$appRoot"
    Write-AutoSwitchLog -Level Info -Message "監視対象: $($config.actionLogPath)"

    if (-not (Test-AudioSwitcherAvailable)) {
        Write-AutoSwitchLog -Level Warn -Message 'AudioDeviceCmdlets を読み込めません。切り替えのたびに再試行します。'
    }

    $context = New-AutoSwitchContext -Config $config
    $context = Invoke-StartupSync -Config $config -Context $context

    while ($true) {
        Start-Sleep -Seconds $context.NextDelaySeconds
        try {
            $context = Invoke-AutoSwitchTick -Config $config -Context $context
        }
        catch {
            # tick は原則として例外を投げない設計だが、万一漏れても常駐を止めない
            Write-AutoSwitchLog -Level Error -Message "予期しない例外が発生しました: $($_.Exception.Message)"
            $context.NextDelaySeconds = $config.idlePollIntervalSeconds
        }
    }
}
finally {
    if ($hasMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
