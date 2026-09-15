Set-StrictMode -Version Latest

$script:MaxReadBytes     = 4194304   # 1 tick で読む上限 4MB。超過分は次の tick へ持ち越す
$script:FingerprintBytes = 256       # ファイル同一性の指紋に使う先頭バイト数

function New-ActionLogReaderState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    return @{
        Path            = $Path
        Offset          = [long]0
        LastLength      = [long]0
        HeadFingerprint = ''
    }
}

function Get-HeadFingerprint {
    <#
        既読部分（先頭 min(FingerprintBytes, Offset) バイト）の Base64。
        既読範囲だけを見るので、通常の追記では変化せず、ファイルの差し替えだけが検知できる。

        CreationTimeUtc は使わない。NTFS の file system tunneling により、削除後 15 秒以内に
        同名ファイルを作り直すと作成日時が元の値のまま復元される（実機計測で確認済み）。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][System.IO.FileStream]$Stream,
        [Parameter(Mandatory = $true)][long]$Offset
    )

    $count = [int][Math]::Min([long]$script:FingerprintBytes, $Offset)
    if ($count -le 0) { return '' }
    if ($Stream.Length -lt $count) { $count = [int]$Stream.Length }
    if ($count -le 0) { return '' }

    $buffer = New-Object byte[] $count
    [void]$Stream.Seek(0, [System.IO.SeekOrigin]::Begin)
    $read = 0
    while ($read -lt $count) {
        $n = $Stream.Read($buffer, $read, $count - $read)
        if ($n -le 0) { break }
        $read += $n
    }
    if ($read -le 0) { return '' }

    return [Convert]::ToBase64String($buffer, 0, $read)
}

function Read-ActionLogLines {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][hashtable]$State
    )

    $result = @{ State = $State; Lines = @(); Status = 'Ok'; Message = '' }
    $path = $State.Path

    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $State.Offset = [long]0
        $State.LastLength = [long]0
        $State.HeadFingerprint = ''
        $result.Status = 'FileMissing'
        return $result
    }

    $stream = $null
    try {
        # 変化が無ければ開かない。1 秒ポーリングでの無駄な I/O を避ける
        $statLength = [long](Get-Item -LiteralPath $path -Force).Length
        if ($statLength -eq $State.LastLength -and $statLength -le $State.Offset) {
            return $result
        }

        $share = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
        $stream = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, $share)
        $length = [long]$stream.Length

        $status = 'Ok'
        if ($length -lt $State.Offset -or $length -lt $State.LastLength) {
            $State.Offset = [long]0
            $status = 'Truncated'
        }
        elseif ($State.Offset -gt 0) {
            $current = Get-HeadFingerprint -Stream $stream -Offset $State.Offset
            if ($current -ne $State.HeadFingerprint) {
                $State.Offset = [long]0
                $status = 'Recreated'
            }
        }

        $State.LastLength = $length

        if ($length -le $State.Offset) {
            $State.HeadFingerprint = Get-HeadFingerprint -Stream $stream -Offset $State.Offset
            $result.Status = $status
            return $result
        }

        $available = $length - $State.Offset
        $count = [int][Math]::Min([long]$script:MaxReadBytes, $available)
        $buffer = New-Object byte[] $count

        [void]$stream.Seek($State.Offset, [System.IO.SeekOrigin]::Begin)
        $read = 0
        while ($read -lt $count) {
            $n = $stream.Read($buffer, $read, $count - $read)
            if ($n -le 0) { break }
            $read += $n
        }

        $lastLf = -1
        for ($i = $read - 1; $i -ge 0; $i--) {
            if ($buffer[$i] -eq 10) { $lastLf = $i; break }
        }

        if ($lastLf -lt 0) {
            # 完結した行がまだ無い。オフセットは進めない
            $State.HeadFingerprint = Get-HeadFingerprint -Stream $stream -Offset $State.Offset
            $result.Status = $status
            return $result
        }

        $text = [System.Text.Encoding]::UTF8.GetString($buffer, 0, $lastLf + 1)
        $State.Offset = $State.Offset + $lastLf + 1
        $State.HeadFingerprint = Get-HeadFingerprint -Stream $stream -Offset $State.Offset

        $lines = New-Object System.Collections.Generic.List[string]
        foreach ($raw in ($text -split "`n")) {
            $line = $raw.TrimEnd([char]13).TrimStart([char]0xFEFF)
            if ($line.Length -gt 0) { $lines.Add($line) }
        }

        $result.Lines = @($lines.ToArray())
        $result.Status = $status
        return $result
    }
    catch {
        $result.Lines = @()
        $result.Status = 'Error'
        $result.Message = $_.Exception.Message
        return $result
    }
    finally {
        if ($null -ne $stream) { $stream.Dispose() }
    }
}

Export-ModuleMember -Function New-ActionLogReaderState, Read-ActionLogLines
