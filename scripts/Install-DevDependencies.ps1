[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$installed = @(Get-Module -ListAvailable -Name Pester |
    Where-Object { $_.Version -ge [version]'5.5.0' -and $_.Version -lt [version]'6.0.0' })

if ($installed.Count -gt 0) {
    Write-Host "Pester $($installed[0].Version) は導入済みです。"
    return
}

if (-not (Get-PackageProvider -Name NuGet -ErrorAction SilentlyContinue)) {
    Write-Host 'NuGet プロバイダを導入します...'
    Install-PackageProvider -Name NuGet -Scope CurrentUser -Force | Out-Null
}

if ((Get-PSRepository -Name PSGallery).InstallationPolicy -ne 'Trusted') {
    Set-PSRepository -Name PSGallery -InstallationPolicy Trusted
}

Write-Host 'Pester 5 を CurrentUser スコープへ導入します...'
Install-Module -Name Pester `
    -MinimumVersion 5.5.0 -MaximumVersion 5.99.99 `
    -Scope CurrentUser -Force -SkipPublisherCheck -AllowClobber

$now = @(Get-Module -ListAvailable -Name Pester |
    Where-Object { $_.Version -ge [version]'5.5.0' -and $_.Version -lt [version]'6.0.0' })
if ($now.Count -eq 0) { throw 'Pester 5 の導入に失敗しました。' }
Write-Host "Pester $($now[0].Version) を導入しました。"
