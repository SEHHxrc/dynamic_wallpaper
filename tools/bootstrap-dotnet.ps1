[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$globalJsonPath = Join-Path $repositoryRoot 'global.json'
$sdkConfiguration = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
$sdkVersion = [string] $sdkConfiguration.sdk.version
$vsCodeRuntimeVersion = '10.0.5'

if ([string]::IsNullOrWhiteSpace($sdkVersion)) {
    throw 'global.json does not declare sdk.version.'
}

$projectDotnetRoot = Join-Path $repositoryRoot '.dotnet'
$projectDotnetExecutable = Join-Path $projectDotnetRoot 'dotnet.exe'
$downloadCache = Join-Path $repositoryRoot '.cache'
$installerPath = Join-Path $downloadCache 'dotnet-install.ps1'

function Ensure-DotnetInstaller {
    if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
        Write-Host 'Downloading the official .NET installer...'
        Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installerPath
    }
}

function Invoke-DotnetInstaller {
    param(
        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    $powerShellExecutable = Join-Path $PSHOME 'pwsh.exe'
    if (-not (Test-Path -LiteralPath $powerShellExecutable -PathType Leaf)) {
        $powerShellExecutable = 'powershell.exe'
    }

    & $powerShellExecutable `
        -NoLogo `
        -NoProfile `
        -NonInteractive `
        -ExecutionPolicy Bypass `
        -File $installerPath `
        @Arguments

    if ($LASTEXITCODE -ne 0) {
        throw "The .NET installer exited with code $LASTEXITCODE."
    }
}

function Test-SharedFrameworkVersion {
    param(
        [Parameter(Mandatory)]
        [string] $FrameworkName,

        [Parameter(Mandatory)]
        [string] $Version
    )

    if (-not (Test-Path -LiteralPath $projectDotnetExecutable -PathType Leaf)) {
        return $false
    }

    $installedRuntimes = & $projectDotnetExecutable --list-runtimes
    return [bool] ($installedRuntimes | Where-Object {
        $_ -match "^$([regex]::Escape($FrameworkName))\s+$([regex]::Escape($Version))\s"
    })
}

New-Item -ItemType Directory -Path $projectDotnetRoot -Force | Out-Null
New-Item -ItemType Directory -Path $downloadCache -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $repositoryRoot '.dotnet-cli-home') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $repositoryRoot '.nuget\packages') -Force | Out-Null

$installed = $false
if (Test-Path -LiteralPath $projectDotnetExecutable -PathType Leaf) {
    $installedSdks = & $projectDotnetExecutable --list-sdks
    $installed = $installedSdks | Where-Object { $_ -match "^$([regex]::Escape($sdkVersion))\s" }
}

if (-not $installed) {
    Ensure-DotnetInstaller

    Write-Host "Installing .NET SDK $sdkVersion into $projectDotnetRoot..."
    Invoke-DotnetInstaller -Arguments @(
        '-Version', $sdkVersion,
        '-InstallDir', $projectDotnetRoot,
        '-Architecture', 'x64',
        '-NoPath'
    )

    if (-not (Test-Path -LiteralPath $projectDotnetExecutable -PathType Leaf)) {
        throw 'The .NET SDK installer completed without creating dotnet.exe.'
    }

    $installedSdks = & $projectDotnetExecutable --list-sdks
    $installed = $installedSdks | Where-Object { $_ -match "^$([regex]::Escape($sdkVersion))\s" }
    if (-not $installed) {
        throw "The .NET SDK installer did not install requested SDK $sdkVersion."
    }
}

# C# Dev Kit 3.20.x requires both runtime families at patch 10.0.5 even though
# SDK 10.0.100 originally carries 10.0.0. Keep them beside the project SDK so
# the editor can run without a machine-wide .NET installation.
$hasVsCodeRuntime = Test-SharedFrameworkVersion -FrameworkName 'Microsoft.NETCore.App' -Version $vsCodeRuntimeVersion
$hasVsCodeAspNetRuntime = Test-SharedFrameworkVersion -FrameworkName 'Microsoft.AspNetCore.App' -Version $vsCodeRuntimeVersion

if (-not $hasVsCodeRuntime) {
    Ensure-DotnetInstaller
    Write-Host "Installing .NET Runtime $vsCodeRuntimeVersion for VS Code into $projectDotnetRoot..."
    Invoke-DotnetInstaller -Arguments @(
        '-Runtime', 'dotnet',
        '-Version', $vsCodeRuntimeVersion,
        '-InstallDir', $projectDotnetRoot,
        '-Architecture', 'x64',
        '-NoPath'
    )
}

if (-not $hasVsCodeAspNetRuntime) {
    Ensure-DotnetInstaller
    Write-Host "Installing ASP.NET Core Runtime $vsCodeRuntimeVersion for VS Code into $projectDotnetRoot..."
    Invoke-DotnetInstaller -Arguments @(
        '-Runtime', 'aspnetcore',
        '-Version', $vsCodeRuntimeVersion,
        '-InstallDir', $projectDotnetRoot,
        '-Architecture', 'x64',
        '-NoPath'
    )
}

if (-not (Test-SharedFrameworkVersion -FrameworkName 'Microsoft.NETCore.App' -Version $vsCodeRuntimeVersion)) {
    throw "The .NET installer did not install Microsoft.NETCore.App $vsCodeRuntimeVersion."
}

if (-not (Test-SharedFrameworkVersion -FrameworkName 'Microsoft.AspNetCore.App' -Version $vsCodeRuntimeVersion)) {
    throw "The .NET installer did not install Microsoft.AspNetCore.App $vsCodeRuntimeVersion."
}

$projectCliHome = Join-Path $repositoryRoot '.dotnet-cli-home'
$projectNugetRoot = Join-Path $repositoryRoot '.nuget'
$vscodeDirectory = Join-Path $repositoryRoot '.vscode'
$vscodeSettingsPath = Join-Path $vscodeDirectory 'settings.json'
New-Item -ItemType Directory -Path $vscodeDirectory -Force | Out-Null

if (Test-Path -LiteralPath $vscodeSettingsPath -PathType Leaf) {
    try {
        $vscodeSettings = Get-Content -LiteralPath $vscodeSettingsPath -Raw | ConvertFrom-Json
    }
    catch {
        throw '.vscode/settings.json must contain valid JSON before bootstrap can update it.'
    }
}
else {
    $vscodeSettings = [pscustomobject]@{}
}

$extensionDotnetPaths = @(
    [ordered]@{
        extensionId = 'ms-dotnettools.csharp'
        path = $projectDotnetExecutable
    },
    [ordered]@{
        extensionId = 'ms-dotnettools.csdevkit'
        path = $projectDotnetExecutable
    }
)
$languageServerEnvironment = [ordered]@{
    DOTNET_ROOT = $projectDotnetRoot
    DOTNET_CLI_HOME = $projectCliHome
    DOTNET_MULTILEVEL_LOOKUP = '0'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_NOLOGO = '1'
    NUGET_PACKAGES = Join-Path $projectNugetRoot 'packages'
    NUGET_HTTP_CACHE_PATH = Join-Path $projectNugetRoot 'http-cache'
    NUGET_PLUGINS_CACHE_PATH = Join-Path $projectNugetRoot 'plugins-cache'
    NUGET_SCRATCH = Join-Path $projectNugetRoot 'scratch'
}
$terminalEnvironment = [ordered]@{
    DOTNET_ROOT = '${workspaceFolder}\.dotnet'
    DOTNET_CLI_HOME = '${workspaceFolder}\.dotnet-cli-home'
    DOTNET_MULTILEVEL_LOOKUP = '0'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_NOLOGO = '1'
    NUGET_PACKAGES = '${workspaceFolder}\.nuget\packages'
    NUGET_HTTP_CACHE_PATH = '${workspaceFolder}\.nuget\http-cache'
    NUGET_PLUGINS_CACHE_PATH = '${workspaceFolder}\.nuget\plugins-cache'
    NUGET_SCRATCH = '${workspaceFolder}\.nuget\scratch'
    PATH = '${workspaceFolder}\.dotnet;${env:PATH}'
}

$vscodeSettings | Add-Member -NotePropertyName 'dotnet.defaultSolution' -NotePropertyValue 'LiveWall.sln' -Force
$vscodeSettings | Add-Member -NotePropertyName 'dotnetAcquisitionExtension.existingDotnetPath' -NotePropertyValue $extensionDotnetPaths -Force
$vscodeSettings | Add-Member -NotePropertyName 'dotnetAcquisitionExtension.cacheTimeToLiveMultiplier' -NotePropertyValue 0 -Force
$vscodeSettings | Add-Member -NotePropertyName 'dotnet.projectRuntimePath' -NotePropertyValue $projectDotnetExecutable -Force
$vscodeSettings | Add-Member -NotePropertyName 'dotnet.projectSdkPath' -NotePropertyValue $projectDotnetExecutable -Force
$vscodeSettings | Add-Member -NotePropertyName 'dotnet.server.environmentVariables' -NotePropertyValue $languageServerEnvironment -Force
$vscodeSettings | Add-Member -NotePropertyName 'terminal.integrated.env.windows' -NotePropertyValue $terminalEnvironment -Force
$vscodeSettings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $vscodeSettingsPath -Encoding utf8

Write-Host "VS Code workspace settings now use $projectDotnetExecutable"

. (Join-Path $PSScriptRoot 'use-dotnet.ps1')
& $projectDotnetExecutable --info
