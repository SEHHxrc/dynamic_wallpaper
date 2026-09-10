[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateRange(1, 100)]
    [int]$FreshProcessCount = 10,

    [ValidateRange(1, 30)]
    [int]$VisibleSeconds = 1,

    [ValidateNotNullOrEmpty()]
    [string]$TargetDisplay = 'primary',

    [string]$OutputDirectory = 'artifacts/diagnostics/product-candidate-reliability'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$targetFramework = 'net10.0-windows10.0.18362.0'
$diagnosticExecutable = Join-Path $repositoryRoot "tools/DesktopHostDiagnostics/bin/$Configuration/$targetFramework/DesktopHostDiagnostics.exe"
$outputBase = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory
} else {
    Join-Path $repositoryRoot $OutputDirectory
}
$batchName = 'batch-{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$outputRoot = Join-Path $outputBase $batchName

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$startedAt = [DateTimeOffset]::UtcNow
$runs = [System.Collections.Generic.List[object]]::new()
$harnessFailure = $null
$baselineTopologyFingerprint = $null

if (-not (Test-Path -LiteralPath $diagnosticExecutable -PathType Leaf)) {
    $harnessFailure = "Release diagnostic executable was not found: $diagnosticExecutable. Build it before running this harness."
} else {
    for ($index = 1; $index -le $FreshProcessCount; $index++) {
        $runName = 'run-{0:D2}' -f $index
        $resultPath = Join-Path $outputRoot "$runName.json"
        $logPath = Join-Path $outputRoot "$runName.log"
        $arguments = @(
            '--raised-desktop-product-candidate'
            '--duration-seconds'
            $VisibleSeconds.ToString([System.Globalization.CultureInfo]::InvariantCulture)
            '--result-json'
            $resultPath
            '--target-display'
            $TargetDisplay
        )

        & $diagnosticExecutable @arguments *> $logPath
        $processExitCode = $LASTEXITCODE
        if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
            $runs.Add([pscustomobject]@{
                ordinal = $index
                exitCode = $processExitCode
                classification = 'HarnessFailure'
                resultPath = $resultPath
                logPath = $logPath
                error = 'The diagnostic process did not produce its structured result.'
            })
            $harnessFailure = "Run $index did not produce a structured result."
            break
        }

        $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        $classification = [string]$result.classification
        if ($null -eq $baselineTopologyFingerprint) {
            $baselineTopologyFingerprint = [string]$result.displayTopologyFingerprint
        } elseif ($baselineTopologyFingerprint -ne [string]$result.displayTopologyFingerprint) {
            $classification = 'EnvironmentChanged'
        }
        $runs.Add([pscustomobject]@{
            ordinal = $index
            exitCode = $processExitCode
            classification = $classification
            resultPath = $resultPath
            logPath = $logPath
            applyElapsedMilliseconds = $result.apply.elapsedMilliseconds
            firstFrameMilliseconds = @($result.apply.phaseTimings |
                Where-Object phase -eq 'FirstFramePresented' |
                Select-Object -ExpandProperty elapsedMilliseconds -Last 1)[0]
            retirementElapsedMilliseconds = $result.retirement.elapsedMilliseconds
            shellGenerationChanged = $result.shellGenerationChanged
            displayTopologyChanged = $result.displayTopologyChanged
            error = $result.error
        })

        if ($classification -in @('EnvironmentBlocked', 'EnvironmentChanged', 'HarnessFailure')) {
            break
        }
    }
}

function Get-Distribution([object[]]$Values) {
    $numbers = @($Values | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_ } | Sort-Object)
    if ($numbers.Count -eq 0) {
        return $null
    }
    $middle = [Math]::Floor($numbers.Count / 2)
    $median = if (($numbers.Count % 2) -eq 0) {
        ($numbers[$middle - 1] + $numbers[$middle]) / 2
    } else {
        $numbers[$middle]
    }
    return [ordered]@{
        minimum = $numbers[0]
        median = $median
        maximum = $numbers[-1]
    }
}

$validRuns = @($runs | Where-Object classification -in @('ValidSuccess', 'ProductFailure', 'Cancelled'))
$validSuccesses = @($validRuns | Where-Object classification -eq 'ValidSuccess')
$passed = $null -eq $harnessFailure -and
    $runs.Count -eq $FreshProcessCount -and
    $validRuns.Count -eq $FreshProcessCount -and
    $validSuccesses.Count -eq $FreshProcessCount
$summary = [ordered]@{
    schemaVersion = '1.0'
    startedAt = $startedAt
    completedAt = [DateTimeOffset]::UtcNow
    classification = if ($passed) { 'Passed' } else { 'Failed' }
    processCold = $true
    targetDisplay = $TargetDisplay
    requestedRuns = $FreshProcessCount
    executedRuns = $runs.Count
    validRuns = $validRuns.Count
    successfulRuns = $validSuccesses.Count
    harnessFailure = $harnessFailure
    statistics = [ordered]@{
        applyElapsedMilliseconds = Get-Distribution @($validSuccesses.applyElapsedMilliseconds)
        firstFrameMilliseconds = Get-Distribution @($validSuccesses.firstFrameMilliseconds)
        retirementElapsedMilliseconds = Get-Distribution @($validSuccesses.retirementElapsedMilliseconds)
    }
    runs = $runs
}

$summaryPath = Join-Path $outputRoot 'summary.json'
$summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $summaryPath -Encoding utf8
$summary | ConvertTo-Json -Depth 10
Write-Host "Reliability summary: $summaryPath"
if (-not $passed) {
    exit 1
}
