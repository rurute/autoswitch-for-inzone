BeforeAll {
    $script:RepoRoot = Split-Path -Parent $PSScriptRoot
    Import-Module (Join-Path $script:RepoRoot 'src\modules\Config.psm1') -Force -DisableNameChecking

    function New-TempConfig {
        param([string]$Content)
        $path = Join-Path ([System.IO.Path]::GetTempPath()) ("cfg-" + [guid]::NewGuid().ToString() + ".json")
        [System.IO.File]::WriteAllText($path, $Content, (New-Object System.Text.UTF8Encoding($true)))
        return $path
    }
}

Describe 'Get-AutoSwitchConfigDefault' {
    It '仕様どおりの既定値を返す' {
        $d = Get-AutoSwitchConfigDefault
        $d.budsDevice               | Should -Be 'スピーカー (INZONE Buds - Game)'
        $d.speakerDevice            | Should -Be 'スピーカー (Realtek(R) Audio)'
        $d.switchOnConnect          | Should -BeTrue
        $d.switchOnDisconnect       | Should -BeTrue
        $d.actionLogPath            | Should -Be '%APPDATA%\Sony\INZONE Hub\ActionLog.log'
        $d.pollIntervalSeconds      | Should -Be 1
        $d.idlePollIntervalSeconds  | Should -Be 5
        $d.startupSyncMaxAgeMinutes | Should -Be 10
        $d.logLevel                 | Should -Be 'Info'
        $d.maxLogSizeKB             | Should -Be 1024
    }

    It '呼ぶたびに独立したコピーを返す' {
        $a = Get-AutoSwitchConfigDefault
        $a.budsDevice = 'CHANGED'
        (Get-AutoSwitchConfigDefault).budsDevice | Should -Be 'スピーカー (INZONE Buds - Game)'
    }
}

Describe 'Read-AutoSwitchConfig' {

    It '完全な JSON を読める' {
        $p = New-TempConfig -Content @'
{
  "budsDevice": "Buds A",
  "speakerDevice": "Speaker B",
  "switchOnConnect": false,
  "switchOnDisconnect": false,
  "actionLogPath": "C:\\tmp\\a.log",
  "pollIntervalSeconds": 2,
  "idlePollIntervalSeconds": 9,
  "startupSyncMaxAgeMinutes": 30,
  "logLevel": "Debug",
  "maxLogSizeKB": 512
}
'@
        $r = Read-AutoSwitchConfig -Path $p
        $r.Warnings.Count                  | Should -Be 0
        $r.Config.budsDevice               | Should -Be 'Buds A'
        $r.Config.speakerDevice            | Should -Be 'Speaker B'
        $r.Config.switchOnConnect          | Should -BeFalse
        $r.Config.switchOnDisconnect       | Should -BeFalse
        $r.Config.actionLogPath            | Should -Be 'C:\tmp\a.log'
        $r.Config.pollIntervalSeconds      | Should -Be 2
        $r.Config.idlePollIntervalSeconds  | Should -Be 9
        $r.Config.startupSyncMaxAgeMinutes | Should -Be 30
        $r.Config.logLevel                 | Should -Be 'Debug'
        $r.Config.maxLogSizeKB             | Should -Be 512
        Remove-Item $p -Force
    }

    It '欠損キーを既定値で補完する' {
        $p = New-TempConfig -Content '{ "budsDevice": "Only This" }'
        $r = Read-AutoSwitchConfig -Path $p
        $r.Config.budsDevice          | Should -Be 'Only This'
        $r.Config.speakerDevice       | Should -Be 'スピーカー (Realtek(R) Audio)'
        $r.Config.pollIntervalSeconds | Should -Be 1
        Remove-Item $p -Force
    }

    It '未知のキーを無視して警告する' {
        $p = New-TempConfig -Content '{ "budsDevice": "X", "totallyUnknownKey": 1 }'
        $r = Read-AutoSwitchConfig -Path $p
        $r.Config.ContainsKey('totallyUnknownKey') | Should -BeFalse
        ($r.Warnings -join "`n") | Should -Match 'totallyUnknownKey'
        Remove-Item $p -Force
    }

    It 'actionLogPath の環境変数を展開する' {
        $p = New-TempConfig -Content '{ "actionLogPath": "%APPDATA%\\Sony\\INZONE Hub\\ActionLog.log" }'
        $r = Read-AutoSwitchConfig -Path $p
        $r.Config.actionLogPath | Should -Not -Match '%APPDATA%'
        $r.Config.actionLogPath | Should -Be (Join-Path $env:APPDATA 'Sony\INZONE Hub\ActionLog.log')
        Remove-Item $p -Force
    }

    It '範囲外の数値を既定値へ戻して警告する' {
        $p = New-TempConfig -Content '{ "pollIntervalSeconds": 0, "maxLogSizeKB": 999999999 }'
        $r = Read-AutoSwitchConfig -Path $p
        $r.Config.pollIntervalSeconds | Should -Be 1
        $r.Config.maxLogSizeKB        | Should -Be 1024
        ($r.Warnings -join "`n") | Should -Match 'pollIntervalSeconds'
        ($r.Warnings -join "`n") | Should -Match 'maxLogSizeKB'
        Remove-Item $p -Force
    }

    It '型が違う値を既定値へ戻して警告する' {
        $p = New-TempConfig -Content '{ "pollIntervalSeconds": "abc", "switchOnConnect": "yes", "budsDevice": "" }'
        $r = Read-AutoSwitchConfig -Path $p
        $r.Config.pollIntervalSeconds | Should -Be 1
        $r.Config.switchOnConnect     | Should -BeTrue
        $r.Config.budsDevice          | Should -Be 'スピーカー (INZONE Buds - Game)'
        $r.Warnings.Count | Should -BeGreaterThan 2
        Remove-Item $p -Force
    }

    It 'startupSyncMaxAgeMinutes = 0 は有効な値として受け入れる' {
        $p = New-TempConfig -Content '{ "startupSyncMaxAgeMinutes": 0 }'
        $r = Read-AutoSwitchConfig -Path $p
        $r.Config.startupSyncMaxAgeMinutes | Should -Be 0
        Remove-Item $p -Force
    }

    It '不正な logLevel を既定値へ戻して警告する' {
        $p = New-TempConfig -Content '{ "logLevel": "Verbose" }'
        $r = Read-AutoSwitchConfig -Path $p
        $r.Config.logLevel | Should -Be 'Info'
        ($r.Warnings -join "`n") | Should -Match 'logLevel'
        Remove-Item $p -Force
    }

    It 'ファイルが存在しなくても例外を投げず既定値を返す' {
        $p = Join-Path ([System.IO.Path]::GetTempPath()) ("missing-" + [guid]::NewGuid().ToString() + ".json")
        $r = Read-AutoSwitchConfig -Path $p
        $r.Config.budsDevice | Should -Be 'スピーカー (INZONE Buds - Game)'
        $r.Warnings.Count    | Should -BeGreaterThan 0
    }

    It 'JSON が壊れていても例外を投げず既定値を返す' {
        $p = New-TempConfig -Content '{ this is not json '
        $r = Read-AutoSwitchConfig -Path $p
        $r.Config.budsDevice | Should -Be 'スピーカー (INZONE Buds - Game)'
        ($r.Warnings -join "`n") | Should -Match '解析'
        Remove-Item $p -Force
    }

    It 'BOM 付き UTF-8 の日本語デバイス名を正しく読む' {
        $p = New-TempConfig -Content '{ "budsDevice": "スピーカー (INZONE Buds - Game)" }'
        $r = Read-AutoSwitchConfig -Path $p
        $r.Config.budsDevice | Should -Be 'スピーカー (INZONE Buds - Game)'
        Remove-Item $p -Force
    }
}
