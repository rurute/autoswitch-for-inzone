Set-StrictMode -Version Latest

$script:Defaults = [ordered]@{
    budsDevice               = 'スピーカー (INZONE Buds - Game)'
    speakerDevice            = 'スピーカー (Realtek(R) Audio)'
    switchOnConnect          = $true
    switchOnDisconnect       = $true
    actionLogPath            = '%APPDATA%\Sony\INZONE Hub\ActionLog.log'
    pollIntervalSeconds      = 1
    idlePollIntervalSeconds  = 5
    startupSyncMaxAgeMinutes = 10
    logLevel                 = 'Info'
    maxLogSizeKB             = 1024
}

$script:IntRanges = @{
    pollIntervalSeconds      = @(1, 60)
    idlePollIntervalSeconds  = @(1, 300)
    startupSyncMaxAgeMinutes = @(0, 1440)
    maxLogSizeKB             = @(64, 102400)
}

$script:ValidLogLevels = @('Debug', 'Info', 'Warn', 'Error')

function Get-AutoSwitchConfigDefault {
    [CmdletBinding()]
    param()

    $copy = @{}
    foreach ($key in $script:Defaults.Keys) { $copy[$key] = $script:Defaults[$key] }
    return $copy
}

function Read-AutoSwitchConfig {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    $warnings = New-Object System.Collections.Generic.List[string]
    $config = Get-AutoSwitchConfigDefault
    $raw = $null

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $warnings.Add("config.json が見つかりません: $Path。既定値で動作します。")
    }
    else {
        try {
            $text = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
            $raw = $text | ConvertFrom-Json
        }
        catch {
            $warnings.Add("config.json を解析できません: $($_.Exception.Message)。既定値で動作します。")
            $raw = $null
        }
    }

    if ($null -ne $raw) {
        foreach ($prop in $raw.PSObject.Properties) {
            $key = $prop.Name
            if (-not $config.ContainsKey($key)) {
                $warnings.Add("未知の設定キーを無視します: $key")
                continue
            }

            $value = $prop.Value
            $default = $script:Defaults[$key]

            if ($default -is [bool]) {
                if ($value -is [bool]) { $config[$key] = $value }
                else { $warnings.Add("$key は true / false で指定してください。既定値 $default を使います。") }
                continue
            }

            if ($default -is [int]) {
                $parsed = 0
                $isInt = ($null -ne $value) -and (-not ($value -is [bool])) -and
                         [int]::TryParse([string]$value, [ref]$parsed)
                if (-not $isInt) {
                    $warnings.Add("$key は整数で指定してください。既定値 $default を使います。")
                    continue
                }
                $range = $script:IntRanges[$key]
                if ($parsed -lt $range[0] -or $parsed -gt $range[1]) {
                    $warnings.Add("$key は $($range[0]) 〜 $($range[1]) の範囲で指定してください。既定値 $default を使います。")
                    continue
                }
                $config[$key] = $parsed
                continue
            }

            if (($value -is [string]) -and ($value.Trim().Length -gt 0)) {
                $config[$key] = $value
            }
            else {
                $warnings.Add("$key は空でない文字列で指定してください。既定値を使います。")
            }
        }
    }

    if ($script:ValidLogLevels -notcontains $config['logLevel']) {
        $warnings.Add("logLevel が不正です: $($config['logLevel'])。既定値 $($script:Defaults['logLevel']) を使います。")
        $config['logLevel'] = $script:Defaults['logLevel']
    }

    $config['actionLogPath'] = [System.Environment]::ExpandEnvironmentVariables($config['actionLogPath'])

    return @{ Config = $config; Warnings = @($warnings.ToArray()) }
}

Export-ModuleMember -Function Get-AutoSwitchConfigDefault, Read-AutoSwitchConfig
