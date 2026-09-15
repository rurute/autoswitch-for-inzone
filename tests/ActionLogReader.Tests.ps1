BeforeAll {
    $script:RepoRoot = Split-Path -Parent $PSScriptRoot
    Import-Module (Join-Path $script:RepoRoot 'src\modules\ActionLogReader.psm1') -Force -DisableNameChecking

    function Set-LogContent {
        param([string]$Path, [string]$Text, [switch]$Bom)
        $encoding = New-Object System.Text.UTF8Encoding($Bom.IsPresent)
        [System.IO.File]::WriteAllText($Path, $Text, $encoding)
    }

    function Add-LogContent {
        param([string]$Path, [string]$Text)
        $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($Text)
        $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite)
        try { $fs.Write($bytes, 0, $bytes.Length) } finally { $fs.Dispose() }
    }
}

Describe 'ActionLogReader' {

    BeforeEach {
        $script:Dir  = Join-Path ([System.IO.Path]::GetTempPath()) ("alr-" + [guid]::NewGuid().ToString())
        New-Item -ItemType Directory -Path $script:Dir -Force | Out-Null
        $script:Path = Join-Path $script:Dir 'ActionLog.log'
    }

    AfterEach {
        Remove-Item -LiteralPath $script:Dir -Recurse -Force -ErrorAction SilentlyContinue
    }

    Describe 'New-ActionLogReaderState' {
        It '初期状態を返す' {
            $s = New-ActionLogReaderState -Path $script:Path
            $s.Path            | Should -Be $script:Path
            $s.Offset          | Should -Be 0
            $s.LastLength      | Should -Be 0
            $s.HeadFingerprint | Should -Be ''
        }
    }

    Describe 'Read-ActionLogLines' {

        It 'ファイルが無ければ FileMissing を返して状態をリセットする' {
            $s = New-ActionLogReaderState -Path $script:Path
            $s.Offset = 999
            $s.HeadFingerprint = 'stale'
            $r = Read-ActionLogLines -State $s
            $r.Status                | Should -Be 'FileMissing'
            @($r.Lines).Count        | Should -Be 0
            $r.State.Offset          | Should -Be 0
            $r.State.HeadFingerprint | Should -Be ''
        }

        It '初回は全行を返す' {
            Set-LogContent -Path $script:Path -Text "line1`nline2`nline3`n"
            $s = New-ActionLogReaderState -Path $script:Path
            $r = Read-ActionLogLines -State $s
            $r.Status | Should -Be 'Ok'
            @($r.Lines) | Should -Be @('line1', 'line2', 'line3')
        }

        It '2 回目は追記分だけを返す' {
            Set-LogContent -Path $script:Path -Text "line1`nline2`n"
            $s = New-ActionLogReaderState -Path $script:Path
            Read-ActionLogLines -State $s | Out-Null
            Add-LogContent -Path $script:Path -Text "line3`n"
            $r = Read-ActionLogLines -State $s
            @($r.Lines) | Should -Be @('line3')
        }

        It '追記が無ければ空を返す' {
            Set-LogContent -Path $script:Path -Text "line1`n"
            $s = New-ActionLogReaderState -Path $script:Path
            Read-ActionLogLines -State $s | Out-Null
            $r = Read-ActionLogLines -State $s
            $r.Status         | Should -Be 'Ok'
            @($r.Lines).Count | Should -Be 0
        }

        It '通常の追記を Recreated と誤判定しない' {
            Set-LogContent -Path $script:Path -Text "aaa`n"
            $s = New-ActionLogReaderState -Path $script:Path
            Read-ActionLogLines -State $s | Out-Null

            foreach ($i in 1..5) {
                Add-LogContent -Path $script:Path -Text ("line{0}`n" -f $i)
                $r = Read-ActionLogLines -State $s
                $r.Status   | Should -Be 'Ok'
                @($r.Lines) | Should -Be @(("line{0}" -f $i))
            }
        }

        It '既読オフセットが指紋長 256 バイトを超えても追記を正しく読み続ける' {
            $long = ('x' * 400)
            Set-LogContent -Path $script:Path -Text "$long`n"
            $s = New-ActionLogReaderState -Path $script:Path
            $r = Read-ActionLogLines -State $s
            @($r.Lines) | Should -Be @($long)
            $s.Offset | Should -BeGreaterThan 256

            Add-LogContent -Path $script:Path -Text "after`n"
            $r2 = Read-ActionLogLines -State $s
            $r2.Status   | Should -Be 'Ok'
            @($r2.Lines) | Should -Be @('after')
        }

        It '改行で終わっていない末尾行は返さずオフセットも進めない' {
            Set-LogContent -Path $script:Path -Text "line1`nincomplete"
            $s = New-ActionLogReaderState -Path $script:Path
            $r = Read-ActionLogLines -State $s
            @($r.Lines) | Should -Be @('line1')
            $offsetAfterFirst = $r.State.Offset

            Add-LogContent -Path $script:Path -Text "-now-complete`n"
            $r2 = Read-ActionLogLines -State $s
            @($r2.Lines) | Should -Be @('incomplete-now-complete')
            $r2.State.Offset | Should -BeGreaterThan $offsetAfterFirst
        }

        It 'truncate（Length < Offset）を検知して先頭から読み直す' {
            Set-LogContent -Path $script:Path -Text "aaaa`nbbbb`ncccc`n"
            $s = New-ActionLogReaderState -Path $script:Path
            Read-ActionLogLines -State $s | Out-Null

            Set-LogContent -Path $script:Path -Text "zzzz`n"
            $r = Read-ActionLogLines -State $s
            $r.Status   | Should -Be 'Truncated'
            @($r.Lines) | Should -Be @('zzzz')
        }

        It '縮小したが Length >= Offset のままの truncate も検知する' {
            Set-LogContent -Path $script:Path -Text ("x" * 100 + "`n")
            $s = New-ActionLogReaderState -Path $script:Path
            Read-ActionLogLines -State $s | Out-Null
            $s.Offset = 10   # Hub が truncate 直後に追記した状況を模す

            Set-LogContent -Path $script:Path -Text ("y" * 50 + "`n")
            $r = Read-ActionLogLines -State $s
            $r.Status | Should -Be 'Truncated'
            @($r.Lines).Count | Should -Be 1
        }

        It '削除して作り直されたら Recreated を返して先頭から読み直す' {
            # 削除の 50ms 後に作り直す。NTFS の file system tunneling により
            # CreationTimeUtc は元の値のまま復元されるので、作成日時では検知できない窓。
            Set-LogContent -Path $script:Path -Text "old1`nold2`n"
            $s = New-ActionLogReaderState -Path $script:Path
            Read-ActionLogLines -State $s | Out-Null

            Remove-Item -LiteralPath $script:Path -Force
            Start-Sleep -Milliseconds 50
            Set-LogContent -Path $script:Path -Text "new1`nnew2`nnew3`n"

            $r = Read-ActionLogLines -State $s
            $r.Status   | Should -Be 'Recreated'
            @($r.Lines) | Should -Be @('new1', 'new2', 'new3')
        }

        It 'truncate されて元より長い別内容に差し替わっても先頭から読み直す' {
            # Length も CreationTime も手がかりにならない最悪ケース。
            # 指紋が無ければ古いオフセットから別内容の途中を「続き」として返してしまう。
            Set-LogContent -Path $script:Path -Text "old1`nold2`n"
            $s = New-ActionLogReaderState -Path $script:Path
            Read-ActionLogLines -State $s | Out-Null

            Set-LogContent -Path $script:Path -Text "new1`nnew2`nnew3`nnew4`n"
            $r = Read-ActionLogLines -State $s
            $r.Status   | Should -Be 'Recreated'
            @($r.Lines) | Should -Be @('new1', 'new2', 'new3', 'new4')
        }

        It 'UTF-8 BOM を除去する' {
            Set-LogContent -Path $script:Path -Text "first`nsecond`n" -Bom
            $s = New-ActionLogReaderState -Path $script:Path
            $r = Read-ActionLogLines -State $s
            @($r.Lines)[0] | Should -Be 'first'
            @($r.Lines)[0].ToCharArray()[0] | Should -Not -Be ([char]0xFEFF)
        }

        It 'CRLF の CR を除去する' {
            Set-LogContent -Path $script:Path -Text "aaa`r`nbbb`r`n"
            $s = New-ActionLogReaderState -Path $script:Path
            $r = Read-ActionLogLines -State $s
            @($r.Lines) | Should -Be @('aaa', 'bbb')
        }

        It '空行は返さない' {
            Set-LogContent -Path $script:Path -Text "aaa`n`n`nbbb`n"
            $s = New-ActionLogReaderState -Path $script:Path
            $r = Read-ActionLogLines -State $s
            @($r.Lines) | Should -Be @('aaa', 'bbb')
        }

        It 'マルチバイト文字を含んでもオフセットがずれない' {
            Set-LogContent -Path $script:Path -Text "スピーカー (INZONE Buds - Game)`n"
            $s = New-ActionLogReaderState -Path $script:Path
            $r = Read-ActionLogLines -State $s
            @($r.Lines) | Should -Be @('スピーカー (INZONE Buds - Game)')

            Add-LogContent -Path $script:Path -Text "スピーカー (Realtek(R) Audio)`n"
            $r2 = Read-ActionLogLines -State $s
            @($r2.Lines) | Should -Be @('スピーカー (Realtek(R) Audio)')
        }

        It '同じ長さの別内容へ差し替えられても検知する' {
            # Length も CreationTime も手がかりにならず、長さが一致するため
            # 「変化が無ければ開かない」最適化があるとここで永久にすり抜ける
            Set-LogContent -Path $script:Path -Text "old1`nold2`n"
            $s = New-ActionLogReaderState -Path $script:Path
            Read-ActionLogLines -State $s | Out-Null

            Set-LogContent -Path $script:Path -Text "new1`nnew2`n"   # 同一バイト長
            $r = Read-ActionLogLines -State $s
            $r.Status   | Should -Be 'Recreated'
            @($r.Lines) | Should -Be @('new1', 'new2')
        }

        It '読み取りが失敗したら Error を返し例外を漏らさない' {
            # Mock ではなく実際の排他ロックで失敗させる。
            # FileShare.None で開かれていると、こちらの ReadWrite|Delete 要求は IOException になる。
            Set-LogContent -Path $script:Path -Text "aaa`n"
            $s = New-ActionLogReaderState -Path $script:Path

            $blocker = [System.IO.File]::Open($script:Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
            try {
                $r = $null
                { $script:r = Read-ActionLogLines -State $s } | Should -Not -Throw
                $script:r.Status  | Should -Be 'Error'
                $script:r.Message | Should -Not -BeNullOrEmpty
            }
            finally {
                $blocker.Dispose()
            }
        }
    }
}
