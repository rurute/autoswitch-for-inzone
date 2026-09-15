BeforeAll {
    $script:RepoRoot = Split-Path -Parent $PSScriptRoot
    Import-Module (Join-Path $script:RepoRoot 'src\modules\Logging.psm1') -Force -DisableNameChecking
}

Describe 'Write-AutoSwitchLog' {

    BeforeEach {
        $script:LogDir = Join-Path ([System.IO.Path]::GetTempPath()) ("log-" + [guid]::NewGuid().ToString())
        $script:LogFile = Join-Path $script:LogDir 'test.log'
    }

    AfterEach {
        if (Test-Path -LiteralPath $script:LogDir) {
            Remove-Item -LiteralPath $script:LogDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'ディレクトリが無くても作ってから書く' {
        Initialize-AutoSwitchLog -Path $script:LogFile -Level 'Info' -MaxSizeKB 1024
        Write-AutoSwitchLog -Level Info -Message 'hello'
        Test-Path -LiteralPath $script:LogFile | Should -BeTrue
    }

    It '仕様どおりの行フォーマットで書く' {
        Initialize-AutoSwitchLog -Path $script:LogFile -Level 'Info' -MaxSizeKB 1024
        Write-AutoSwitchLog -Level Warn -Message 'デバイスが見つかりません'
        $line = @(Get-Content -LiteralPath $script:LogFile -Encoding UTF8)[0]
        $line | Should -Match '^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[WARN\] デバイスが見つかりません$'
    }

    It 'logLevel 未満のメッセージを書かない' {
        Initialize-AutoSwitchLog -Path $script:LogFile -Level 'Warn' -MaxSizeKB 1024
        Write-AutoSwitchLog -Level Debug -Message 'debug msg'
        Write-AutoSwitchLog -Level Info  -Message 'info msg'
        Write-AutoSwitchLog -Level Error -Message 'error msg'
        $content = Get-Content -LiteralPath $script:LogFile -Encoding UTF8 -Raw
        $content | Should -Not -Match 'debug msg'
        $content | Should -Not -Match 'info msg'
        $content | Should -Match 'error msg'
    }

    It '同一メッセージの連続を抑制する' {
        Initialize-AutoSwitchLog -Path $script:LogFile -Level 'Info' -MaxSizeKB 1024
        1..5 | ForEach-Object { Write-AutoSwitchLog -Level Warn -Message 'same message' }
        $lines = @(Get-Content -LiteralPath $script:LogFile -Encoding UTF8)
        $lines.Count | Should -Be 1
    }

    It '抑制中に別の文言が来たら抑制件数を先に出力する' {
        Initialize-AutoSwitchLog -Path $script:LogFile -Level 'Info' -MaxSizeKB 1024
        1..4 | ForEach-Object { Write-AutoSwitchLog -Level Warn -Message 'same message' }
        Write-AutoSwitchLog -Level Info -Message 'different message'
        $lines = @(Get-Content -LiteralPath $script:LogFile -Encoding UTF8)
        $lines.Count | Should -Be 3
        $lines[0] | Should -Match 'same message'
        $lines[1] | Should -Match '同一メッセージを 3 回抑制'
        $lines[2] | Should -Match 'different message'
    }

    It 'サイズ超過で .1 へローテーションする' {
        Initialize-AutoSwitchLog -Path $script:LogFile -Level 'Info' -MaxSizeKB 1
        1..80 | ForEach-Object { Write-AutoSwitchLog -Level Info -Message ("msg {0} {1}" -f $_, ('x' * 40)) }
        Test-Path -LiteralPath "$($script:LogFile).1" | Should -BeTrue
        # ローテーションは追記の「前」に判定するので、直後は上限を 1 行分だけ超えうる
        (Get-Item -LiteralPath $script:LogFile).Length | Should -BeLessThan 2048
    }

    It 'ローテーションの世代は 1 つだけで .2 は作らない' {
        Initialize-AutoSwitchLog -Path $script:LogFile -Level 'Info' -MaxSizeKB 1
        1..400 | ForEach-Object { Write-AutoSwitchLog -Level Info -Message ("msg {0} {1}" -f $_, ('x' * 40)) }
        Test-Path -LiteralPath "$($script:LogFile).1" | Should -BeTrue
        Test-Path -LiteralPath "$($script:LogFile).2" | Should -BeFalse
    }

    It 'Initialize 前に呼ばれても例外を投げない' {
        Reset-AutoSwitchLogState
        { Write-AutoSwitchLog -Level Info -Message 'no init' } | Should -Not -Throw
    }

    It '書き込めないパスでも例外を投げない' {
        Initialize-AutoSwitchLog -Path 'Z:\definitely\not\here\x.log' -Level 'Info' -MaxSizeKB 1024
        { Write-AutoSwitchLog -Level Error -Message 'boom' } | Should -Not -Throw
    }

    It '別プロセスが書き込み用に掴んでいてもログ行を失わない' {
        Initialize-AutoSwitchLog -Path $script:LogFile -Level 'Info' -MaxSizeKB 1024
        Write-AutoSwitchLog -Level Info -Message 'first line'

        # 他プロセスが追記用に開いている状況を模す
        $other = [System.IO.File]::Open(
            $script:LogFile,
            [System.IO.FileMode]::Append,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::ReadWrite)
        try {
            Write-AutoSwitchLog -Level Info -Message 'second line'
        }
        finally {
            $other.Dispose()
        }

        $content = Get-Content -LiteralPath $script:LogFile -Encoding UTF8 -Raw
        $content | Should -Match 'first line'
        $content | Should -Match 'second line'
    }
}
