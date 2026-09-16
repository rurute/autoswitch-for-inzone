<#
.SYNOPSIS
    INZONE Auto Switch を %LOCALAPPDATA% へ配置し、ログオン時に自動起動するよう登録する。
.DESCRIPTION
    管理者権限は不要。何度実行しても同じ結果になる。
    config.json は既に存在する場合は上書きしない。
#>
[CmdletBinding()]
param(
    [string]$InstallRoot,
    [switch]$NoStart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptsDir 'Common.ps1')

$repoRoot = Split-Path -Parent $scriptsDir
if ([string]::IsNullOrWhiteSpace($InstallRoot)) { $InstallRoot = Get-AutoSwitchInstallRoot }
$taskName = Get-AutoSwitchTaskName

Write-Host "配置先: $InstallRoot"

# --- 1) 既存のタスクとプロセスを止める ------------------------------------
if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Write-Host "既存のタスク '$taskName' を解除します"
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}
Stop-AutoSwitchProcess

# --- 2) ファイルを配置する ------------------------------------------------
New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null

# 古いモジュールが残らないよう modules\ は作り直す
$modulesDst = Join-Path $InstallRoot 'modules'
if (Test-Path -LiteralPath $modulesDst) { Remove-Item -LiteralPath $modulesDst -Recurse -Force }

# src\ の「中身」を配置する。src というディレクトリ階層は作らない
# C# 版（新版）のプロジェクトは旧版の配置先へ入れない。旧版が使うのは modules\ と INZONE-AutoSwitch.ps1 だけ
$csharpProjects = @('AutoSwitch.Core', 'AutoSwitch.App', 'AutoSwitch.Audio')
# -Force は隠しファイル・隠しフォルダも列挙するため。元の src\* のワイルドカードは隠し項目も拾うので、
# それを付けないと除外3つ以外でも取りこぼしが出る（段階4 plan レビュー F4）
Get-ChildItem -Path (Join-Path $repoRoot 'src') -Force |
    Where-Object { $csharpProjects -notcontains $_.Name } |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $InstallRoot -Recurse -Force }
Copy-Item -Path (Join-Path $repoRoot 'scripts\launch-hidden.vbs') -Destination $InstallRoot -Force

New-Item -ItemType Directory -Path (Join-Path $InstallRoot 'logs') -Force | Out-Null

$configDst = Join-Path $InstallRoot 'config.json'
if (Test-Path -LiteralPath $configDst) {
    Write-Host 'config.json は既に存在するため、そのまま残します'
}
else {
    Copy-Item -Path (Join-Path $repoRoot 'config.json') -Destination $configDst
    Write-Host 'config.json を配置しました'
}

# --- 3) ログオン時のタスクを登録する --------------------------------------
$vbsPath = Join-Path $InstallRoot 'launch-hidden.vbs'

$action = New-ScheduledTaskAction -Execute 'wscript.exe' -Argument ('//B //Nologo "{0}"' -f $vbsPath)

$trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
$trigger.Delay = 'PT15S'

$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -Hidden
$settings.RestartInterval = 'PT1M'
$settings.RestartCount = 3

Register-ScheduledTask `
    -TaskName $taskName `
    -Action $action `
    -Trigger $trigger `
    -Principal $principal `
    -Settings $settings `
    -Description 'INZONE Buds の接続／切断に追従して既定の再生デバイスを切り替えます。' `
    -Force | Out-Null

Write-Host "タスク '$taskName' を登録しました（ログオン時、15 秒遅延、非表示）"

# --- 4) すぐに起動する ----------------------------------------------------
if ($NoStart) {
    Write-Host '-NoStart が指定されたため起動しません。次回ログオン時から動作します。'
}
else {
    Start-ScheduledTask -TaskName $taskName
    Write-Host '起動しました。ログ: ' -NoNewline
    Write-Host (Join-Path $InstallRoot 'logs\INZONE-AutoSwitch.log')
}
