Set-StrictMode -Version Latest

$script:LevelRank = @{ Debug = 0; Info = 1; Warn = 2; Error = 3 }

$script:LogPath         = $null
$script:MinRank         = 1
$script:MaxSizeBytes    = 1048576L
$script:LastMessage     = $null
$script:SuppressedCount = 0

function Reset-AutoSwitchLogState {
    [CmdletBinding()]
    param()

    $script:LogPath         = $null
    $script:MinRank         = 1
    $script:MaxSizeBytes    = 1048576L
    $script:LastMessage     = $null
    $script:SuppressedCount = 0
}

function Initialize-AutoSwitchLog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$Level = 'Info',
        [int]$MaxSizeKB = 1024
    )

    $script:LogPath = $Path
    $script:MinRank = if ($script:LevelRank.ContainsKey($Level)) { $script:LevelRank[$Level] } else { 1 }
    $script:MaxSizeBytes = [long]$MaxSizeKB * 1024L
    $script:LastMessage = $null
    $script:SuppressedCount = 0

    try {
        $dir = Split-Path -Parent $Path
        if ($dir -and -not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force -ErrorAction Stop | Out-Null
        }
    }
    catch { }
}

function Format-AutoSwitchLogLine {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Level,
        [Parameter(Mandatory = $true)][string]$Message
    )

    return ('{0} [{1}] {2}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff'), $Level.ToUpperInvariant(), $Message)
}

function Invoke-AutoSwitchLogRotation {
    [CmdletBinding()]
    param()

    try {
        if (-not (Test-Path -LiteralPath $script:LogPath -PathType Leaf)) { return }
        if ((Get-Item -LiteralPath $script:LogPath -Force).Length -lt $script:MaxSizeBytes) { return }

        $backup = "$($script:LogPath).1"
        if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force -ErrorAction Stop }
        Move-Item -LiteralPath $script:LogPath -Destination $backup -Force -ErrorAction Stop
    }
    catch { }
}

function Add-AutoSwitchLogLines {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string[]]$Lines
    )

    # FileShare.ReadWrite で開く。AppendAllLines は FileShare.Read 相当で開くため、
    # 別プロセスが書き込み用に掴んでいると共有違反になり、行が無言で失われる。
    $encoding = New-Object System.Text.UTF8Encoding($false)
    $bytes = $encoding.GetBytes((($Lines -join "`r`n") + "`r`n"))

    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            $stream = [System.IO.File]::Open(
                $script:LogPath,
                [System.IO.FileMode]::Append,
                [System.IO.FileAccess]::Write,
                [System.IO.FileShare]::ReadWrite)
            try {
                $stream.Write($bytes, 0, $bytes.Length)
                $stream.Flush()
            }
            finally {
                $stream.Dispose()
            }
            return
        }
        catch {
            if ($attempt -ge 3) { return }
            [System.Threading.Thread]::Sleep(20 * $attempt)
        }
    }
}

function Write-AutoSwitchLog {
    [CmdletBinding()]
    param(
        [ValidateSet('Debug', 'Info', 'Warn', 'Error')][string]$Level = 'Info',
        [Parameter(Mandatory = $true)][string]$Message
    )

    if ([string]::IsNullOrEmpty($script:LogPath)) { return }
    if ($script:LevelRank[$Level] -lt $script:MinRank) { return }

    if ($Message -eq $script:LastMessage) {
        $script:SuppressedCount++
        return
    }

    $lines = New-Object System.Collections.Generic.List[string]
    if ($script:SuppressedCount -gt 0) {
        $lines.Add((Format-AutoSwitchLogLine -Level 'Info' -Message "(同一メッセージを $($script:SuppressedCount) 回抑制)"))
        $script:SuppressedCount = 0
    }
    $lines.Add((Format-AutoSwitchLogLine -Level $Level -Message $Message))
    $script:LastMessage = $Message

    Invoke-AutoSwitchLogRotation

    try {
        Add-AutoSwitchLogLines -Lines $lines.ToArray()
    }
    catch { }
}

Export-ModuleMember -Function Initialize-AutoSwitchLog, Write-AutoSwitchLog, Reset-AutoSwitchLogState
