BeforeAll {
    $script:RepoRoot = Split-Path -Parent $PSScriptRoot

    # 実機の AudioDeviceCmdlets が入っていても、必ずスタブが解決されるよう最後に読み込む
    Import-Module (Join-Path $PSScriptRoot 'stubs\AudioDeviceCmdletsStub.psm1') -Global -Force -DisableNameChecking
    Import-Module (Join-Path $script:RepoRoot 'src\modules\AudioSwitcher.psm1') -Force -DisableNameChecking

    # Mock のスクリプトブロックは AudioSwitcher モジュールのスコープで実行されるため、
    # ヘルパは global に定義しないと解決できない
    function global:New-FakeDevice {
        param([string]$Name, [string]$Type = 'Playback', [bool]$Default = $false)
        return [pscustomobject]@{ Name = $Name; Type = $Type; Default = $Default; ID = "id-$Name" }
    }
}

Describe 'Set-DefaultPlaybackDeviceByName' {

    It '既定でないデバイスへ切り替えると Switched になり Set-AudioDevice が 1 回だけ呼ばれる' {
        Mock -ModuleName AudioSwitcher Get-AudioDevice {
            param($List, $Playback, $Recording, $ID, $Index)
            if ($Playback) { return (New-FakeDevice -Name 'スピーカー (Realtek(R) Audio)' -Default $true) }
            return @(
                (New-FakeDevice -Name 'スピーカー (Realtek(R) Audio)' -Default $true),
                (New-FakeDevice -Name 'スピーカー (INZONE Buds - Game)')
            )
        }
        Mock -ModuleName AudioSwitcher Set-AudioDevice { }

        $r = Set-DefaultPlaybackDeviceByName -Name 'スピーカー (INZONE Buds - Game)'
        $r.Result | Should -Be 'Switched'
        Should -Invoke -ModuleName AudioSwitcher Set-AudioDevice -Times 1 -Exactly
    }

    It '既に既定なら AlreadyDefault になり Set-AudioDevice を呼ばない' {
        Mock -ModuleName AudioSwitcher Get-AudioDevice {
            param($List, $Playback, $Recording, $ID, $Index)
            if ($Playback) { return (New-FakeDevice -Name 'スピーカー (INZONE Buds - Game)' -Default $true) }
            return @((New-FakeDevice -Name 'スピーカー (INZONE Buds - Game)' -Default $true))
        }
        Mock -ModuleName AudioSwitcher Set-AudioDevice { }

        $r = Set-DefaultPlaybackDeviceByName -Name 'スピーカー (INZONE Buds - Game)'
        $r.Result | Should -Be 'AlreadyDefault'
        Should -Invoke -ModuleName AudioSwitcher Set-AudioDevice -Times 0 -Exactly
    }

    It '一覧に無い名前なら DeviceNotFound になり Set-AudioDevice を呼ばない' {
        Mock -ModuleName AudioSwitcher Get-AudioDevice {
            param($List, $Playback, $Recording, $ID, $Index)
            if ($Playback) { return (New-FakeDevice -Name 'スピーカー (Realtek(R) Audio)' -Default $true) }
            return @((New-FakeDevice -Name 'スピーカー (Realtek(R) Audio)' -Default $true))
        }
        Mock -ModuleName AudioSwitcher Set-AudioDevice { }

        $r = Set-DefaultPlaybackDeviceByName -Name '存在しないデバイス'
        $r.Result  | Should -Be 'DeviceNotFound'
        $r.Message | Should -Match '存在しないデバイス'
        Should -Invoke -ModuleName AudioSwitcher Set-AudioDevice -Times 0 -Exactly
    }

    It '同名でも Playback 以外は選ばない' {
        Mock -ModuleName AudioSwitcher Get-AudioDevice {
            param($List, $Playback, $Recording, $ID, $Index)
            if ($Playback) { return (New-FakeDevice -Name 'スピーカー (Realtek(R) Audio)' -Default $true) }
            return @(
                (New-FakeDevice -Name 'スピーカー (Realtek(R) Audio)' -Default $true),
                (New-FakeDevice -Name 'INZONE Buds' -Type 'Recording')
            )
        }
        Mock -ModuleName AudioSwitcher Set-AudioDevice { }

        (Set-DefaultPlaybackDeviceByName -Name 'INZONE Buds').Result | Should -Be 'DeviceNotFound'
    }

    It 'モジュールが使えないときは ModuleUnavailable を返す' {
        Mock -ModuleName AudioSwitcher Test-AudioSwitcherAvailable { return $false }
        $r = Set-DefaultPlaybackDeviceByName -Name 'なんでもよい'
        $r.Result | Should -Be 'ModuleUnavailable'
    }

    It 'Set-AudioDevice が例外を投げても Error を返して例外を漏らさない' {
        Mock -ModuleName AudioSwitcher Get-AudioDevice {
            param($List, $Playback, $Recording, $ID, $Index)
            if ($Playback) { return (New-FakeDevice -Name 'スピーカー (Realtek(R) Audio)' -Default $true) }
            return @((New-FakeDevice -Name 'スピーカー (INZONE Buds - Game)'))
        }
        Mock -ModuleName AudioSwitcher Set-AudioDevice { throw 'simulated device failure' }

        $r = $null
        { $script:r = Set-DefaultPlaybackDeviceByName -Name 'スピーカー (INZONE Buds - Game)' } | Should -Not -Throw
        $script:r.Result  | Should -Be 'Error'
        $script:r.Message | Should -Match 'simulated device failure'
    }

    It '現在の既定を取得できなくても切り替えを試みる' {
        Mock -ModuleName AudioSwitcher Get-AudioDevice {
            param($List, $Playback, $Recording, $ID, $Index)
            if ($Playback) { throw 'no default endpoint' }
            return @((New-FakeDevice -Name 'スピーカー (INZONE Buds - Game)'))
        }
        Mock -ModuleName AudioSwitcher Set-AudioDevice { }

        (Set-DefaultPlaybackDeviceByName -Name 'スピーカー (INZONE Buds - Game)').Result | Should -Be 'Switched'
    }
}

Describe 'Get-DefaultPlaybackDeviceName' {

    It '現在の既定デバイス名を返す' {
        Mock -ModuleName AudioSwitcher Get-AudioDevice {
            return (New-FakeDevice -Name 'スピーカー (Realtek(R) Audio)' -Default $true)
        }
        Get-DefaultPlaybackDeviceName | Should -Be 'スピーカー (Realtek(R) Audio)'
    }

    It '取得に失敗したら $null を返し例外を漏らさない' {
        Mock -ModuleName AudioSwitcher Get-AudioDevice { throw 'boom' }
        $r = 'sentinel'
        { $script:r = Get-DefaultPlaybackDeviceName } | Should -Not -Throw
        $script:r | Should -BeNullOrEmpty
    }

    It 'モジュールが使えないときは $null を返す' {
        Mock -ModuleName AudioSwitcher Test-AudioSwitcherAvailable { return $false }
        Get-DefaultPlaybackDeviceName | Should -BeNullOrEmpty
    }
}
