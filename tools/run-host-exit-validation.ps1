[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory = 'artifacts/diagnostics/host-exit'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'use-dotnet.ps1')
$hostExecutable = Join-Path $repositoryRoot "src/LiveWall.Host/bin/$Configuration/net10.0-windows10.0.18362.0/LiveWall.Host.exe"
if (-not (Test-Path -LiteralPath $hostExecutable -PathType Leaf)) { throw 'Build the Host project before running exit validation.' }
$outputBase = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $repositoryRoot $OutputDirectory }
$outputRoot = Join-Path $outputBase ('batch-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

if (-not ('LiveWallExitHarnessNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class LiveWallExitHarnessNative {
    public delegate bool EnumProc(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
    public static IntPtr FindLifecycleWindow(uint process) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, data) => {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner != process) return true;
            var title = new StringBuilder(256);
            GetWindowText(window, title, title.Capacity);
            if (title.ToString() != "LiveWall Host Lifecycle") return true;
            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }
    public static int CountOwnedWindows(uint process) {
        int count = 0;
        EnumWindows((window, data) => { GetWindowThreadProcessId(window, out uint owner); if (owner == process) count++; return true; }, IntPtr.Zero);
        return count;
    }
    public static ulong SendOwned(IntPtr window, uint process, uint message, ulong wParam, long lParam) {
        GetWindowThreadProcessId(window, out uint owner);
        if (window == IntPtr.Zero || owner != process) throw new InvalidOperationException("Refusing to send a message to another process.");
        if (SendMessageTimeout(window, message, (UIntPtr)wParam, (IntPtr)lParam, 2, 6000, out UIntPtr result) == IntPtr.Zero)
            throw new InvalidOperationException("Owned lifecycle message failed or timed out.");
        return result.ToUInt64();
    }
}
'@
}

$runs = [Collections.Generic.List[object]]::new()
foreach ($scenario in @('Close', 'QueryCancelledThenClose', 'ShutdownMessage', 'LogoffMessage', 'CloseAppMessage')) {
    $runRoot = Join-Path $outputRoot $scenario
    New-Item -ItemType Directory -Path $runRoot | Out-Null
    $configurationRoot = Join-Path $runRoot 'configuration'
    New-Item -ItemType Directory -Path $configurationRoot | Out-Null
    $assignmentPath = Join-Path $configurationRoot 'assignments.json'
    $seed = @{ schemaVersion = 1; assignments = @(@{ displayId = 'diagnostic.offline.display'; wallpaperId = 'diagnostic.offline.wallpaper'; layoutMode = 'perDisplay'; fitMode = 'cover'; presetId = $null }) }
    [IO.File]::WriteAllText($assignmentPath, ($seed | ConvertTo-Json -Depth 6))
    $start = [Diagnostics.ProcessStartInfo]::new($hostExecutable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add('--installation-scope')
    $start.ArgumentList.Add('exit-' + [Guid]::NewGuid().ToString('N').Substring(0, 16))
    $start.ArgumentList.Add('--configuration-directory')
    $start.ArgumentList.Add($configurationRoot)
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $failureType = $null
    $failureDetail = $null
    $cancelledQueryPreserved = $null
    $ownedWindowsAfter = $null
    $forcedCleanup = $false
    $exitCode = $null
    $persisted = $false
    $receiptVerified = $false
    $nativeReceiptVerified = $false
    $foundWindow = $false
    $lastReadiness = $null
    $startupOwnedWindows = $null
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        $window = [IntPtr]::Zero
        $ready = $false
        while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
            $window = [LiveWallExitHarnessNative]::FindLifecycleWindow([uint32]$process.Id)
            if ($window -ne [IntPtr]::Zero) {
                $foundWindow = $true
                $lastReadiness = [LiveWallExitHarnessNative]::SendOwned($window, [uint32]$process.Id, 0x8033, 0, 0)
                if ($lastReadiness -eq 1) { $ready = $true; break }
            }
            Start-Sleep -Milliseconds 50
        }
        $startupOwnedWindows = [LiveWallExitHarnessNative]::CountOwnedWindows([uint32]$process.Id)
        if (-not $ready) { throw 'Host did not reach Running with its owned lifecycle window.' }
        if ($scenario -ne 'Close') {
            if ([LiveWallExitHarnessNative]::SendOwned($window, [uint32]$process.Id, 0x11, 0, 0) -ne 1) { throw 'QueryEndSession did not immediately consent.' }
        }
        if ($scenario -eq 'QueryCancelledThenClose') {
            $null = [LiveWallExitHarnessNative]::SendOwned($window, [uint32]$process.Id, 0x16, 0, 0)
            $cancelledQueryPreserved = -not $process.HasExited -and
                [LiveWallExitHarnessNative]::SendOwned($window, [uint32]$process.Id, 0x8033, 0, 0) -eq 1
            if (-not $cancelledQueryPreserved) { throw 'Cancelled session end stopped the Host.' }
        }
        if ($scenario -in @('Close', 'QueryCancelledThenClose')) {
            $null = [LiveWallExitHarnessNative]::SendOwned($window, [uint32]$process.Id, 0x10, 0, 0)
        } else {
            $flags = if ($scenario -eq 'LogoffMessage') { [long]2147483648 } elseif ($scenario -eq 'CloseAppMessage') { [long]1 } else { [long]0 }
            $null = [LiveWallExitHarnessNative]::SendOwned($window, [uint32]$process.Id, 0x16, 1, $flags)
        }
        if (-not $process.WaitForExit(15000)) { throw 'The owned Host process failed to exit.' }
        $exitCode = $process.ExitCode
        $ownedWindowsAfter = [LiveWallExitHarnessNative]::CountOwnedWindows([uint32]$process.Id)
        $saved = Get-Content -LiteralPath $assignmentPath -Raw | ConvertFrom-Json
        $persisted = (Test-Path -LiteralPath (Join-Path $configurationRoot 'playback-policy.json')) -and
            $saved.schemaVersion -eq 1 -and $saved.assignments.Count -eq 1 -and
            $saved.assignments[0].displayId -eq 'diagnostic.offline.display' -and
            $saved.assignments[0].wallpaperId -eq 'diagnostic.offline.wallpaper'
        $receipt = Get-Content -LiteralPath (Join-Path $configurationRoot 'host-exit-receipt.json') -Raw | ConvertFrom-Json
        $expectedReason = switch ($scenario) { 'ShutdownMessage' { 'SessionShutdown' } 'LogoffMessage' { 'SessionLogoff' } 'CloseAppMessage' { 'SessionCloseApp' } default { 'WindowClose' } }
        $receiptVerified = $receipt.schemaVersion -eq 1 -and $receipt.runtimeStopped -and $receipt.cleanupSucceeded -and $receipt.reason -eq $expectedReason
        $native = Get-Content -LiteralPath (Join-Path $configurationRoot 'host-native-exit-receipt.json') -Raw | ConvertFrom-Json
        $nativeReceiptVerified = $native.schemaVersion -eq 1 -and $native.runId -eq $receipt.runId -and
            $native.phase -eq 'NativeCallbackCleanupObserved' -and $native.cleanupOutcome -eq 'Completed' -and
            $native.observedReason -eq $expectedReason -and $native.stopReason -eq $expectedReason -and
            $native.runtimeStopped -and $native.cleanupSucceeded -and $null -eq $native.failureType -and
            $native.callbackReturnObserved -eq $false -and $native.processExitObserved -eq $false
    } catch { $failureType = $_.Exception.GetType().Name; $failureDetail = $_.Exception.Message } finally {
        if (-not $process.HasExited) { $forcedCleanup = $true; $process.Kill(); $process.WaitForExit() }
        [IO.File]::WriteAllText((Join-Path $runRoot 'stdout.log'), $stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $runRoot 'stderr.log'), $stderr.GetAwaiter().GetResult())
        $process.Dispose()
    }
    $valid = $failureType -eq $null -and $exitCode -eq 0 -and $ownedWindowsAfter -eq 0 -and $persisted -and $receiptVerified -and $nativeReceiptVerified -and -not $forcedCleanup
    $runs.Add([pscustomobject]@{ scenario = $scenario; classification = $(if ($valid) { 'ControlledProcessGatePassed' } else { 'Failed' }); exitCode = $exitCode;
        cancelledQueryPreserved = $cancelledQueryPreserved; ownedWindowsAfter = $ownedWindowsAfter; assignmentPreserved = $persisted;
        forcedCleanup = $forcedCleanup; failureType = $failureType; failureDetail = $failureDetail; receiptVerified = $receiptVerified; nativeReceiptVerified = $nativeReceiptVerified;
        lifecycleWindowFound = $foundWindow; lastReadiness = $lastReadiness; startupOwnedWindows = $startupOwnedWindows; realSystemLogoffOrShutdown = $false })
    if (-not $valid) { break }
}
$summary = [pscustomobject]@{ schemaVersion = 1; recordedAt = [DateTimeOffset]::UtcNow; scope = 'OwnedProcessMessageInjection';
    requested = 5; executed = $runs.Count; allPassed = ($runs.Count -eq 5 -and @($runs | Where-Object classification -ne 'ControlledProcessGatePassed').Count -eq 0);
    realSystemLogoffOrShutdown = 'NotExecuted'; activeWallpaperSurface = 'NotCreated'; runs = $runs.ToArray() }
$summaryPath = Join-Path $outputRoot 'summary.json'
[IO.File]::WriteAllText($summaryPath, ($summary | ConvertTo-Json -Depth 8))
Write-Output ($summary | ConvertTo-Json -Depth 8)
Write-Output "Structured result: $summaryPath"
if (-not $summary.allPassed) { exit 1 }
