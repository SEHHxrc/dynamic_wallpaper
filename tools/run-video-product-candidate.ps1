[CmdletBinding()]
param(
    [switch] $PreflightOnly,
    [switch] $ConfirmDesktopPresentation,
    [switch] $ControlRecovery,
    [ValidateRange(1, 30)][int] $DurationSeconds = 10,
    [string] $ResultJson
)

$ErrorActionPreference = 'Stop'
if ($ControlRecovery) {
    if ($PSBoundParameters.ContainsKey('DurationSeconds') -and $DurationSeconds -ne 20) {
        throw 'Control acceptance requires four fixed 5-second stages (20 seconds total).'
    }
    $DurationSeconds = 20
}
if (-not $PreflightOnly -and -not $ConfirmDesktopPresentation) {
    throw 'Visible video requires -ConfirmDesktopPresentation. Use -PreflightOnly for read-only checks.'
}
$candidateRoot = Split-Path -Parent $PSScriptRoot
$candidateExecutable = Join-Path $candidateRoot 'tools/DesktopHostDiagnostics/bin/Release/net10.0-windows10.0.18362.0/DesktopHostDiagnostics.exe'
$rendererExecutable = Join-Path $candidateRoot 'src/LiveWall.Renderer.Video/bin/Release/net10.0-windows10.0.18362.0/LiveWall.Renderer.Video.exe'
$nativeLibrary = Join-Path $candidateRoot '.cache/libmpv/20261021-x64/libmpv-2.dll'
$fixtureRoot = Join-Path $candidateRoot 'artifacts/diagnostics/video-c0/fixture-v1-20261024'
$candidateSdk = Join-Path $candidateRoot '.dotnet'
foreach ($requiredPath in @($candidateExecutable, $rendererExecutable, $nativeLibrary, (Join-Path $candidateSdk 'dotnet.exe'))) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) { throw "Build/bootstrap the project first: $requiredPath" }
}
if ([string]::IsNullOrWhiteSpace($ResultJson)) {
    $ResultJson = Join-Path $candidateRoot ('artifacts/diagnostics/video-c1/video-product-' + [Guid]::NewGuid().ToString('N') + '.json')
}
elseif (-not [IO.Path]::IsPathFullyQualified($ResultJson)) { $ResultJson = Join-Path $candidateRoot $ResultJson }
$ResultJson = [IO.Path]::GetFullPath($ResultJson)
if (Test-Path -LiteralPath $ResultJson) { throw 'Use a fresh result file; existing evidence will not be overwritten.' }
$candidateArgs = @('--video-product-candidate', '--renderer-executable', $rendererExecutable,
    '--content-root', $fixtureRoot, '--libmpv', $nativeLibrary, '--result-json', $ResultJson,
    '--duration-seconds', $DurationSeconds.ToString([Globalization.CultureInfo]::InvariantCulture))
if ($PreflightOnly) { $candidateArgs += '--preflight-only' }
else { $candidateArgs += '--confirm-desktop-presentation' }
if ($ControlRecovery) { $candidateArgs += '--control-recovery' }

# Only select the project runtime for this invocation. Never edit PATH, user/machine
# environment or profiles, and restore even when the command fails or is cancelled.
$runtimeVariables = @('DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_MULTILEVEL_LOOKUP')
$previousRuntime = @{}
foreach ($runtimeVariable in $runtimeVariables) {
    $previousRuntime[$runtimeVariable] = [Environment]::GetEnvironmentVariable($runtimeVariable, 'Process')
}
$candidateExit = 1
try {
    $env:DOTNET_ROOT = $candidateSdk
    $env:DOTNET_ROOT_X64 = $candidateSdk
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    & $candidateExecutable @candidateArgs
    $candidateExit = $LASTEXITCODE
}
finally {
    foreach ($runtimeVariable in $runtimeVariables) {
        if ($null -eq $previousRuntime[$runtimeVariable]) {
            Remove-Item -LiteralPath ('Env:' + $runtimeVariable) -ErrorAction SilentlyContinue
        }
        else { [Environment]::SetEnvironmentVariable($runtimeVariable, $previousRuntime[$runtimeVariable], 'Process') }
    }
}
Write-Host "Video candidate exit=$candidateExit; result=$ResultJson"
exit $candidateExit
