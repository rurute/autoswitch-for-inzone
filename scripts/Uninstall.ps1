<#
.SYNOPSIS
    INZONE Auto Switch の自動起動登録を解除する。
.DESCRIPTION
    既定ではファイルを残す。config.json とログを失わないため。
    -RemoveFiles を付けた場合だけ配置先ごと削除する。
#>
[CmdletBinding()]
param(
    [string]$InstallRoot,
    [switch]$RemoveFiles
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptsDir 'Common.ps1')

if ([string]::IsNullOrWhiteSpace($InstallRoot)) { $InstallRoot = Get-AutoSwitchInstallRoot }
$taskName = Get-AutoSwitchTaskName

if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    Write-Host "タスク '$taskName' を解除しました"
}
else {
    Write-Host "タスク '$taskName' は登録されていません"
}

Stop-AutoSwitchProcess

if ($RemoveFiles) {
    if (Test-Path -LiteralPath $InstallRoot) {
        Remove-Item -LiteralPath $InstallRoot -Recurse -Force
        Write-Host "$InstallRoot を削除しました"
    }
}
else {
    Write-Host "ファイルは $InstallRoot に残しています（削除するには -RemoveFiles を指定してください）"
}
