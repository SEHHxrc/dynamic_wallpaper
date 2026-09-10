[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$TargetDisplay = 'all',

    [ValidateRange(1, 32)]
    [int]$TargetSurfaceOrdinal = 2,

    [string]$OutputDirectory = 'artifacts/diagnostics/product-candidate-faults'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$framework = 'net10.0-windows10.0.18362.0'
$executable = Join-Path $repositoryRoot "tools/DesktopHostDiagnostics/bin/$Configuration/$framework/DesktopHostDiagnostics.exe"
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Build the $Configuration DesktopHostDiagnostics executable before running the fault matrix: $executable"
}

$batchId = 'batch-{0}-{1}' -f [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ'), [Guid]::NewGuid().ToString('N').Substring(0, 8)
$batchDirectory = Join-Path (Join-Path $repositoryRoot $OutputDirectory) $batchId
[void](New-Item -ItemType Directory -Path $batchDirectory -Force)

$phases = @('surface-attached', 'content-loaded', 'first-frame-presented')
$actions = @('fatal', 'exit', 'suppress')
$runs = [System.Collections.Generic.List[object]]::new()
$ordinal = 0
$startedAt = [DateTimeOffset]::UtcNow
$failed = $false

foreach ($phase in $phases) {
    foreach ($action in $actions) {
        $ordinal++
        $name = '{0:D2}-{1}-{2}' -f $ordinal, $phase, $action
        $resultPath = Join-Path $batchDirectory "$name.json"
        $stdoutPath = Join-Path $batchDirectory "$name.stdout.log"
        $stderrPath = Join-Path $batchDirectory "$name.stderr.log"
        $arguments = @(
            '--raised-desktop-product-candidate-fault',
            '--target-display', $TargetDisplay,
            '--target-surface-ordinal', $TargetSurfaceOrdinal.ToString([Globalization.CultureInfo]::InvariantCulture),
            '--fault-phase', $phase,
            '--fault-action', $action,
            '--result-json', $resultPath
        )

        $process = Start-Process `
            -FilePath $executable `
            -ArgumentList $arguments `
            -WorkingDirectory $repositoryRoot `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath `
            -WindowStyle Hidden `
            -Wait `
            -PassThru

        $classification = $null
        $errorMessage = $null
        if (Test-Path -LiteralPath $resultPath -PathType Leaf) {
            $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
            $classification = $result.classification
            $errorMessage = $result.error
        }
        $valid = $process.ExitCode -eq 0 -and $classification -eq 'ExpectedFailureValidated'
        $runs.Add([pscustomobject]@{
            ordinal = $ordinal
            phase = $phase
            action = $action
            exitCode = $process.ExitCode
            classification = $classification
            valid = $valid
            resultPath = $resultPath
            stdoutPath = $stdoutPath
            stderrPath = $stderrPath
            error = $errorMessage
        })
        if (-not $valid) {
            $failed = $true
            break
        }
    }
    if ($failed) {
        break
    }
}

$summary = [pscustomobject]@{
    schemaVersion = '1.0'
    startedAt = $startedAt
    completedAt = [DateTimeOffset]::UtcNow
    classification = if ($failed) { 'Failed' } else { 'Passed' }
    targetDisplay = $TargetDisplay
    targetSurfaceOrdinal = $TargetSurfaceOrdinal
    requestedRuns = $phases.Count * $actions.Count
    executedRuns = $runs.Count
    validatedRuns = @($runs | Where-Object valid).Count
    runs = @($runs)
}
$summaryPath = Join-Path $batchDirectory 'summary.json'
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding utf8NoBOM
$summary | ConvertTo-Json -Depth 8
Write-Output "Fault matrix summary: $summaryPath"

if ($failed) {
    exit 1
}
