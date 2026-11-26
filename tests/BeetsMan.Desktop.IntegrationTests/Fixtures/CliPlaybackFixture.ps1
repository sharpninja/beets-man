[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('oneShot', 'session')]
    [string] $FixtureMode,

    [Parameter(Mandatory, Position = 1)]
    [string] $ExpectedMediaPath,

    [Parameter(Position = 2)]
    [string] $Action,

    [Parameter(Position = 3)]
    [string] $MediaPath,

    [Parameter(Position = 4)]
    [long] $PositionMs,

    [string] $StartupMarker,

    [string] $StopMarker
)

$ErrorActionPreference = 'Stop'
if ($StartupMarker -and $PSCmdlet.ShouldProcess($StartupMarker, 'Record the fixture worker process ID')) {
    Set-Content -LiteralPath $StartupMarker -Value $PID -NoNewline
}

function Write-TestReply {
    param([System.Collections.IDictionary] $Reply)
    Write-Output (ConvertTo-Json -InputObject $Reply -Compress)
}

function Invoke-TestFailure {
    param([string] $Kind)
    switch ($Kind) {
        'exit' { exit 17 }
        'timeout' { Start-Sleep -Seconds 30 }
        'malformed' { Write-Output '{broken' }
        'oversize' { Write-Output ('x' * 70000) }
    }
}

if ($FixtureMode -eq 'oneShot') {
    if ($Action -in 'exit', 'timeout', 'malformed', 'oversize') {
        Invoke-TestFailure -Kind $Action
        exit
    }
    if ($MediaPath -cne $ExpectedMediaPath) { exit 18 }
    $workerState = switch ($Action) {
        'play' { 'running' }
        'pause' { 'waiting' }
        'stop' { 'idle' }
        'seek' { 'running' }
        default { exit 19 }
    }
    if ($Action -eq 'stop' -and $StopMarker -and $PSCmdlet.ShouldProcess($StopMarker, 'Record the fixture Stop action')) {
        Set-Content -LiteralPath $StopMarker -Value $Action -NoNewline
    }
    Write-TestReply -Reply @{ playerState = $workerState; where = $PositionMs; length = 30000 }
    exit
}

$workerState = 'idle'
$workerPosition = 0L
:worker while ($null -ne ($line = [Console]::ReadLine())) {
    $request = ConvertFrom-Json -InputObject $line -AsHashtable
    switch ($request.kind) {
        'play' {
            if ($request.path -cne $ExpectedMediaPath) { exit 18 }
            $workerState = 'running'
            $workerPosition = [long] $request.positionMs
        }
        'pause' {
            if ($workerState -ne 'running') { exit 20 }
            $workerState = 'waiting'
        }
        'stop' {
            $workerState = 'idle'
            $workerPosition = 0L
        }
        'seek' { $workerPosition = [long] $request.positionMs }
        'status' { }
        'wrongId' {
            Write-TestReply -Reply @{ id = [guid]::NewGuid(); playerState = $workerState }
            continue worker
        }
        { $_ -in 'exit', 'timeout', 'malformed', 'oversize' } {
            Invoke-TestFailure -Kind $request.kind
            continue worker
        }
        default { exit 19 }
    }
    Write-TestReply -Reply @{ id = $request.id; playerState = $workerState; where = $workerPosition; length = 30000 }
}
