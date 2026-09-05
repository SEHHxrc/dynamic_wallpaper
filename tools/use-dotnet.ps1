[CmdletBinding()]
param()

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectDotnetRoot = Join-Path $repositoryRoot '.dotnet'
$projectDotnetExecutable = Join-Path $projectDotnetRoot 'dotnet.exe'
$projectCliHome = Join-Path $repositoryRoot '.dotnet-cli-home'
$projectAppData = Join-Path $projectCliHome 'AppData\Roaming'
$projectLocalAppData = Join-Path $projectCliHome 'AppData\Local'

if (-not (Test-Path -LiteralPath $projectDotnetExecutable -PathType Leaf)) {
    throw "Project SDK is not installed. Run tools/bootstrap-dotnet.ps1 first."
}

New-Item -ItemType Directory -Path $projectAppData -Force | Out-Null
New-Item -ItemType Directory -Path $projectLocalAppData -Force | Out-Null

$env:DOTNET_ROOT = $projectDotnetRoot
$env:DOTNET_CLI_HOME = $projectCliHome
$env:NUGET_PACKAGES = Join-Path $repositoryRoot '.nuget\packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $repositoryRoot '.nuget\http-cache'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $repositoryRoot '.nuget\plugins-cache'
$env:NUGET_SCRATCH = Join-Path $repositoryRoot '.nuget\scratch'
$env:APPDATA = $projectAppData
$env:LOCALAPPDATA = $projectLocalAppData
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$env:PATH = "$projectDotnetRoot;$env:PATH"

Write-Host "LiveWall project .NET environment is active."
Write-Host "SDK root: $projectDotnetRoot"
Write-Host "NuGet packages: $env:NUGET_PACKAGES"
