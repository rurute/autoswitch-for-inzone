Set-StrictMode -Version Latest

# 実機（AudioDeviceCmdlets）に触れるのはこのモジュールだけ。
# 将来 HID 監視へ移行しても、このファイルは変更しない。

$script:AudioModuleName = 'AudioDeviceCmdlets'

function Test-AudioSwitcherAvailable {
    [CmdletBinding()]
    param()

    try {
        if (Get-Command -Name 'Get-AudioDevice' -ErrorAction SilentlyContinue) { return $true }
        Import-Module $script:AudioModuleName -ErrorAction Stop
        return $true
    }
    catch {
        return $false
    }
}

function Get-DefaultPlaybackDeviceName {
    [CmdletBinding()]
    param()

    if (-not (Test-AudioSwitcherAvailable)) { return $null }

    try {
        $device = Get-AudioDevice -Playback -ErrorAction Stop
        if ($null -eq $device) { return $null }
        return [string]$device.Name
    }
    catch {
        return $null
    }
}

function Set-DefaultPlaybackDeviceByName {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Name
    )

    if (-not (Test-AudioSwitcherAvailable)) {
        return @{
            Result  = 'ModuleUnavailable'
            Message = "$($script:AudioModuleName) を読み込めません。切り替えを見送ります。"
        }
    }

    # 既に目的のデバイスなら何もしない。取得に失敗した場合は判定せずに先へ進む
    try {
        $current = Get-AudioDevice -Playback -ErrorAction Stop
        if ($null -ne $current -and ([string]$current.Name) -eq $Name) {
            return @{ Result = 'AlreadyDefault'; Message = "既に既定の再生デバイスです: $Name" }
        }
    }
    catch { }

    try {
        $candidates = @(Get-AudioDevice -List -ErrorAction Stop |
            Where-Object { $_.Type -eq 'Playback' -and $_.Name -eq $Name })

        if ($candidates.Count -eq 0) {
            return @{ Result = 'DeviceNotFound'; Message = "再生デバイスが見つかりません: $Name" }
        }

        $candidates[0] | Set-AudioDevice -ErrorAction Stop | Out-Null
        return @{ Result = 'Switched'; Message = "既定の再生デバイスを切り替えました: $Name" }
    }
    catch {
        return @{ Result = 'Error'; Message = "切り替えに失敗しました ($Name): $($_.Exception.Message)" }
    }
}

Export-ModuleMember -Function Test-AudioSwitcherAvailable, Get-DefaultPlaybackDeviceName, Set-DefaultPlaybackDeviceByName
