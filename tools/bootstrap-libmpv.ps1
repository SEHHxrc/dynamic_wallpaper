[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'libmpv-development.lock.json') -Raw | ConvertFrom-Json
$cacheRoot = Join-Path $repositoryRoot '.cache'
$archive = Join-Path $cacheRoot $lock.archiveFile
$runtimeRoot = Join-Path $cacheRoot ('libmpv/' + $lock.release + '-x64')
New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -Uri $lock.archiveUrl -OutFile $archive -TimeoutSec 180
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $lock.archiveSha256) {
    throw 'Archive SHA-256 mismatch. Existing file was preserved; do not load it.'
}
$library = Join-Path $runtimeRoot $lock.libraryFile
if (-not (Test-Path -LiteralPath $library)) {
    # Expand a fully pinned archive only inside this repository. No installer or PATH changes.
    New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null
    & tar -xf $archive -C $runtimeRoot
    if ($LASTEXITCODE -ne 0) { throw 'libmpv extraction failed.' }
}
if ((Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash -ne $lock.librarySha256) {
    throw 'libmpv DLL SHA-256 mismatch. Existing file was preserved; refusing to use it.'
}
Write-Output "Development-only libmpv ready: $library"
Write-Output 'No native code was executed. Distribution license/dependency review remains pending.'
