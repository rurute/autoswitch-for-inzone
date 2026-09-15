BeforeAll {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    Import-Module (Join-Path $repoRoot 'src\modules\DeviceState.psm1') -Force -DisableNameChecking
}

Describe 'Resolve-StateTransition' {

    It 'Unknown から Connected は遷移とみなす' {
        $r = Resolve-StateTransition -CurrentState 'Unknown' -EventState 'Connected'
        $r.Changed  | Should -BeTrue
        $r.NewState | Should -Be 'Connected'
    }

    It 'Unknown から Disconnected は遷移とみなす' {
        $r = Resolve-StateTransition -CurrentState 'Unknown' -EventState 'Disconnected'
        $r.Changed  | Should -BeTrue
        $r.NewState | Should -Be 'Disconnected'
    }

    It 'Connected から Disconnected は遷移とみなす' {
        $r = Resolve-StateTransition -CurrentState 'Connected' -EventState 'Disconnected'
        $r.Changed  | Should -BeTrue
        $r.NewState | Should -Be 'Disconnected'
    }

    It 'Disconnected から Connected は遷移とみなす' {
        $r = Resolve-StateTransition -CurrentState 'Disconnected' -EventState 'Connected'
        $r.Changed  | Should -BeTrue
        $r.NewState | Should -Be 'Connected'
    }

    It 'Connected から Connected は遷移ではない（ConnectDevice の連続発生対策）' {
        $r = Resolve-StateTransition -CurrentState 'Connected' -EventState 'Connected'
        $r.Changed  | Should -BeFalse
        $r.NewState | Should -Be 'Connected'
    }

    It 'Disconnected から Disconnected は遷移ではない' {
        $r = Resolve-StateTransition -CurrentState 'Disconnected' -EventState 'Disconnected'
        $r.Changed  | Should -BeFalse
        $r.NewState | Should -Be 'Disconnected'
    }

    It '不正な CurrentState は例外にする' {
        { Resolve-StateTransition -CurrentState 'Bogus' -EventState 'Connected' } | Should -Throw
    }

    It '不正な EventState は例外にする（Unknown はイベントにならない）' {
        { Resolve-StateTransition -CurrentState 'Connected' -EventState 'Unknown' } | Should -Throw
    }
}
