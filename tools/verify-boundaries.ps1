[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$excludedDirectoryNames = @(
    '.git',
    '.vs',
    '.vscode',
    '.idea',
    '.dotnet',
    '.dotnet-cli-home',
    '.nuget',
    '.cache',
    'bin',
    'obj',
    'artifacts',
    'TestResults'
)

function Test-IsExcludedPath {
    param([Parameter(Mandatory)][string] $Path)

    $relativePath = [IO.Path]::GetRelativePath($repositoryRoot, $Path)
    $segments = $relativePath -split '[\\/]'
    return $null -ne ($segments | Where-Object { $_ -in $excludedDirectoryNames } | Select-Object -First 1)
}

Push-Location $repositoryRoot
try {
    $allowedReferences = @{
        'LiveWall.Domain' = @()
        'LiveWall.Contracts' = @()
        'LiveWall.Application' = @('LiveWall.Domain')
        'LiveWall.Platform.Windows' = @('LiveWall.Application', 'LiveWall.Domain')
        'LiveWall.Infrastructure' = @('LiveWall.Application', 'LiveWall.Domain', 'LiveWall.Contracts')
        'LiveWall.Importers' = @('LiveWall.Application', 'LiveWall.Domain')
        'LiveWall.Host' = @(
            'LiveWall.Application',
            'LiveWall.Contracts',
            'LiveWall.Domain',
            'LiveWall.Importers',
            'LiveWall.Infrastructure',
            'LiveWall.Platform.Windows'
        )
        'LiveWall.UI' = @('LiveWall.Contracts')
        'LiveWall.Renderer.Video' = @('LiveWall.Contracts')
        'LiveWall.Renderer.Web' = @('LiveWall.Contracts')
    }

    $errors = [System.Collections.Generic.List[string]]::new()

    $repositoryFiles = Get-ChildItem -File -Recurse |
        Where-Object { -not (Test-IsExcludedPath -Path $_.FullName) }

    Get-ChildItem -Directory -Recurse |
        Where-Object { -not (Test-IsExcludedPath -Path $_.FullName) } |
        Where-Object { -not (Test-Path -LiteralPath (Join-Path $_.FullName 'README.md')) } |
        ForEach-Object { $errors.Add("Directory has no README contract: $($_.FullName)") }

    Get-ChildItem -Path src -Recurse -Filter *.csproj | ForEach-Object {
        $projectName = $_.BaseName
        if (-not $allowedReferences.ContainsKey($projectName)) {
            $errors.Add("Project is absent from boundary map: $projectName")
            return
        }

        [xml]$projectXml = Get-Content -LiteralPath $_.FullName -Raw
        $references = @(
            $projectXml.Project.ItemGroup.ProjectReference |
                ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Include) }
        ) | Where-Object { $_ }

        foreach ($reference in $references) {
            if ($reference -notin $allowedReferences[$projectName]) {
                $errors.Add("Forbidden ProjectReference: $projectName -> $reference")
            }
        }
    }

    $repositoryFiles | Where-Object Extension -eq '.json' | ForEach-Object {
        try {
            Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json | Out-Null
        }
        catch {
            $errors.Add("Invalid JSON: $($_.FullName)")
        }
    }

    $repositoryFiles | Where-Object { $_.Extension -in @('.csproj', '.props') } | ForEach-Object {
        try {
            [xml](Get-Content -LiteralPath $_.FullName -Raw) | Out-Null
        }
        catch {
            $errors.Add("Invalid MSBuild XML: $($_.FullName)")
        }
    }

    $solutionProjectPaths = Select-String -Path LiveWall.sln -Pattern '"(src\\[^\"]+\.csproj)"' -AllMatches |
        ForEach-Object { $_.Matches.Groups[1].Value }
    foreach ($projectPath in $solutionProjectPaths) {
        if (-not (Test-Path -LiteralPath $projectPath)) {
            $errors.Add("Solution references a missing project: $projectPath")
        }
    }

    $nativeApiLeaks = Get-ChildItem -Path src -Recurse -Filter *.cs |
        Where-Object { $_.FullName -notlike '*\LiveWall.Platform.Windows\NativeMethods\*' } |
        Select-String -Pattern '\b(DllImport|LibraryImport)\b'
    foreach ($leak in $nativeApiLeaks) {
        $errors.Add("Native API declared outside NativeMethods: $($leak.Path):$($leak.LineNumber)")
    }

    $rendererFormatLeaks = Get-ChildItem -Path src -Recurse -Filter *.cs |
        Where-Object { $_.FullName -match '\\LiveWall\.Renderer\.(Video|Web)\\' } |
        Select-String -Pattern 'WallpaperEngine|\.mpkg|\.pkg'
    foreach ($leak in $rendererFormatLeaks) {
        $errors.Add("External package knowledge leaked into Renderer: $($leak.Path):$($leak.LineNumber)")
    }

    if ($errors.Count -gt 0) {
        $errors | ForEach-Object { Write-Error $_ }
        exit 1
    }

    Write-Host "Architecture boundary checks passed for $($allowedReferences.Count) projects."
}
finally {
    Pop-Location
}
