Set-StrictMode -Version Latest

$script:ValidCurrentStates = @('Unknown', 'Connected', 'Disconnected')
$script:ValidEventStates   = @('Connected', 'Disconnected')

function Resolve-StateTransition {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$CurrentState,
        [Parameter(Mandatory = $true)][string]$EventState
    )

    if ($script:ValidCurrentStates -notcontains $CurrentState) {
        throw "CurrentState が不正です: '$CurrentState'"
    }
    if ($script:ValidEventStates -notcontains $EventState) {
        throw "EventState が不正です: '$EventState'"
    }

    if ($CurrentState -eq $EventState) {
        return @{ Changed = $false; NewState = $CurrentState }
    }

    return @{ Changed = $true; NewState = $EventState }
}

Export-ModuleMember -Function Resolve-StateTransition
