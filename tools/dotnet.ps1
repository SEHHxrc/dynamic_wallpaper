[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $DotnetArguments
)

. (Join-Path $PSScriptRoot 'use-dotnet.ps1')

$projectDotnetExecutable = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
& $projectDotnetExecutable @DotnetArguments
exit $LASTEXITCODE
