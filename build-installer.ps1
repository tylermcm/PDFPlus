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

# Installed build: native libraries sit beside the exe and the bundle isn't compressed, so it starts faster than the
# portable exe. The MSI compresses everything anyway.
dotnet publish (Join-Path $root 'src\PDFPlus\PDFPlus.csproj') -c Release -r win-x64 --self-contained true `
    -p:IncludeNativeLibrariesForSelfExtract=false -p:EnableCompressionInSingleFile=false -o $publish -nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

# Package.wxs lists the files explicitly; fail loudly if a dependency update added or removed one.
$expected = 'D3DCompiler_47_cor3.dll', 'pdfium.dll', 'PDFPlus.exe', 'PenImc_cor3.dll', 'PresentationNative_cor3.dll', 'vcruntime140_cor3.dll', 'wpfgfx_cor3.dll'
$actual = Get-ChildItem $publish -File | Where-Object Extension -ne '.pdb' | ForEach-Object Name
$difference = Compare-Object ($expected | Sort-Object) ($actual | Sort-Object)
if ($difference) { throw "Published files changed; update installer\Package.wxs:`n$($difference | Out-String)" }

& (Join-Path $root 'tools\make-installer-art.ps1') -Icon (Join-Path $root 'src\PDFPlus\Assets\PDFPlus.ico') -OutDir $art

dotnet build (Join-Path $root 'installer\PDFPlus.Installer.wixproj') -c Release -nologo `
    "-p:PublishDir=$publish" "-p:ArtDir=$art" "-p:OutputPath=$msiOut"
if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }

$version = [Version](Get-Item (Join-Path $publish 'PDFPlus.exe')).VersionInfo.FileVersion
$target = Join-Path $dist ("PDFPlus-{0}.{1}.{2}-x64.msi" -f $version.Major, $version.Minor, $version.Build)
Copy-Item (Join-Path $msiOut 'PDFPlus.msi') $target -Force
"{0}  {1:N1} MB" -f $target, ((Get-Item $target).Length / 1MB)
