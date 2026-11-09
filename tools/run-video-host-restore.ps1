[CmdletBinding()]
param(
    [switch] $PreflightOnly,
    [switch] $ConfirmDesktopPresentation,
    [switch] $CrashRecovery,
    [ValidateRange(1, 4)][int] $Crashes = 2,
    [ValidateRange(3, 30)][int] $DurationSeconds = 8,
    [string] $SourceVideo
)

# Visible Host scenarios with an imported local video. Default (C2.1): show it with one Host, stop, and let a
# second Host restore it from the persisted data root. -CrashRecovery (C3): kill the Renderer -Crashes times and
# expect the ARCHITECTURE section 11.5 restarts. Written for Windows PowerShell 5.1 and PowerShell 7.
$ErrorActionPreference = 'Stop'
if ($PreflightOnly -eq $ConfirmDesktopPresentation) {
    throw 'Use exactly one of -PreflightOnly (read-only) or -ConfirmDesktopPresentation (visible).'
}
if ($PSBoundParameters.ContainsKey('Crashes') -and -not $CrashRecovery) {
    throw '-Crashes only applies together with -CrashRecovery.'
}
if ($CrashRecovery -and -not $PSBoundParameters.ContainsKey('DurationSeconds')) { $DurationSeconds = 6 }
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$framework = 'net10.0-windows10.0.18362.0'
$tool = Join-Path $repositoryRoot "tools/DesktopHostDiagnostics/bin/Release/$framework/DesktopHostDiagnostics.exe"
$renderer = Join-Path $repositoryRoot "src/LiveWall.Renderer.Video/bin/Release/$framework/LiveWall.Renderer.Video.exe"
$nativeLibrary = Join-Path $repositoryRoot '.cache/libmpv/20261021-x64/libmpv-2.dll'
$sdk = Join-Path $repositoryRoot '.dotnet'
if ([string]::IsNullOrWhiteSpace($SourceVideo)) {
    $SourceVideo = Join-Path $repositoryRoot 'artifacts/diagnostics/video-c0/fixture-v1-20261024/content/motion.avi'
}
foreach ($requiredPath in @($tool, $renderer, $nativeLibrary, $SourceVideo, (Join-Path $sdk 'dotnet.exe'))) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) { throw "Build/bootstrap the project first: $requiredPath" }
}
# Resolve against the PowerShell location; .NET Framework's GetFullPath would use the process directory.
$SourceVideo = (Resolve-Path -LiteralPath $SourceVideo).ProviderPath
if ($CrashRecovery) {
    $mode = '--video-host-crash-recovery'
    $runDirectory = Join-Path $repositoryRoot ('artifacts/diagnostics/video-c3/crash-' + [Guid]::NewGuid().ToString('N'))
}
else {
    $mode = '--video-host-restore'
    $runDirectory = Join-Path $repositoryRoot ('artifacts/diagnostics/video-c2/restore-' + [Guid]::NewGuid().ToString('N'))
}
$toolArguments = @($mode, '--renderer-executable', $renderer, '--libmpv', $nativeLibrary,
    '--source-video', $SourceVideo, '--run-directory', $runDirectory,
    '--duration-seconds', $DurationSeconds.ToString([Globalization.CultureInfo]::InvariantCulture))
if ($CrashRecovery) { $toolArguments += @('--crashes', $Crashes.ToString([Globalization.CultureInfo]::InvariantCulture)) }
if ($PreflightOnly) { $toolArguments += '--preflight-only' }
else { $toolArguments += '--confirm-desktop-presentation' }

# Select the project runtime for this invocation only; never edit PATH, user/machine variables or profiles.
$runtimeVariables = @('DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_MULTILEVEL_LOOKUP')
$previousRuntime = @{}
foreach ($runtimeVariable in $runtimeVariables) {
    $previousRuntime[$runtimeVariable] = [Environment]::GetEnvironmentVariable($runtimeVariable, 'Process')
}
$toolExit = 1
try {
    $env:DOTNET_ROOT = $sdk
    $env:DOTNET_ROOT_X64 = $sdk
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    & $tool @toolArguments
    $toolExit = $LASTEXITCODE
}
finally {
    foreach ($runtimeVariable in $runtimeVariables) {
        if ($null -eq $previousRuntime[$runtimeVariable]) {
            Remove-Item -LiteralPath ('Env:' + $runtimeVariable) -ErrorAction SilentlyContinue
        }
        else { [Environment]::SetEnvironmentVariable($runtimeVariable, $previousRuntime[$runtimeVariable], 'Process') }
    }
}
Write-Host "Host scenario exit=$toolExit; run=$runDirectory"
exit $toolExit
