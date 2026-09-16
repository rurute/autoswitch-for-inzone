<#
.SYNOPSIS
    C# トレイ版を %LOCALAPPDATA%\AutoSwitchForInzone\bin へ配置する。
.DESCRIPTION
    管理者権限は不要。何度実行しても同じ結果になる。
    config.json と logs\ には触らない。自動起動の登録も行わない
    （トレイメニューの「Windows ログオン時の自動起動」から行う）。
    実行中の AutoSwitch.App があるときは、強制終了せずエラーで終わる。
#>
[CmdletBinding()]
param(
    [string]$InstallRoot,
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptsDir
if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = Join-Path $env:LOCALAPPDATA 'AutoSwitchForInzone'
}
$binDir = Join-Path $InstallRoot 'bin'

Write-Host "配置先: $binDir"

# --- 1) 実行中なら中断する ------------------------------------------------
# 強制終了はトレイ版の終了経路（監視の停止、アイコンと Mutex の解放）を飛ばすため行わない
if (Get-Process -Name 'AutoSwitch.App' -ErrorAction SilentlyContinue) {
    throw 'AutoSwitch.App が実行中です。トレイメニューの「終了」で終わらせてから実行してください。'
}

# --- 2) publish する ------------------------------------------------------
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ('AutoSwitchForInzone-publish-' + [Guid]::NewGuid().ToString('N'))
try {
    $project = Join-Path $repoRoot 'src\AutoSwitch.App\AutoSwitch.App.csproj'
    & dotnet publish $project -c $Configuration --output $staging
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish が失敗しました (終了コード $LASTEXITCODE)"
    }

    # --- 3) 配置する ------------------------------------------------------
    # bin\ は毎回作り直す。前の版の dll を残さないため
    New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
    if (Test-Path -LiteralPath $binDir) {
        Remove-Item -LiteralPath $binDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $binDir -Force | Out-Null
    Copy-Item -Path (Join-Path $staging '*') -Destination $binDir -Recurse -Force
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}

$exePath = Join-Path $binDir 'AutoSwitch.App.exe'
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "配置に失敗しました: $exePath がありません"
}

Write-Host "配置しました: $exePath"
Write-Host '自動起動はトレイメニューの「Windows ログオン時の自動起動」で ON/OFF します。'
Write-Host '設定ファイルとログには触れていません。'
