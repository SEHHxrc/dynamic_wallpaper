[CmdletBinding()]
param()

# Synthetic evidence only: no Host launch, native messages, logoff, or desktop access.
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$batchRoot = Join-Path $repositoryRoot ('artifacts/diagnostics/host-session-end/fixtures-' + [Guid]::NewGuid().ToString('N'))
$results = [Collections.Generic.List[object]]::new()
foreach ($scenario in @('ValidNativeNoStdoutTail', 'LegacyMissingTail', 'MissingNative', 'MismatchedRun',
    'CleanupTimeout', 'CleanupFailed', 'RuntimeNotStopped', 'CleanupNotSucceeded', 'WrongReason',
    'WrongPhase', 'ExceededBudget', 'ClaimsProcessExit', 'UnsupportedVersion',
    'ActiveValid', 'ActiveMissingEvidence', 'ActiveMissingRetirement', 'ActiveWrongRun',
    'ActiveWrongGeneration', 'ActiveMissingShutdownAck', 'ActiveSurfaceRemains', 'ActiveProcessAlive',
    'ActiveHandleOpen', 'ActiveWrongSession', 'ActiveDuplicateRetirement', 'ActiveRequiresNative')) {
    $runRoot = Join-Path $batchRoot $scenario
    $configRoot = Join-Path $runRoot 'configuration'
    New-Item -ItemType Directory -Path $configRoot | Out-Null
    $assignment = @{ displayId = 'diagnostic.offline.display'; wallpaperId = 'diagnostic.offline.wallpaper';
        layoutMode = 'perDisplay'; fitMode = 'cover'; presetId = $null }
    $armed = @{ schemaVersion = 1; runId = 'fixture'; armedAt = [DateTimeOffset]::UtcNow.AddMinutes(-1).ToString('O');
        expectedAssignment = $assignment; nativeExitEvidenceVersion = 1 }
    if ($scenario -eq 'LegacyMissingTail') { $armed.Remove('nativeExitEvidenceVersion') }
    if ($scenario -eq 'UnsupportedVersion') { $armed.nativeExitEvidenceVersion = 99 }
    if ($scenario.StartsWith('Active', [StringComparison]::Ordinal)) {
        $armed.activeSessionEvidenceVersion = 1
        $armed.initialGeneration = 7
        $armed.expectedSessionIds = @('active-session')
        if ($scenario -eq 'ActiveRequiresNative') { $armed.Remove('nativeExitEvidenceVersion') }
        $activeEvidence = @{ schemaVersion = 1; runId = 'runtime-fixture'; generation = 7;
            expectedSessionIds = @('active-session'); initialCommitSucceeded = $true; ownedSurfaceCount = 0; valid = $true;
            retirement = @(@{ sessionId = @{ value = 'active-session' }; generation = 7; surfaceOrdinal = 1;
                shutdownSent = $true; shutdownCompleted = $true; rendererDisposed = $true; surfacesCleaned = $true; issues = @() });
            rendererCleanup = @(@{ sessionId = 'active-session'; exitObserved = $true; handleDisposed = $true; issue = $null }) }
        switch ($scenario) {
            'ActiveMissingRetirement' { $activeEvidence.retirement = @() }
            'ActiveWrongRun' { $activeEvidence.runId = 'other-run' }
            'ActiveWrongGeneration' { $activeEvidence.generation = 6 }
            'ActiveMissingShutdownAck' { $activeEvidence.retirement[0].shutdownCompleted = $false }
            'ActiveSurfaceRemains' { $activeEvidence.ownedSurfaceCount = 1 }
            'ActiveProcessAlive' { $activeEvidence.rendererCleanup[0].exitObserved = $false }
            'ActiveHandleOpen' { $activeEvidence.rendererCleanup[0].handleDisposed = $false }
            'ActiveWrongSession' { $activeEvidence.retirement[0].sessionId.value = 'other-session' }
            'ActiveDuplicateRetirement' { $activeEvidence.retirement += $activeEvidence.retirement[0] }
        }
        if ($scenario -ne 'ActiveMissingEvidence') {
            [IO.File]::WriteAllText((Join-Path $runRoot 'active-exit-evidence.json'), ($activeEvidence | ConvertTo-Json -Depth 10))
        }
    }
    $receipt = @{ schemaVersion = 1; runId = 'runtime-fixture'; recordedAt = [DateTimeOffset]::UtcNow.AddSeconds(-1).ToString('O');
        reason = 'SessionLogoff'; runtimeStopped = $true; cleanupSucceeded = $true; failureType = $null }
    $native = @{ schemaVersion = 1; runId = 'runtime-fixture'; recordedAt = [DateTimeOffset]::UtcNow.ToString('O');
        phase = 'NativeCallbackCleanupObserved'; observedReason = 'SessionLogoff'; stopReason = 'SessionLogoff';
        cleanupOutcome = 'Completed'; runtimeStopped = $true; cleanupSucceeded = $true; failureType = $null;
        totalBudgetMilliseconds = 4000; elapsedMilliseconds = 20; callbackReturnObserved = $false; processExitObserved = $false }
    switch ($scenario) {
        'MismatchedRun' { $native.runId = 'another-run' }
        'CleanupTimeout' { $native.cleanupOutcome = 'TimedOut' }
        'CleanupFailed' { $native.cleanupOutcome = 'Failed' }
        'RuntimeNotStopped' { $native.runtimeStopped = $false }
        'CleanupNotSucceeded' { $native.cleanupSucceeded = $false }
        'WrongReason' { $native.observedReason = 'WindowClose' }
        'WrongPhase' { $native.phase = 'Unknown' }
        'ExceededBudget' { $native.elapsedMilliseconds = 5000 }
        'ClaimsProcessExit' { $native.processExitObserved = $true }
    }
    [IO.File]::WriteAllText((Join-Path $runRoot 'armed.json'), ($armed | ConvertTo-Json -Depth 8))
    [IO.File]::WriteAllText((Join-Path $configRoot 'assignments.json'), (@{ schemaVersion = 1; assignments = @($assignment) } | ConvertTo-Json -Depth 8))
    [IO.File]::WriteAllText((Join-Path $configRoot 'playback-policy.json'), '{}')
    [IO.File]::WriteAllText((Join-Path $configRoot 'host-exit-receipt.json'), ($receipt | ConvertTo-Json -Depth 8))
    if ($scenario -ne 'MissingNative') {
        [IO.File]::WriteAllText((Join-Path $configRoot 'host-native-exit-receipt.json'), ($native | ConvertTo-Json -Depth 8))
    }
    [IO.File]::WriteAllText((Join-Path $runRoot 'stdout.log'), "HOST_RUNNING`r`n")
    [IO.File]::WriteAllText((Join-Path $runRoot 'stderr.log'), '')
    $output = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File (Join-Path $PSScriptRoot 'run-host-session-end.ps1') `
        -Mode Verify -RunDirectory $runRoot 2>&1
    $code = $LASTEXITCODE
    $expectedSuccess = $scenario -in @('ValidNativeNoStdoutTail', 'ActiveValid')
    $valid = ($code -eq 0) -eq $expectedSuccess
    $results.Add([pscustomobject]@{ scenario = $scenario; exitCode = $code; expectedSuccess = $expectedSuccess; passed = $valid })
    if (-not $valid) { Write-Output $output }
}
$summary = @{ scope = 'SyntheticEvidenceOnly'; recordedAt = [DateTimeOffset]::UtcNow.ToString('O');
    allPassed = @($results | Where-Object { -not $_.passed }).Count -eq 0; runs = $results.ToArray() }
[IO.File]::WriteAllText((Join-Path $batchRoot 'summary.json'), ($summary | ConvertTo-Json -Depth 8))
Write-Output ($summary | ConvertTo-Json -Depth 8)
Write-Output "Evidence=$batchRoot"
if (-not $summary.allPassed) { exit 1 }
exit 0
