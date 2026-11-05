[CmdletBinding()]
param(
    [ValidateSet('Arm', 'Verify')][string]$Mode = 'Arm',
    [string]$RunDirectory,
    [switch]$WithActiveRenderer
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$evidenceRoot = Join-Path $repositoryRoot 'artifacts/diagnostics/host-session-end'

function Write-EvidenceJson([string]$Path, [object]$Value) {
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 10))
    [IO.File]::Move($temporary, $Path, $true)
}

if ($Mode -eq 'Verify') {
    if ([string]::IsNullOrWhiteSpace($RunDirectory)) { throw 'Verify requires the exact RunDirectory printed by Arm.' }
    $runRoot = [IO.Path]::GetFullPath($RunDirectory)
    $allowedRoot = [IO.Path]::GetFullPath($evidenceRoot).TrimEnd('\') + '\'
    if (-not $runRoot.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'RunDirectory must be inside this project host-session-end evidence directory.'
    }
    $armed = Get-Content -LiteralPath (Join-Path $runRoot 'armed.json') -Raw | ConvertFrom-Json
    $configurationRoot = Join-Path $runRoot 'configuration'
    $receiptPath = Join-Path $configurationRoot 'host-exit-receipt.json'
    $receipt = if (Test-Path -LiteralPath $receiptPath) { Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json } else { $null }
    $saved = Get-Content -LiteralPath (Join-Path $configurationRoot 'assignments.json') -Raw | ConvertFrom-Json
    $assignmentPreserved = $saved.schemaVersion -eq 1 -and $saved.assignments.Count -eq 1 -and
        $saved.assignments[0].displayId -eq $armed.expectedAssignment.displayId -and
        $saved.assignments[0].wallpaperId -eq $armed.expectedAssignment.wallpaperId -and
        $saved.assignments[0].layoutMode -eq $armed.expectedAssignment.layoutMode -and
        $saved.assignments[0].fitMode -eq $armed.expectedAssignment.fitMode -and
        $saved.assignments[0].presetId -eq $armed.expectedAssignment.presetId
    $receiptVerified = $null -ne $receipt -and $receipt.schemaVersion -eq 1 -and
        $receipt.reason -eq 'SessionLogoff' -and $receipt.runtimeStopped -and $receipt.cleanupSucceeded -and
        $null -eq $receipt.failureType -and
        [DateTimeOffset]::Parse($receipt.recordedAt) -ge [DateTimeOffset]::Parse($armed.armedAt)
    $stdout = Get-Content -LiteralPath (Join-Path $runRoot 'stdout.log') -Raw
    $stderr = Get-Content -LiteralPath (Join-Path $runRoot 'stderr.log') -Raw
    $processExitLogged = $stdout -match 'HOST_STOPPED reason=SessionLogoff \| windowWait=Completed \| exitCode=0'
    # Prospective v2 evidence: do not relax the original stdout-only gate for old runs.
    $usesNativeEvidence = $armed.nativeExitEvidenceVersion -eq 1
    if ($null -ne $armed.nativeExitEvidenceVersion -and -not $usesNativeEvidence) {
        throw 'Unsupported native exit evidence version; refusing to fall back to the legacy gate.'
    }
    $nativePath = Join-Path $configurationRoot 'host-native-exit-receipt.json'
    $native = if ($usesNativeEvidence -and (Test-Path -LiteralPath $nativePath)) {
        Get-Content -LiteralPath $nativePath -Raw | ConvertFrom-Json
    } else { $null }
    $nativeVerified = $usesNativeEvidence -and $receiptVerified -and $null -ne $native -and
        $native.schemaVersion -eq 1 -and $native.runId -eq $receipt.runId -and
        $native.phase -eq 'NativeCallbackCleanupObserved' -and
        $native.observedReason -eq 'SessionLogoff' -and $native.stopReason -eq 'SessionLogoff' -and
        $native.cleanupOutcome -eq 'Completed' -and $native.runtimeStopped -and $native.cleanupSucceeded -and
        $null -eq $native.failureType -and $native.callbackReturnObserved -eq $false -and $native.processExitObserved -eq $false -and
        $native.totalBudgetMilliseconds -gt 0 -and $native.totalBudgetMilliseconds -le 4000 -and
        $native.elapsedMilliseconds -ge 0 -and $native.elapsedMilliseconds -lt $native.totalBudgetMilliseconds -and
        [DateTimeOffset]::Parse($native.recordedAt) -ge [DateTimeOffset]::Parse($receipt.recordedAt)
    $policySaved = Test-Path -LiteralPath (Join-Path $configurationRoot 'playback-policy.json')
    $activeExpected = $armed.activeSessionEvidenceVersion -eq 1
    if ($null -ne $armed.activeSessionEvidenceVersion -and -not $activeExpected) {
        throw 'Unsupported active session-end evidence version.'
    }
    $activePath = Join-Path $runRoot 'active-exit-evidence.json'
    $active = if ($activeExpected -and (Test-Path -LiteralPath $activePath)) {
        Get-Content -LiteralPath $activePath -Raw | ConvertFrom-Json
    } else { $null }
    $expectedSessions = @($armed.expectedSessionIds)
    $activeVerified = $activeExpected -and $usesNativeEvidence -and $nativeVerified -and $null -ne $active -and
        $active.schemaVersion -eq 1 -and $active.runId -eq $receipt.runId -and $active.valid -and
        $active.initialCommitSucceeded -and $armed.initialGeneration -gt 0 -and $active.generation -eq $armed.initialGeneration -and
        $active.ownedSurfaceCount -eq 0 -and $expectedSessions.Count -eq 1 -and -not [string]::IsNullOrWhiteSpace($expectedSessions[0]) -and
        $active.expectedSessionIds.Count -eq 1 -and $active.expectedSessionIds[0] -eq $expectedSessions[0] -and
        $active.retirement.Count -eq 1 -and $active.rendererCleanup.Count -eq 1
    if ($activeVerified) {
        $retired = $active.retirement[0]
        $rendererExit = $active.rendererCleanup[0]
        $activeVerified = $retired.sessionId.value -eq $expectedSessions[0] -and
            $retired.generation -eq $armed.initialGeneration -and $retired.surfaceOrdinal -eq 1 -and
            $retired.shutdownSent -and $retired.shutdownCompleted -and $retired.rendererDisposed -and
            $retired.surfacesCleaned -and $retired.issues.Count -eq 0 -and
            $rendererExit.sessionId -eq $expectedSessions[0] -and $rendererExit.exitObserved -and
            $rendererExit.handleDisposed -and $null -eq $rendererExit.issue
    }
    $terminalEvidence = if ($usesNativeEvidence) { $nativeVerified } else { $processExitLogged }
    $valid = $receiptVerified -and $assignmentPreserved -and $policySaved -and $terminalEvidence -and
        [string]::IsNullOrWhiteSpace($stderr) -and (-not $activeExpected -or $activeVerified)
    $result = [ordered]@{
        schemaVersion = 1; runId = $armed.runId; checkedAt = [DateTimeOffset]::UtcNow.ToString('O')
        classification = $(if (-not $valid) { 'IncompleteOrFailed' } elseif ($activeExpected) { 'ActiveRendererSessionLogoffEvidenceValidated' } elseif ($usesNativeEvidence) { 'SessionLogoffCallbackEvidenceValidated' } else { 'SessionLogoffEvidenceValidated' })
        receiptVerified = $receiptVerified; assignmentPreserved = $assignmentPreserved
        policySaved = $policySaved; processExitLogged = $processExitLogged
        stderrEmpty = [string]::IsNullOrWhiteSpace($stderr)
        exitReceipt = $receipt; nativeReceipt = $native; nativeReceiptVerified = $nativeVerified
        evidenceVersion = $(if ($activeExpected) { 3 } elseif ($usesNativeEvidence) { 2 } else { 1 })
        processExitCodeObserved = $processExitLogged; callbackReturnObserved = $false
        activeWallpaperSurface = $(if ($activeExpected) { 'CreatedAtArm' } else { 'NotCreated' })
        activeSessionEvidence = $active; activeSessionEvidenceVerified = $activeVerified
        userMustConfirmManualLogoff = $true; systemLogoffTriggeredByHarness = $false
    }
    $verificationName = if ($activeExpected) { 'verification-v3.json' } elseif ($usesNativeEvidence) { 'verification-v2.json' } else { 'verification.json' }
    Write-EvidenceJson (Join-Path $runRoot $verificationName) $result
    Write-Output ($result | ConvertTo-Json -Depth 10)
    if (-not $valid) { exit 1 }
    return
}

if (-not [string]::IsNullOrWhiteSpace($RunDirectory)) { throw 'Arm always creates a fresh directory; do not reuse a prior run.' }
. (Join-Path $PSScriptRoot 'use-dotnet.ps1')
$hostExecutable = Join-Path $repositoryRoot 'src/LiveWall.Host/bin/Release/net10.0-windows10.0.18362.0/LiveWall.Host.exe'
if ($WithActiveRenderer) { $hostExecutable = Join-Path $repositoryRoot 'tools/DesktopHostDiagnostics/bin/Release/net10.0-windows10.0.18362.0/DesktopHostDiagnostics.exe' }
if (-not (Test-Path -LiteralPath $hostExecutable -PathType Leaf)) { throw 'Build the Release Host first.' }

if (-not ('LiveWallSessionEndObserverNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class LiveWallSessionEndObserverNative {
    public delegate bool EnumProc(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetProcessWindowStation();
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder text, uint length, out uint needed);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", SetLastError=true)]
    static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll", SetLastError=true)] static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    public static string ObjectName(IntPtr handle) {
        var text = new StringBuilder(256);
        if (!GetUserObjectInformation(handle, 2, text, 512, out uint needed))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return text.ToString();
    }
    public static IntPtr FindLifecycle(uint process) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, data) => {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner != process) return true;
            var text = new StringBuilder(256);
            GetWindowText(window, text, text.Capacity);
            if (text.ToString() != "LiveWall Host Lifecycle") return true;
            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }
    public static bool IsReady(IntPtr window, uint process) {
        GetWindowThreadProcessId(window, out uint owner);
        if (window == IntPtr.Zero || owner != process) return false;
        return SendMessageTimeout(window, 0x8033, UIntPtr.Zero, IntPtr.Zero, 2, 1000, out UIntPtr result) != IntPtr.Zero && result.ToUInt64() == 1;
    }
    public static bool CloseOwned(IntPtr window, uint process) {
        GetWindowThreadProcessId(window, out uint owner);
        return window != IntPtr.Zero && owner == process && PostMessage(window, 0x10, UIntPtr.Zero, IntPtr.Zero);
    }
}
'@
}
$currentSession = [Diagnostics.Process]::GetCurrentProcess().SessionId
$station = [LiveWallSessionEndObserverNative]::ObjectName([LiveWallSessionEndObserverNative]::GetProcessWindowStation())
$desktop = [LiveWallSessionEndObserverNative]::ObjectName([LiveWallSessionEndObserverNative]::GetThreadDesktop([LiveWallSessionEndObserverNative]::GetCurrentThreadId()))
$shell = [LiveWallSessionEndObserverNative]::GetShellWindow()
if ($station -ne 'WinSta0' -or $desktop -ne 'Default' -or $shell -eq [IntPtr]::Zero) {
    throw 'EnvironmentBlocked: require an interactive WinSta0\Default with a visible Explorer Shell.'
}
$shellProcess = [uint32]0
$null = [LiveWallSessionEndObserverNative]::GetWindowThreadProcessId($shell, [ref]$shellProcess)
if ([Diagnostics.Process]::GetProcessById($shellProcess).SessionId -ne $currentSession) {
    throw 'EnvironmentBlocked: Explorer and the observer must belong to the same session.'
}
$build = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$runId = [Guid]::NewGuid().ToString('N')
$runRoot = Join-Path $evidenceRoot ('run-' + $runId)
$configurationRoot = Join-Path $runRoot 'configuration'
New-Item -ItemType Directory -Path $configurationRoot | Out-Null
$assignment = [ordered]@{
    displayId = 'diagnostic.offline.display'; wallpaperId = 'diagnostic.offline.wallpaper'
    layoutMode = 'perDisplay'; fitMode = 'cover'; presetId = $null
}
if (-not $WithActiveRenderer) { Write-EvidenceJson (Join-Path $configurationRoot 'assignments.json') @{ schemaVersion = 1; assignments = @($assignment) } }
$arguments = '--installation-scope logoff-' + $runId.Substring(0, 16) + ' --configuration-directory "' + $configurationRoot + '"'
if ($WithActiveRenderer) { $arguments = '--raised-desktop-host-session-end --run-directory "' + $runRoot + '"' }
$process = Start-Process -FilePath $hostExecutable -ArgumentList $arguments -WorkingDirectory $repositoryRoot -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput (Join-Path $runRoot 'stdout.log') -RedirectStandardError (Join-Path $runRoot 'stderr.log')
try {
    $startupStarted = [DateTime]::UtcNow
    $deadline = $startupStarted.AddSeconds(20)
    $activeBudgetRead = $false
    $ready = $false
    while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        $startingPath = Join-Path $runRoot 'active-starting.json'
        if ($WithActiveRenderer -and -not $activeBudgetRead -and (Test-Path -LiteralPath $startingPath)) {
            $starting = Get-Content -LiteralPath $startingPath -Raw | ConvertFrom-Json
            if ($starting.startupBudgetMilliseconds -le 0 -or $starting.startupBudgetMilliseconds -gt 600000) { throw 'Invalid active startup budget.' }
            $deadline = $startupStarted.AddMilliseconds($starting.startupBudgetMilliseconds + 5000)
            $activeBudgetRead = $true
        }
        $window = [LiveWallSessionEndObserverNative]::FindLifecycle([uint32]$process.Id)
        if ([LiveWallSessionEndObserverNative]::IsReady($window, [uint32]$process.Id)) { $ready = $true; break }
        Start-Sleep -Milliseconds 50
    }
    if (-not $ready) { throw 'Host did not reach Running; do not log off for this run.' }
    if ($process.SessionId -ne $currentSession) { throw 'Host was started in the wrong session; do not log off.' }
    if (Test-Path -LiteralPath (Join-Path $configurationRoot 'host-exit-receipt.json')) {
        throw 'An exit receipt exists before arming; do not log off for this run.'
    }
    $activeReady = $null
    if ($WithActiveRenderer) {
        $activeReady = Get-Content -LiteralPath (Join-Path $runRoot 'active-ready.json') -Raw | ConvertFrom-Json
        if (-not $activeReady.firstFrameCommitted -or $activeReady.activeSurfaceCount -ne 1 -or $activeReady.expectedSessionIds.Count -ne 1) {
            throw 'Active renderer did not commit exactly one first frame; do not sign out for this run.'
        }
        if ($activeReady.hostShutdownLevel -ne 0x281 -or $activeReady.rendererShutdownLevel -ne 0x280) {
            throw 'The active observer has not configured Host-before-Renderer shutdown ordering.'
        }
        $activeAssignments = Get-Content -LiteralPath (Join-Path $configurationRoot 'assignments.json') -Raw | ConvertFrom-Json
        if ($activeAssignments.assignments.Count -ne 1) { throw 'The active assignment seed is incomplete.' }
        $assignment = $activeAssignments.assignments[0]
    }
    Write-EvidenceJson (Join-Path $runRoot 'armed.json') ([ordered]@{
        schemaVersion = 1; runId = $runId; armedAt = [DateTimeOffset]::UtcNow.ToString('O')
        classification = 'ArmedAwaitingManualLogoff'; fullBuild = "$($build.CurrentBuildNumber).$($build.UBR)"
        executionContext = @{ sessionId = $currentSession; windowStationName = $station; desktopName = $desktop }
        expectedAssignment = $assignment; activeWallpaperSurface = $(if ($WithActiveRenderer) { 'CreatedAtArm' } else { 'NotCreated' })
        activeSessionEvidenceVersion = $(if ($WithActiveRenderer) { 1 } else { $null })
        initialGeneration = $(if ($activeReady) { $activeReady.generation } else { $null })
        hostShutdownLevel = $(if ($activeReady) { $activeReady.hostShutdownLevel } else { $null })
        rendererShutdownLevel = $(if ($activeReady) { $activeReady.rendererShutdownLevel } else { $null })
        expectedSessionIds = @(if ($activeReady) { $activeReady.expectedSessionIds })
        nativeExitEvidenceVersion = 1; hostExecutableSha256 = (Get-FileHash -LiteralPath $hostExecutable -Algorithm SHA256).Hash
        systemLogoffTriggeredByHarness = $false; autoStartRegistered = $false
    })
    Write-Output 'SESSION_END_OBSERVER_ARMED: save all work, then manually SIGN OUT (not Lock or Sleep).'
    if ($WithActiveRenderer) { Write-Output 'ACTIVE_RENDERER: orange remains visible until sign-out (10 minute maximum); no automatic OS operation.' }
    Write-Output "RunDirectory=$runRoot"
    Write-Output 'After signing in again, reopen this project and ask to verify the session-end receipt.'
} catch {
    if (-not $process.HasExited) {
        $ownedWindow = [LiveWallSessionEndObserverNative]::FindLifecycle([uint32]$process.Id)
        $null = [LiveWallSessionEndObserverNative]::CloseOwned($ownedWindow, [uint32]$process.Id)
        $null = $process.WaitForExit(5000)
    }
    Write-EvidenceJson (Join-Path $runRoot 'arming-failure.json') @{
        classification = 'HarnessFailure'; detail = $_.Exception.Message; hostStillRunning = -not $process.HasExited
    }
    throw
} finally { $process.Dispose() }
