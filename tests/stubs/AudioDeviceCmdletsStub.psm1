Set-StrictMode -Version Latest

# 実機の AudioDeviceCmdlets を置き換えるスタブ。
# 本体は何もしない。Pester の Mock を掛けるための「存在するコマンド」を用意するのが目的。

function Get-AudioDevice {
    [CmdletBinding()]
    param(
        [switch]$List,
        [switch]$Playback,
        [switch]$Recording,
        [string]$ID,
        [int]$Index
    )
    throw 'AudioDeviceCmdletsStub: Get-AudioDevice は Mock されていません。'
}

function Set-AudioDevice {
    [CmdletBinding()]
    param(
        [Parameter(ValueFromPipeline = $true)]$InputObject,
        [string]$ID,
        [int]$Index
    )
    throw 'AudioDeviceCmdletsStub: Set-AudioDevice は Mock されていません。'
}

Export-ModuleMember -Function Get-AudioDevice, Set-AudioDevice
