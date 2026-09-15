BeforeAll {
    $script:RepoRoot = Split-Path -Parent $PSScriptRoot
    Import-Module (Join-Path $script:RepoRoot 'src\modules\SonyEventProvider.psm1') -Force -DisableNameChecking
    $script:FixturePath = Join-Path $PSScriptRoot 'fixtures\actionlog-sample.log'

    function New-EventLine {
        param([string]$LogId, [long]$TimeStamp, [int]$ActionTypeId, [string]$ActionTypeName)
        return ('{{"logVersion":"1.5","logId":"{0}","timeStamp":{1},"actionTypeId":{2},"actionTypeName":"{3}"}}' -f $LogId, $TimeStamp, $ActionTypeId, $ActionTypeName)
    }
}

Describe 'Add-SeenLogId' {

    It '初回は true、2 回目は false を返す' {
        $set = New-SeenLogIdSet -Capacity 10
        Add-SeenLogId -Set $set -LogId 'a' | Should -BeTrue
        Add-SeenLogId -Set $set -LogId 'a' | Should -BeFalse
    }

    It '容量を超えたら最古から破棄する' {
        $set = New-SeenLogIdSet -Capacity 3
        Add-SeenLogId -Set $set -LogId 'a' | Out-Null
        Add-SeenLogId -Set $set -LogId 'b' | Out-Null
        Add-SeenLogId -Set $set -LogId 'c' | Out-Null
        Add-SeenLogId -Set $set -LogId 'd' | Out-Null   # a が押し出される
        $set.Lookup.Count | Should -Be 3
        Add-SeenLogId -Set $set -LogId 'a' | Should -BeTrue    # 破棄済みなので再び新規
        Add-SeenLogId -Set $set -LogId 'c' | Should -BeFalse   # まだ残っている
    }

    It '既定容量は 200' {
        (New-SeenLogIdSet).Capacity | Should -Be 200
    }
}

Describe 'Get-BudsEventsFromLines' {

    It '23011 を Connected、23012 を Disconnected に変換する' {
        $set = New-SeenLogIdSet
        $lines = @(
            (New-EventLine -LogId 'id-1' -TimeStamp 1000 -ActionTypeId 23011 -ActionTypeName 'ConnectDevice'),
            (New-EventLine -LogId 'id-2' -TimeStamp 2000 -ActionTypeId 23012 -ActionTypeName 'DisconnectDevice')
        )
        $events = @(Get-BudsEventsFromLines -Lines $lines -SeenLogIds $set)
        $events.Count          | Should -Be 2
        $events[0].State       | Should -Be 'Connected'
        $events[0].LogId       | Should -Be 'id-1'
        $events[0].TimeStampMs | Should -Be 1000
        $events[1].State       | Should -Be 'Disconnected'
        $events[1].TimeStampMs | Should -Be 2000
    }

    It '対象外の actionTypeId を無視する' {
        $set = New-SeenLogIdSet
        $lines = @(
            (New-EventLine -LogId 'id-1' -TimeStamp 1000 -ActionTypeId 23004 -ActionTypeName 'MdrSetting'),
            (New-EventLine -LogId 'id-2' -TimeStamp 2000 -ActionTypeId 23014 -ActionTypeName 'MdrStatus'),
            (New-EventLine -LogId 'id-3' -TimeStamp 3000 -ActionTypeId 23001 -ActionTypeName 'DisplayedScreen')
        )
        @(Get-BudsEventsFromLines -Lines $lines -SeenLogIds $set).Count | Should -Be 0
    }

    It '不正な JSON 行を読み飛ばし、正常な行は処理する' {
        $set = New-SeenLogIdSet
        $lines = @(
            '{"actionTypeId":23011, this is broken',
            (New-EventLine -LogId 'id-ok' -TimeStamp 5000 -ActionTypeId 23011 -ActionTypeName 'ConnectDevice')
        )
        $events = @(Get-BudsEventsFromLines -Lines $lines -SeenLogIds $set)
        $events.Count    | Should -Be 1
        $events[0].LogId | Should -Be 'id-ok'
    }

    It '同じ logId を二度返さない' {
        $set = New-SeenLogIdSet
        $line = New-EventLine -LogId 'dup-1' -TimeStamp 1000 -ActionTypeId 23011 -ActionTypeName 'ConnectDevice'
        @(Get-BudsEventsFromLines -Lines @($line) -SeenLogIds $set).Count | Should -Be 1
        @(Get-BudsEventsFromLines -Lines @($line) -SeenLogIds $set).Count | Should -Be 0
    }

    It '同一呼び出し内の重複も排除する' {
        $set = New-SeenLogIdSet
        $line = New-EventLine -LogId 'dup-2' -TimeStamp 1000 -ActionTypeId 23011 -ActionTypeName 'ConnectDevice'
        @(Get-BudsEventsFromLines -Lines @($line, $line) -SeenLogIds $set).Count | Should -Be 1
    }

    It 'logId が無い行を無視する' {
        $set = New-SeenLogIdSet
        $lines = @('{"timeStamp":1000,"actionTypeId":23011,"actionTypeName":"ConnectDevice"}')
        @(Get-BudsEventsFromLines -Lines $lines -SeenLogIds $set).Count | Should -Be 0
    }

    It '空配列と $null を安全に扱う' {
        $set = New-SeenLogIdSet
        @(Get-BudsEventsFromLines -Lines @()   -SeenLogIds $set).Count | Should -Be 0
        @(Get-BudsEventsFromLines -Lines $null -SeenLogIds $set).Count | Should -Be 0
    }

    It '実ログ由来の fixture から出現順どおりに 4 件を返す' {
        $set = New-SeenLogIdSet
        $lines = @(Get-Content -LiteralPath $script:FixturePath -Encoding UTF8 | Where-Object { $_.Trim().Length -gt 0 })
        $events = @(Get-BudsEventsFromLines -Lines $lines -SeenLogIds $set)
        $events.Count | Should -Be 4
        @($events | ForEach-Object { $_.State }) | Should -Be @('Connected', 'Disconnected', 'Connected', 'Connected')
        $events[0].TimeStampMs | Should -Be 1789454166382
        $events[3].TimeStampMs | Should -Be 1789454196511
    }

    It 'fixture を二度読んでも二度目は 0 件になる（truncate 再読対策）' {
        $set = New-SeenLogIdSet
        $lines = @(Get-Content -LiteralPath $script:FixturePath -Encoding UTF8 | Where-Object { $_.Trim().Length -gt 0 })
        @(Get-BudsEventsFromLines -Lines $lines -SeenLogIds $set).Count | Should -Be 4
        @(Get-BudsEventsFromLines -Lines $lines -SeenLogIds $set).Count | Should -Be 0
    }
}
