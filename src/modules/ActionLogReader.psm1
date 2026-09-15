Set-StrictMode -Version Latest

$script:MaxReadBytes = 4194304   # 1 tick で読む上限 4MB。超過分は次の tick へ持ち越す

function New-ActionLogReaderState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    return @{
        Path                = $Path
        Offset              = [long]0
        LastLength          = [long]0
        LastCreationTimeUtc = $null
    }
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
        $State.LastCreationTimeUtc = $null
        $result.Status = 'FileMissing'
        return $result
    }

    try {
        $item = Get-Item -LiteralPath $path -Force
        $length = [long]$item.Length
        $creation = $item.CreationTimeUtc

        $status = 'Ok'
        if ($null -ne $State.LastCreationTimeUtc -and $creation -ne $State.LastCreationTimeUtc) {
            $State.Offset = [long]0
            $status = 'Recreated'
        }
        elseif ($length -lt $State.Offset -or $length -lt $State.LastLength) {
            $State.Offset = [long]0
            $status = 'Truncated'
        }

        $State.LastCreationTimeUtc = $creation
        $State.LastLength = $length

        if ($length -le $State.Offset) {
            $result.Status = $status
            return $result
        }

        $available = $length - $State.Offset
        $count = [int][Math]::Min([long]$script:MaxReadBytes, $available)
        $buffer = New-Object byte[] $count

        $share = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
        $stream = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, $share)
        $read = 0
        try {
            [void]$stream.Seek($State.Offset, [System.IO.SeekOrigin]::Begin)
            while ($read -lt $count) {
                $n = $stream.Read($buffer, $read, $count - $read)
                if ($n -le 0) { break }
                $read += $n
            }
        }
        finally {
            $stream.Dispose()
        }

        $lastLf = -1
        for ($i = $read - 1; $i -ge 0; $i--) {
            if ($buffer[$i] -eq 10) { $lastLf = $i; break }
        }

        if ($lastLf -lt 0) {
            # 完結した行がまだ無い。オフセットは進めない
            $result.Status = $status
            return $result
        }

        $text = [System.Text.Encoding]::UTF8.GetString($buffer, 0, $lastLf + 1)
        $State.Offset = $State.Offset + $lastLf + 1

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
}

Export-ModuleMember -Function New-ActionLogReaderState, Read-ActionLogLines
