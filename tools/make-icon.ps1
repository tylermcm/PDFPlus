# Generates src/PDFPlus/Assets/PDFPlus.ico (multi-size, PNG-compressed entries).
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\src\PDFPlus\Assets\PDFPlus.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function P([double]$x, [double]$y) { [System.Drawing.PointF]::new([float]$x, [float]$y) }

function RoundedRect([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [float]($r * 2)
    $p.AddArc([float]$x, [float]$y, $d, $d, 180, 90)
    $p.AddArc([float]($x + $w - $d), [float]$y, $d, $d, 270, 90)
    $p.AddArc([float]($x + $w - $d), [float]($y + $h - $d), $d, $d, 0, 90)
    $p.AddArc([float]$x, [float]($y + $h - $d), $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

$images = @()
foreach ($s in $sizes) {
    $k = $s / 256.0
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded tile with a warm red gradient.
    $tile = RoundedRect (6 * $k) (6 * $k) (244 * $k) (244 * $k) (54 * $k)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (P 0 0), (P $s $s), ([System.Drawing.Color]::FromArgb(255, 250, 96, 84)), ([System.Drawing.Color]::FromArgb(255, 200, 32, 64))
    $g.FillPath($grad, $tile)

    # White page with a folded corner.
    $px = 62 * $k; $py = 40 * $k; $pw = 122 * $k; $ph = 164 * $k; $fold = 38 * $k
    $page = [System.Drawing.PointF[]]@((P $px $py), (P ($px + $pw - $fold) $py), (P ($px + $pw) ($py + $fold)), (P ($px + $pw) ($py + $ph)), (P $px ($py + $ph)))
    $g.FillPolygon([System.Drawing.Brushes]::White, $page)
    $corner = [System.Drawing.PointF[]]@((P ($px + $pw - $fold) $py), (P ($px + $pw - $fold) ($py + $fold)), (P ($px + $pw) ($py + $fold)))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 200, 196))), $corner)

    if ($s -ge 32) {
        $line = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 238, 150, 150))
        foreach ($i in 0..2) {
            $ly = $py + (70 + $i * 26) * $k
            $lw = if ($i -eq 2) { 52 * $k } else { 82 * $k }
            $g.FillRectangle($line, [float]($px + 20 * $k), [float]$ly, [float]$lw, [float](12 * $k))
        }
    }

    # Plus badge.
    $cx = 182 * $k; $cy = 182 * $k; $r = 50 * $k
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 30, 32, 44))), [float]($cx - $r), [float]($cy - $r), [float]($r * 2), [float]($r * 2))
    $bar = [Math]::Max(1.5, 14 * $k); $len = 54 * $k
    $g.FillRectangle([System.Drawing.Brushes]::White, [float]($cx - $len / 2), [float]($cy - $bar / 2), [float]$len, [float]$bar)
    $g.FillRectangle([System.Drawing.Brushes]::White, [float]($cx - $bar / 2), [float]($cy - $len / 2), [float]$bar, [float]$len)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $images += , @($s, $ms.ToArray())
}

$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $dim = if ($img[0] -ge 256) { 0 } else { $img[0] }
    $w.Write([Byte]$dim); $w.Write([Byte]$dim); $w.Write([Byte]0); $w.Write([Byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$img[1].Length); $w.Write([UInt32]$offset)
    $offset += $img[1].Length
}
foreach ($img in $images) { $w.Write([byte[]]$img[1]) }
$w.Close()
Write-Output "Wrote $out"
