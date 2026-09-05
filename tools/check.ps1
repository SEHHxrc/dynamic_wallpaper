[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repositoryRoot
try {
    & (Join-Path $PSScriptRoot 'verify-boundaries.ps1')
    if (-not $?) {
        exit 1
    }

    & (Join-Path $PSScriptRoot 'dotnet.ps1') restore LiveWall.sln --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    & (Join-Path $PSScriptRoot 'dotnet.ps1') build LiveWall.sln `
        --configuration $Configuration `
        --no-restore
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    & (Join-Path $PSScriptRoot 'dotnet.ps1') test LiveWall.sln `
        --configuration $Configuration `
        --no-build `
        --no-restore
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
