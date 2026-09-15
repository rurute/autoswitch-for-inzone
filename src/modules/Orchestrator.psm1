Set-StrictMode -Version Latest

$script:ModulesDir = Split-Path -Parent $PSCommandPath

# 依存モジュールを読み込む。テストから個別に import されている場合は二重読み込みしない
foreach ($dependency in @('Logging', 'ActionLogReader', 'SonyEventProvider', 'DeviceState', 'AudioSwitcher')) {
    if (-not (Get-Module -Name $dependency)) {
        Import-Module (Join-Path $script:ModulesDir "$dependency.psm1") -DisableNameChecking
    }
}

$script:HubProcessName    = 'INZONEHub'
$script:MaxBackoffSeconds = 60
$script:ResumeGapSeconds  = 300
$script:SeenLogIdCapacity = 200

function Get-NowUtc {
    [CmdletBinding()]
    param()

    return [datetime]::UtcNow
}

function Get-HubProcessStartTimeUtc {
    [CmdletBinding()]
    param()

    try {
        $processes = @(Get-Process -Name $script:HubProcessName -ErrorAction SilentlyContinue)
        if ($processes.Count -eq 0) { return $null }
        $earliest = $processes | Sort-Object -Property StartTime | Select-Object -First 1
        return $earliest.StartTime.ToUniversalTime()
    }
    catch {
        return $null
    }
}

function ConvertFrom-EpochMilliseconds {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][long]$Milliseconds
    )

    return [System.DateTimeOffset]::FromUnixTimeMilliseconds($Milliseconds).UtcDateTime
}

function New-AutoSwitchContext {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][hashtable]$Config
    )

    return @{
        ReaderState       = New-ActionLogReaderState -Path $Config.actionLogPath
        SeenLogIds        = New-SeenLogIdSet -Capacity $script:SeenLogIdCapacity
        BudsState         = 'Unknown'
        ConsecutiveErrors = 0
        NextDelaySeconds  = $Config.pollIntervalSeconds
        LastTickUtc       = $null
    }
}

function Invoke-DeviceSwitch {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][hashtable]$Config,
        [Parameter(Mandatory = $true)][string]$State
    )

    if ($State -eq 'Connected') {
        if (-not $Config.switchOnConnect) {
            Write-AutoSwitchLog -Level Info -Message 'switchOnConnect が false のため切り替えません。'
            return
        }
        $targetName = $Config.budsDevice
    }
    else {
        if (-not $Config.switchOnDisconnect) {
            Write-AutoSwitchLog -Level Info -Message 'switchOnDisconnect が false のため切り替えません。'
            return
        }
        $targetName = $Config.speakerDevice
    }

    $outcome = Set-DefaultPlaybackDeviceByName -Name $targetName

    switch ($outcome.Result) {
        'Switched'          { Write-AutoSwitchLog -Level Info  -Message $outcome.Message }
        'AlreadyDefault'    { Write-AutoSwitchLog -Level Debug -Message $outcome.Message }
        'DeviceNotFound'    { Write-AutoSwitchLog -Level Warn  -Message $outcome.Message }
        'ModuleUnavailable' { Write-AutoSwitchLog -Level Warn  -Message $outcome.Message }
        default             { Write-AutoSwitchLog -Level Error -Message $outcome.Message }
    }
}

function Invoke-StartupSync {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][hashtable]$Config,
        [Parameter(Mandatory = $true)][hashtable]$Context
    )

    $read = Read-ActionLogLines -State $Context.ReaderState
    $Context.ReaderState = $read.State
    $Context.LastTickUtc = Get-NowUtc

    if ($read.Status -eq 'FileMissing') {
        Write-AutoSwitchLog -Level Info -Message 'ActionLog.log がまだありません。INZONE Hub の起動を待機します。'
        $Context.NextDelaySeconds = $Config.idlePollIntervalSeconds
        return $Context
    }

    if ($read.Status -eq 'Error') {
        Write-AutoSwitchLog -Level Warn -Message "起動時同期: ActionLog.log を読めません: $($read.Message)"
        $Context.NextDelaySeconds = $Config.idlePollIntervalSeconds
        return $Context
    }

    # 採用可否にかかわらず、読んだ行はすべて既読として登録する。
    # これをしないと次の tick で過去のイベントが「新規」として再発火する。
    $events = @(Get-BudsEventsFromLines -Lines $read.Lines -SeenLogIds $Context.SeenLogIds)
    $Context.NextDelaySeconds = $Config.pollIntervalSeconds

    if ($Config.startupSyncMaxAgeMinutes -le 0) {
        Write-AutoSwitchLog -Level Info -Message '起動時同期は無効です。既存のログは読み飛ばしました。'
        return $Context
    }

    if ($events.Count -eq 0) {
        Write-AutoSwitchLog -Level Info -Message '起動時同期: 判定できるイベントがありません。切り替えません。'
        return $Context
    }

    $hubStartUtc = Get-HubProcessStartTimeUtc
    if ($null -eq $hubStartUtc) {
        Write-AutoSwitchLog -Level Info -Message '起動時同期: INZONE Hub が起動していません。切り替えません。'
        return $Context
    }

    $lastEvent = $events[$events.Count - 1]
    $eventUtc = ConvertFrom-EpochMilliseconds -Milliseconds $lastEvent.TimeStampMs
    $nowUtc = Get-NowUtc

    if ($eventUtc -lt $hubStartUtc) {
        Write-AutoSwitchLog -Level Info -Message '起動時同期: 最終イベントが INZONE Hub の起動より前です。切り替えません。'
        return $Context
    }

    $ageMinutes = ($nowUtc - $eventUtc).TotalMinutes
    if ($ageMinutes -gt $Config.startupSyncMaxAgeMinutes) {
        Write-AutoSwitchLog -Level Info -Message ('起動時同期: 最終イベントが古すぎます（{0:N1} 分前）。切り替えません。' -f $ageMinutes)
        return $Context
    }

    $Context.BudsState = $lastEvent.State
    Write-AutoSwitchLog -Level Info -Message "起動時同期: 現在の状態を $($lastEvent.State) と判定しました。"
    Invoke-DeviceSwitch -Config $Config -State $lastEvent.State

    return $Context
}

function Invoke-AutoSwitchTick {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][hashtable]$Config,
        [Parameter(Mandatory = $true)][hashtable]$Context
    )

    $nowUtc = Get-NowUtc
    if ($null -ne $Context.LastTickUtc -and
        ($nowUtc - $Context.LastTickUtc).TotalSeconds -gt $script:ResumeGapSeconds) {
        Write-AutoSwitchLog -Level Info -Message '長時間のギャップを検知しました（スリープ復帰とみなします）。監視を通常間隔へ戻します。'
        $Context.ConsecutiveErrors = 0
    }
    $Context.LastTickUtc = $nowUtc

    $read = Read-ActionLogLines -State $Context.ReaderState
    $Context.ReaderState = $read.State

    if ($read.Status -eq 'FileMissing') {
        Write-AutoSwitchLog -Level Debug -Message 'ActionLog.log がありません。待機します。'
        $Context.ConsecutiveErrors = 0
        $Context.NextDelaySeconds = $Config.idlePollIntervalSeconds
        return $Context
    }

    if ($read.Status -eq 'Error') {
        $Context.ConsecutiveErrors++
        $delay = [double]$Config.pollIntervalSeconds * [Math]::Pow(2, $Context.ConsecutiveErrors - 1)
        $Context.NextDelaySeconds = [int][Math]::Min($delay, [double]$script:MaxBackoffSeconds)
        Write-AutoSwitchLog -Level Warn -Message "ActionLog.log の読み取りに失敗しました: $($read.Message)"
        return $Context
    }

    if ($read.Status -eq 'Truncated') {
        Write-AutoSwitchLog -Level Info -Message 'ActionLog.log の truncate を検知しました。先頭から読み直します。'
    }
    elseif ($read.Status -eq 'Recreated') {
        Write-AutoSwitchLog -Level Info -Message 'ActionLog.log の再生成を検知しました。先頭から読み直します。'
    }

    $Context.ConsecutiveErrors = 0
    $Context.NextDelaySeconds = $Config.pollIntervalSeconds

    # 追記順がそのまま時系列順。PowerShell 5.1 の Sort-Object は安定ではないので並べ替えない
    foreach ($budsEvent in @(Get-BudsEventsFromLines -Lines $read.Lines -SeenLogIds $Context.SeenLogIds)) {
        $transition = Resolve-StateTransition -CurrentState $Context.BudsState -EventState $budsEvent.State
        if (-not $transition.Changed) {
            Write-AutoSwitchLog -Level Debug -Message "状態は $($budsEvent.State) のままです。切り替えません。"
            continue
        }

        $Context.BudsState = $transition.NewState
        Write-AutoSwitchLog -Level Info -Message "状態遷移を検知しました: $($transition.NewState)"
        Invoke-DeviceSwitch -Config $Config -State $transition.NewState
    }

    return $Context
}

Export-ModuleMember -Function `
    Get-NowUtc, Get-HubProcessStartTimeUtc, ConvertFrom-EpochMilliseconds, `
    New-AutoSwitchContext, Invoke-StartupSync, Invoke-AutoSwitchTick
