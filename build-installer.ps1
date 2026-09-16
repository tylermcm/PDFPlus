# Builds dist\PDFPlus-<version>-x64.msi, a standard Windows installer:
# Program Files install, Start menu shortcut, Add/Remove Programs, and "Open with" / Default apps registration for PDFs.
# Needs the .NET 8 SDK; WiX Toolset 5 is restored from NuGet automatically.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$work = Join-Path $root 'artifacts\installer'
$publish = Join-Path $work 'publish\'
$art = Join-Path $work 'art\'
$msiOut = Join-Path $work 'msi\'
$dist = Join-Path $root 'dist'

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force $publish, $art, $msiOut, $dist | Out-Null

# Ordinary files, not a packed single-file exe; see the note in PDFPlus.csproj for why that matters to startup.
dotnet publish (Join-Path $root 'src\PDFPlus\PDFPlus.csproj') -c Release -r win-x64 --self-contained true `
    -o $publish -nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

# Package.wxs picks the files up as a set, so only the things it names by hand need checking here.
$files = Get-ChildItem $publish -File | Where-Object Extension -ne '.pdb'
foreach ($required in 'PDFPlus.exe', 'pdfium.dll', 'wpfgfx_cor3.dll') {
    if ($files.Name -notcontains $required) { throw "$required is missing from the publish output" }
}
if ($files.Count -lt 100) { throw "Only $($files.Count) files published; expected the whole self-contained runtime" }
if (Get-ChildItem $publish -Directory) { throw "The publish output has subfolders; installer\Package.wxs installs a flat folder" }
"published $($files.Count) files"


& (Join-Path $root 'tools\make-installer-art.ps1') -Icon (Join-Path $root 'src\PDFPlus\Assets\PDFPlus.ico') -OutDir $art

dotnet build (Join-Path $root 'installer\PDFPlus.Installer.wixproj') -c Release -nologo `
    "-p:PublishDir=$publish" "-p:ArtDir=$art" "-p:OutputPath=$msiOut"
if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }

$version = [Version](Get-Item (Join-Path $publish 'PDFPlus.exe')).VersionInfo.FileVersion
$target = Join-Path $dist ("PDFPlus-{0}.{1}.{2}-x64.msi" -f $version.Major, $version.Minor, $version.Build)
Copy-Item (Join-Path $msiOut 'PDFPlus.msi') $target -Force
"{0}  {1:N1} MB" -f $target, ((Get-Item $target).Length / 1MB)
