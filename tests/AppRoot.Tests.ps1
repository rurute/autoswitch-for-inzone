BeforeAll {
    $script:RepoRoot = Split-Path -Parent $PSScriptRoot
    Import-Module (Join-Path $script:RepoRoot 'src\modules\Config.psm1') -Force -DisableNameChecking
}

Describe 'AppRoot' {

    BeforeEach {
        $script:Base = Join-Path ([System.IO.Path]::GetTempPath()) ("root-" + [guid]::NewGuid().ToString())
        New-Item -ItemType Directory -Path $script:Base -Force | Out-Null
    }

    AfterEach {
        Remove-Item -LiteralPath $script:Base -Recurse -Force -ErrorAction SilentlyContinue
    }

    Describe 'Resolve-AutoSwitchAppRoot' {

        It 'インストール後の配置: スクリプトと同じ場所に config.json があればそこ' {
            New-Item -ItemType File -Path (Join-Path $script:Base 'config.json') -Force | Out-Null
            Resolve-AutoSwitchAppRoot -ScriptRoot $script:Base | Should -Be $script:Base
        }

        It '開発時の配置: src\ の 1 つ上に config.json があればそこ' {
            $srcDir = Join-Path $script:Base 'src'
            New-Item -ItemType Directory -Path $srcDir -Force | Out-Null
            New-Item -ItemType File -Path (Join-Path $script:Base 'config.json') -Force | Out-Null
            Resolve-AutoSwitchAppRoot -ScriptRoot $srcDir | Should -Be $script:Base
        }

        It '同じ場所を優先する' {
            $srcDir = Join-Path $script:Base 'src'
            New-Item -ItemType Directory -Path $srcDir -Force | Out-Null
            New-Item -ItemType File -Path (Join-Path $script:Base 'config.json') -Force | Out-Null
            New-Item -ItemType File -Path (Join-Path $srcDir 'config.json') -Force | Out-Null
            Resolve-AutoSwitchAppRoot -ScriptRoot $srcDir | Should -Be $srcDir
        }

        It 'どこにも無ければ ScriptRoot を返す' {
            Resolve-AutoSwitchAppRoot -ScriptRoot $script:Base | Should -Be $script:Base
        }
    }

    Describe 'リポジトリの config.json' {

        It '既定値と同じ内容で、警告なしに読める' {
            $r = Read-AutoSwitchConfig -Path (Join-Path $script:RepoRoot 'config.json')
            $r.Warnings.Count            | Should -Be 0
            $r.Config.budsDevice         | Should -Be 'スピーカー (INZONE Buds - Game)'
            $r.Config.speakerDevice      | Should -Be 'スピーカー (Realtek(R) Audio)'
            $r.Config.switchOnConnect    | Should -BeTrue
            $r.Config.switchOnDisconnect | Should -BeTrue
            $r.Config.actionLogPath      | Should -Be (Join-Path $env:APPDATA 'Sony\INZONE Hub\ActionLog.log')
        }
    }
}
