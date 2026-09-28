# Builds PDFPlus into dist\app and runs it, for trying changes without installing anything.
# The layout matches what the MSI installs (ordinary files, not a packed single-file exe), so startup and
# behaviour are the same as a real install.
#
#   .\run.ps1                     build and run
#   .\run.ps1 tests\sample.docx   build and run, opening a file
#   .\run.ps1 -NoBuild            just run what was built last time
param(
    [string]$File,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$app = Join-Path $root 'dist\app'
$exe = Join-Path $app 'PDFPlus.exe'

if (-not $NoBuild) {
    dotnet publish (Join-Path $root 'src\PDFPlus\PDFPlus.csproj') -c Release -r win-x64 --self-contained true -o $app -nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
}
if (-not (Test-Path $exe)) { throw "$exe not found; run without -NoBuild first" }

# A second copy would hand its file to the one already running and exit, which is confusing while testing.
$running = Get-Process PDFPlus -ErrorAction SilentlyContinue
if ($running) { "note: PDFPlus is already running (pid $($running.Id -join ', '))" }

if ($File) { Start-Process $exe -ArgumentList (Resolve-Path $File) } else { Start-Process $exe }
"running $exe"
