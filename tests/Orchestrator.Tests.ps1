BeforeAll {
    $script:RepoRoot = Split-Path -Parent $PSScriptRoot

    Import-Module (Join-Path $PSScriptRoot 'stubs\AudioDeviceCmdletsStub.psm1') -Global -Force -DisableNameChecking
    foreach ($m in 'Config', 'Logging', 'ActionLogReader', 'SonyEventProvider', 'DeviceState', 'AudioSwitcher', 'Orchestrator') {
        Import-Module (Join-Path $script:RepoRoot "src\modules\$m.psm1") -Force -DisableNameChecking
    }

    # エポックミリ秒 <-> UTC の相互変換（テスト側でも使う）
    function global:ConvertTo-EpochMs {
        param([datetime]$Utc)
        return [long]([System.DateTimeOffset]::new($Utc, [TimeSpan]::Zero).ToUnixTimeMilliseconds())
    }

    function global:New-JsonEventLine {
        param([string]$LogId, [long]$TimeStamp, [int]$ActionTypeId)
        $name = if ($ActionTypeId -eq 23011) { 'ConnectDevice' } else { 'DisconnectDevice' }
        return ('{{"logVersion":"1.5","logId":"{0}","timeStamp":{1},"actionTypeId":{2},"actionTypeName":"{3}"}}' -f $LogId, $TimeStamp, $ActionTypeId, $name)
    }

    # $script:LogPath は BeforeEach が設定する。同じファイルスコープなので参照できる
    function Write-LogLines {
        param([string[]]$Lines, [switch]$Append)
        $text = if ($Lines.Count -eq 0) { '' } else { ($Lines -join "`n") + "`n" }
        if ($Append) {
            $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($text)
            $stream = [System.IO.File]::Open($script:LogPath, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite)
            try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
        }
        else {
            [System.IO.File]::WriteAllText($script:LogPath, $text, (New-Object System.Text.UTF8Encoding($true)))
        }
    }
}

Describe 'Orchestrator' {

    BeforeEach {
        $script:Dir = Join-Path ([System.IO.Path]::GetTempPath()) ("orc-" + [guid]::NewGuid().ToString())
        New-Item -ItemType Directory -Path $script:Dir -Force | Out-Null
        $script:LogPath = Join-Path $script:Dir 'ActionLog.log'

        $script:Config = Get-AutoSwitchConfigDefault
        $script:Config.actionLogPath = $script:LogPath
        $script:Config.budsDevice = 'BUDS'
        $script:Config.speakerDevice = 'SPEAKER'

        Initialize-AutoSwitchLog -Path (Join-Path $script:Dir 'app.log') -Level 'Debug' -MaxSizeKB 1024

        # 既定の時刻: 2026-09-15 06:36:40Z 固定
        $script:NowUtc = [datetime]::new(2026, 9, 15, 6, 36, 40, [System.DateTimeKind]::Utc)
        Mock -ModuleName Orchestrator Get-NowUtc { return $script:NowUtc }
        Mock -ModuleName Orchestrator Get-HubProcessStartTimeUtc { return $script:NowUtc.AddMinutes(-30) }
        # Mock のスクリプトブロックでは、渡されなかったパラメータが $false ではなく「未設定」に
        # なる。環境側の StrictMode 下でそれを読むと例外になるため、明示的に param を置く。
        Mock -ModuleName Orchestrator Set-DefaultPlaybackDeviceByName {
            param($Name)
            return @{ Result = 'Switched'; Message = "switched to $Name" }
        }
    }

    AfterEach {
        Remove-Item -LiteralPath $script:Dir -Recurse -Force -ErrorAction SilentlyContinue
    }

    Describe 'Invoke-StartupSync' {

        It 'INZONE Hub が起動していなければ切り替えない' {
            Mock -ModuleName Orchestrator Get-HubProcessStartTimeUtc { return $null }
            Write-LogLines -Lines @((New-JsonEventLine -LogId 'e1' -TimeStamp (ConvertTo-EpochMs -Utc $script:NowUtc.AddMinutes(-1)) -ActionTypeId 23011))

            $ctx = New-AutoSwitchContext -Config $script:Config
            $ctx = Invoke-StartupSync -Config $script:Config -Context $ctx

            $ctx.BudsState | Should -Be 'Unknown'
            Should -Invoke -ModuleName Orchestrator Set-DefaultPlaybackDeviceByName -Times 0 -Exactly
        }

        It '最終イベントが Hub 起動より前なら切り替えない' {
            Write-LogLines -Lines @((New-JsonEventLine -LogId 'e1' -TimeStamp (ConvertTo-EpochMs -Utc $script:NowUtc.AddHours(-2)) -ActionTypeId 23011))

            $ctx = New-AutoSwitchContext -Config $script:Config
            $ctx = Invoke-StartupSync -Config $script:Config -Context $ctx

            $ctx.BudsState | Should -Be 'Unknown'
            Should -Invoke -ModuleName Orchestrator Set-DefaultPlaybackDeviceByName -Times 0 -Exactly
        }

        It '最終イベントが startupSyncMaxAgeMinutes より古ければ切り替えない' {
            Mock -ModuleName Orchestrator Get-HubProcessStartTimeUtc { return $script:NowUtc.AddHours(-6) }
            Write-LogLines -Lines @((New-JsonEventLine -LogId 'e1' -TimeStamp (ConvertTo-EpochMs -Utc $script:NowUtc.AddMinutes(-11)) -ActionTypeId 23011))

            $ctx = New-AutoSwitchContext -Config $script:Config
            $ctx = Invoke-StartupSync -Config $script:Config -Context $ctx

            $ctx.BudsState | Should -Be 'Unknown'
            Should -Invoke -ModuleName Orchestrator Set-DefaultPlaybackDeviceByName -Times 0 -Exactly
        }

        It '両方の条件を満たせば 1 回だけ切り替える' {
            Write-LogLines -Lines @((New-JsonEventLine -LogId 'e1' -TimeStamp (ConvertTo-EpochMs -Utc $script:NowUtc.AddMinutes(-2)) -ActionTypeId 23011))

            $ctx = New-AutoSwitchContext -Config $script:Config
            $ctx = Invoke-StartupSync -Config $script:Config -Context $ctx

            $ctx.BudsState | Should -Be 'Connected'
            Should -Invoke -ModuleName Orchestrator Set-DefaultPlaybackDeviceByName -Times 1 -Exactly -ParameterFilter { $Name -eq 'BUDS' }
        }

        It '複数イベントがあれば最後のものを採用する' {
            Write-LogLines -Lines @(
                (New-JsonEventLine -LogId 'e1' -TimeStamp (ConvertTo-EpochMs -Utc $script:NowUtc.AddMinutes(-5)) -ActionTypeId 23011),
                (New-JsonEventLine -LogId 'e2' -TimeStamp (ConvertTo-EpochMs -Utc $script:NowUtc.AddMinutes(-2)) -ActionTypeId 23012)
            )

            $ctx = New-AutoSwitchContext -Config $script:Config
            $ctx = Invoke-StartupSync -Config $script:Config -Context $ctx

            $ctx.BudsState | Should -Be 'Disconnected'
            Should -Invoke -ModuleName Orchestrator Set-DefaultPlaybackDeviceByName -Times 1 -Exactly -ParameterFilter { $Name -eq 'SPEAKER' }
        }

        It '同期で読んだイベントが次の tick で再発火しない' {
            Write-LogLines -Lines @((New-JsonEventLine -LogId 'e1' -TimeStamp (ConvertTo-EpochMs -Utc $script:NowUtc.AddMinutes(-2)) -ActionTypeId 23011))

            $ctx = New-AutoSwitchContext -Config $script:Config
            $ctx = Invoke-StartupSync -Config $script:Config -Context $ctx
            $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx

            Should -Invoke -ModuleName Orchestrator Set-DefaultPlaybackDeviceByName -Times 1 -Exactly
        }

        It 'startupSyncMaxAgeMinutes = 0 なら同期しないが既存行は読み飛ばす' {
            $script:Config.startupSyncMaxAgeMinutes = 0
            Write-LogLines -Lines @((New-JsonEventLine -LogId 'e1' -TimeStamp (ConvertTo-EpochMs -Utc $script:NowUtc.AddMinutes(-2)) -ActionTypeId 23011))

            $ctx = New-AutoSwitchContext -Config $script:Config
            $ctx = Invoke-StartupSync -Config $script:Config -Context $ctx
            $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx

            $ctx.BudsState | Should -Be 'Unknown'
            Should -Invoke -ModuleName Orchestrator Set-DefaultPlaybackDeviceByName -Times 0 -Exactly
        }

        It 'ログがまだ無ければ idle 間隔で待機する' {
            $ctx = New-AutoSwitchContext -Config $script:Config
            $ctx = Invoke-StartupSync -Config $script:Config -Context $ctx

            $ctx.BudsState        | Should -Be 'Unknown'
            $ctx.NextDelaySeconds | Should -Be $script:Config.idlePollIntervalSeconds
        }
    }

    Describe 'Invoke-AutoSwitchTick' {

        It 'ConnectDevice で budsDevice へ切り替える' {
            Write-LogLines -Lines @()
            $ctx = New-AutoSwitchContext -Config $script:Config
            $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx

            Write-LogLines -Append -Lines @((New-JsonEventLine -LogId 'c1' -TimeStamp 1000 -ActionTypeId 23011))
            $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx

            $ctx.BudsState | Should -Be 'Connected'
            Should -Invoke -ModuleName Orchestrator Set-DefaultPlaybackDeviceByName -Times 1 -Exactly -ParameterFilter { $Name -eq 'BUDS' }
        }

        It 'DisconnectDevice で speakerDevice へ切り替える' {
            Write-LogLines -Lines @()
            $ctx = New-AutoSwitchContext -Config $script:Config
            $ctx.BudsState = 'Connected'
            $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx

            Write-LogLines -Append -Lines @((New-JsonEventLine -LogId 'd1' -TimeStamp 2000 -ActionTypeId 23012))
            $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx

            $ctx.BudsState | Should -Be 'Disconnected'
            Should -Invoke -ModuleName Orchestrator Set-DefaultPlaybackDeviceByName -Times 1 -Exactly -ParameterFilter { $Name -eq 'SPEAKER' }
        }

        It 'ConnectDevice が連続しても 2 回目は切り替えない（手動変更を尊重する）' {
            Write-LogLines -Lines @()
            $ctx = New-AutoSwitchContext -Config $script:Config
            $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx

            Write-LogLines -Append -Lines @((New-JsonEventLine -LogId 'c1' -TimeStamp 1000 -ActionTypeId 23011))
            $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx

            Write-LogLines -Append -Lines @((New-JsonEventLine -LogId 'c2' -TimeStamp 5000 -ActionTypeId 23011))
            $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx

            $ctx.BudsState | Should -Be 'Connected'
            Should -Invoke -ModuleName Orchestrator Set-DefaultPlaybackDeviceByName -Times 1 -Exactly
        }

        It 'switchOnConnect が false なら切り替えないが状態は更新する' {
            $script:Config.switchOnConnect = $false
            Write-LogLines -Lines @()
            $ctx = New-AutoSwitchContext -Config $script:Config
            $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx

            Write-LogLines -Append -Lines @((New-JsonEventLine -LogId 'c1' -TimeStamp 1000 -ActionTypeId 23011))
            $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx

            $ctx.BudsState | Should -Be 'Connected'
            Should -Invoke -ModuleName Orchestrator Set-DefaultPlaybackDeviceByName -Times 0 -Exactly
        }

        It 'switchOnDisconnect が false なら切り替えないが状態は更新する' {
            $script:Config.switchOnDisconnect = $false
            Write-LogLines -Lines @()
            $ctx = New-AutoSwitchContext -Config $script:Config
            $ctx.BudsState = 'Connected'
            $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx

            Write-LogLines -Append -Lines @((New-JsonEventLine -LogId 'd1' -TimeStamp 1000 -ActionTypeId 23012))
            $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx

            $ctx.BudsState | Should -Be 'Disconnected'
            Should -Invoke -ModuleName Orchestrator Set-DefaultPlaybackDeviceByName -Times 0 -Exactly
        }

        It '1 tick に複数イベントがあれば追記順に適用し最終状態へ落ち着く' {
            Write-LogLines -Lines @()
            $ctx = New-AutoSwitchContext -Config $script:Config
            $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx

            Write-LogLines -Append -Lines @(
                (New-JsonEventLine -LogId 'm1' -TimeStamp 1000 -ActionTypeId 23011),
                (New-JsonEventLine -LogId 'm2' -TimeStamp 2000 -ActionTypeId 23012),
                (New-JsonEventLine -LogId 'm3' -TimeStamp 3000 -ActionTypeId 23011)
            )
            $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx

            $ctx.BudsState | Should -Be 'Connected'
            Should -Invoke -ModuleName Orchestrator Set-DefaultPlaybackDeviceByName -Times 3 -Exactly
        }

        It 'truncate 後に残った行を読み直しても再発火しない' {
            # 2 行書いて両方を処理させる（Connected -> Disconnected の 2 回切り替わる）
            Write-LogLines -Lines @(
                (New-JsonEventLine -LogId 't1' -TimeStamp 1000 -ActionTypeId 23011),
                (New-JsonEventLine -LogId 't2' -TimeStamp 2000 -ActionTypeId 23012)
            )
            $ctx = New-AutoSwitchContext -Config $script:Config
            $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx
            $ctx.BudsState | Should -Be 'Disconnected'
            Should -Invoke -ModuleName Orchestrator Set-DefaultPlaybackDeviceByName -Times 2 -Exactly

            # Hub が truncate して 1 行目だけを書き直した状況（Length < Offset になる）
            Write-LogLines -Lines @((New-JsonEventLine -LogId 't1' -TimeStamp 1000 -ActionTypeId 23011))
            $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx

            # t1 は既読なので再発火しない。状態も Disconnected のまま
            $ctx.BudsState | Should -Be 'Disconnected'
            Should -Invoke -ModuleName Orchestrator Set-DefaultPlaybackDeviceByName -Times 2 -Exactly
        }

        It 'ファイルが無ければ idle 間隔になり状態は保持される' {
            Write-LogLines -Lines @((New-JsonEventLine -LogId 'k1' -TimeStamp 1000 -ActionTypeId 23011))
            $ctx = New-AutoSwitchContext -Config $script:Config
            $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx

            Remove-Item -LiteralPath $script:LogPath -Force
            $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx

            $ctx.BudsState        | Should -Be 'Connected'
            $ctx.NextDelaySeconds | Should -Be $script:Config.idlePollIntervalSeconds
        }

        It '読み取りエラーが続くと遅延が 1 → 2 → 4 と伸びる' {
            # ActionLogReader は Get-Item を呼ばないので Mock では注入できない。
            # FileShare.None で掴んだハンドルを保持し、実際に開けない状態を作る。
            Write-LogLines -Lines @()
            $ctx = New-AutoSwitchContext -Config $script:Config

            $blocker = [System.IO.File]::Open($script:LogPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
            try {
                $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx
                $ctx.NextDelaySeconds | Should -Be 1
                $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx
                $ctx.NextDelaySeconds | Should -Be 2
                $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx
                $ctx.NextDelaySeconds | Should -Be 4
            }
            finally {
                $blocker.Dispose()
            }
        }

        It 'バックオフは 60 秒で頭打ちになる' {
            Write-LogLines -Lines @()
            $ctx = New-AutoSwitchContext -Config $script:Config

            $blocker = [System.IO.File]::Open($script:LogPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
            try {
                foreach ($i in 1..12) { $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx }
                $ctx.NextDelaySeconds | Should -Be 60
            }
            finally {
                $blocker.Dispose()
            }
        }

        It 'エラーのあと成功したら遅延が通常間隔へ戻る' {
            Write-LogLines -Lines @()
            $ctx = New-AutoSwitchContext -Config $script:Config

            $blocker = [System.IO.File]::Open($script:LogPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
            try {
                $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx
                $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx
                $ctx.NextDelaySeconds | Should -Be 2
            }
            finally {
                $blocker.Dispose()
            }

            # ロックが外れたので次の tick は成功する
            $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx
            $ctx.ConsecutiveErrors | Should -Be 0
            $ctx.NextDelaySeconds  | Should -Be $script:Config.pollIntervalSeconds
        }

        It '長時間のギャップを検知したらバックオフをリセットする（スリープ復帰）' {
            Write-LogLines -Lines @()
            $ctx = New-AutoSwitchContext -Config $script:Config

            $blocker = [System.IO.File]::Open($script:LogPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
            try {
                foreach ($i in 1..5) { $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx }
                $ctx.ConsecutiveErrors | Should -BeGreaterThan 1
            }
            finally {
                $blocker.Dispose()
            }

            # 1 時間後に復帰
            $script:NowUtc = $script:NowUtc.AddHours(1)
            $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx

            $ctx.ConsecutiveErrors | Should -Be 0
            $ctx.NextDelaySeconds  | Should -Be $script:Config.pollIntervalSeconds
        }

        It '切り替えが DeviceNotFound でも状態は遷移済みとして記録し再試行で暴れない' {
            Mock -ModuleName Orchestrator Set-DefaultPlaybackDeviceByName {
                param($Name)
                return @{ Result = 'DeviceNotFound'; Message = "not found: $Name" }
            }
            Write-LogLines -Lines @()
            $ctx = New-AutoSwitchContext -Config $script:Config
            $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx

            Write-LogLines -Append -Lines @((New-JsonEventLine -LogId 'n1' -TimeStamp 1000 -ActionTypeId 23011))
            $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx
            $ctx = Invoke-AutoSwitchTick -Config $script:Config -Context $ctx

            $ctx.BudsState | Should -Be 'Connected'
            Should -Invoke -ModuleName Orchestrator Set-DefaultPlaybackDeviceByName -Times 1 -Exactly
        }
    }
}
