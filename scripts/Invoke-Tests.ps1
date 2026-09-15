[CmdletBinding()]
param(
    [string]$Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptsDir

Import-Module Pester -MinimumVersion 5.5.0 -MaximumVersion 5.99.99 -Force

$config = New-PesterConfiguration
$config.Run.Path = if ([string]::IsNullOrWhiteSpace($Path)) { Join-Path $repoRoot 'tests' } else { $Path }
$config.Run.Exit = $true
$config.Output.Verbosity = 'Detailed'
$config.Should.ErrorAction = 'Stop'

Invoke-Pester -Configuration $config
