# Draws the installer wizard images from the app icon (no binary art checked in).
#   dialog.bmp  493x312  Welcome and finish pages; Windows Installer draws the text right of the left panel
#   banner.bmp  493x58   Top strip of the other pages; the page title is drawn on the left
# PNG copies are written too, for previewing.
param(
    [Parameter(Mandatory)] [string]$Icon,
    [Parameter(Mandatory)] [string]$OutDir
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

# System.Drawing can't read the PNG-compressed 256 px icon frame, so decode the largest frame with WPF.
Add-Type -AssemblyName PresentationCore
$decoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create((New-Object Uri((Resolve-Path $Icon).Path)), 'None', 'OnLoad')
$frame = $decoder.Frames | Sort-Object PixelWidth -Descending | Select-Object -First 1
$encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($frame))
$logoStream = New-Object System.IO.MemoryStream
$encoder.Save($logoStream)
$logo = New-Object System.Drawing.Bitmap($logoStream)
$text = [System.Drawing.Color]::FromArgb(28, 29, 34)
$dim = [System.Drawing.Color]::FromArgb(107, 111, 122)
$soft = [System.Drawing.Color]::FromArgb(252, 228, 231)
$softer = [System.Drawing.Color]::FromArgb(255, 245, 246)
$accent = [System.Drawing.Color]::FromArgb(224, 52, 75)

function New-Canvas([int]$width, [int]$height) {
    $bitmap = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = 'AntiAlias'
    $graphics.InterpolationMode = 'HighQualityBicubic'
    $graphics.TextRenderingHint = 'ClearTypeGridFit'
    $graphics.Clear([System.Drawing.Color]::White)
    return $bitmap, $graphics
}

function Save-Art($bitmap, [string]$name) {
    $bitmap.Save((Join-Path $OutDir "$name.bmp"), [System.Drawing.Imaging.ImageFormat]::Bmp)
    $bitmap.Save((Join-Path $OutDir "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

$center = New-Object System.Drawing.StringFormat
$center.Alignment = 'Center'

# Welcome / finish: soft panel on the left with the logo and name.
$bitmap, $g = New-Canvas 493 312
$panel = New-Object System.Drawing.Rectangle(0, 0, 164, 312)
$gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush($panel, $softer, $soft, 90)
$g.FillRectangle($gradient, $panel)
$g.FillRectangle((New-Object System.Drawing.SolidBrush($accent)), 164, 0, 2, 312)
$g.DrawImage($logo, 42, 84, 80, 80)
$title = New-Object System.Drawing.Font('Segoe UI Semibold', 20, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$small = New-Object System.Drawing.Font('Segoe UI', 12, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString('PDFPlus', $title, (New-Object System.Drawing.SolidBrush($text)), (New-Object System.Drawing.RectangleF(0, 176, 164, 30)), $center)
$g.DrawString("Free forever`nNo subscription", $small, (New-Object System.Drawing.SolidBrush($dim)), (New-Object System.Drawing.RectangleF(0, 208, 164, 40)), $center)
$g.Dispose()
Save-Art $bitmap 'dialog'

# Banner: white with the logo on the right.
$bitmap, $g = New-Canvas 493 58
$g.DrawImage($logo, 431, 9, 40, 40)
$g.Dispose()
Save-Art $bitmap 'banner'
