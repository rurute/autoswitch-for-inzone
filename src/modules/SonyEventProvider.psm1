Set-StrictMode -Version Latest

# 1 行 1KB の JSON をすべてパースするのは無駄なので、対象行だけを先に絞り込む
$script:EventLinePattern = '"actionTypeId"\s*:\s*2301[12]'

$script:StateByActionTypeId = @{
    '23011' = 'Connected'
    '23012' = 'Disconnected'
}

function New-SeenLogIdSet {
    [CmdletBinding()]
    param(
        [int]$Capacity = 200
    )

    return @{
        Capacity = $Capacity
        Lookup   = @{}
        Order    = New-Object 'System.Collections.Generic.Queue[string]'
    }
}

function Add-SeenLogId {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][hashtable]$Set,
        [Parameter(Mandatory = $true)][string]$LogId
    )

    if ($Set.Lookup.ContainsKey($LogId)) { return $false }

    $Set.Lookup[$LogId] = $true
    $Set.Order.Enqueue($LogId)

    while ($Set.Order.Count -gt $Set.Capacity) {
        $evicted = $Set.Order.Dequeue()
        [void]$Set.Lookup.Remove($evicted)
    }

    return $true
}

function Get-BudsEventsFromLines {
    [CmdletBinding()]
    param(
        [AllowNull()][AllowEmptyCollection()][string[]]$Lines,
        [Parameter(Mandatory = $true)][hashtable]$SeenLogIds
    )

    $events = New-Object 'System.Collections.Generic.List[hashtable]'
    if ($null -eq $Lines) { return @() }

    foreach ($line in $Lines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -notmatch $script:EventLinePattern) { continue }

        $obj = $null
        try { $obj = $line | ConvertFrom-Json }
        catch { continue }
        if ($null -eq $obj) { continue }

        if (-not $obj.PSObject.Properties['actionTypeId']) { continue }
        $key = [string]$obj.actionTypeId
        if (-not $script:StateByActionTypeId.ContainsKey($key)) { continue }
        $state = $script:StateByActionTypeId[$key]

        if (-not $obj.PSObject.Properties['logId']) { continue }
        $logId = [string]$obj.logId
        if ([string]::IsNullOrWhiteSpace($logId)) { continue }

        if (-not (Add-SeenLogId -Set $SeenLogIds -LogId $logId)) { continue }

        $timeStampMs = [long]0
        if ($obj.PSObject.Properties['timeStamp']) {
            $parsed = [long]0
            if ([long]::TryParse([string]$obj.timeStamp, [ref]$parsed)) { $timeStampMs = $parsed }
        }

        $events.Add(@{ LogId = $logId; TimeStampMs = $timeStampMs; State = $state })
    }

    return @($events.ToArray())
}

Export-ModuleMember -Function New-SeenLogIdSet, Add-SeenLogId, Get-BudsEventsFromLines
