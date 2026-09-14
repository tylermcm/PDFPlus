# Builds the portable PDFPlus.exe into .\dist
# A single self-contained file: no installer, no .NET runtime required, runs from anywhere (USB stick included).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

dotnet publish (Join-Path $root 'src\PDFPlus\PDFPlus.csproj') -c Release -r win-x64 --self-contained true -o $dist -nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

Get-ChildItem $dist | ForEach-Object { "{0,-32} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
