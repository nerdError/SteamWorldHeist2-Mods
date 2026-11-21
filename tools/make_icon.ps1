<#
.SYNOPSIS
  Draws the mod loader icon (brass cog on a dark steel plate) and writes SWH2.ModLoader\app.ico
  with 16/24/32/48/64/256 px images (PNG-compressed entries, Vista+ format).
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\make_icon.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'SWH2.ModLoader\app.ico'

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function New-Cog([float]$cx, [float]$cy, [float]$rOuter, [float]$rInner, [int]$teeth) {
    # Trapezoid teeth: each tooth spans 50% of its slot at the root and ~32% at the tip.
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pts = New-Object System.Collections.Generic.List[System.Drawing.PointF]
    $slot = 2 * [Math]::PI / $teeth
    for ($i = 0; $i -lt $teeth; $i++) {
        $a = $i * $slot - [Math]::PI / 2
        foreach ($s in @(@(-0.25, $rInner), @(-0.16, $rOuter), @(0.16, $rOuter), @(0.25, $rInner), @(0.5, $rInner))) {
            $ang = $a + $s[0] * $slot
            $pts.Add((New-Object System.Drawing.PointF ([float]($cx + $s[1] * [Math]::Cos($ang))), ([float]($cy + $s[1] * [Math]::Sin($ang)))))
        }
    }
    $p.AddPolygon($pts.ToArray())
    $p
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    $g.ScaleTransform($s, $s)

    # Plate
    $plate = New-RoundedRect 8 8 240 240 44
    $plateBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point 0, 256), ([System.Drawing.Color]::FromArgb(255, 46, 62, 78)), ([System.Drawing.Color]::FromArgb(255, 20, 28, 38))
    $g.FillPath($plateBrush, $plate)
    $g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 12, 16, 22)), 6), $plate)

    # Cog with a hole
    $cog = New-Cog 128 128 104 80 10
    $cog.AddEllipse(128 - 34, 128 - 34, 68, 68)
    $cog.FillMode = 'Alternate'
    $brass = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 40, 30), (New-Object System.Drawing.Point 216, 226), ([System.Drawing.Color]::FromArgb(255, 250, 210, 120)), ([System.Drawing.Color]::FromArgb(255, 176, 110, 30))
    $g.FillPath($brass, $cog)
    $g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 92, 54, 12)), 6), $cog)

    # Inner ring groove (only where it stays visible)
    if ($size -ge 48) {
        $g.DrawEllipse((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(150, 110, 66, 16)), 5), 128 - 58, 128 - 58, 116, 116)
    }

    # Teal "power" dot in the hole: the loader's accent colour
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 70, 210, 200))), 128 - 18, 128 - 18, 36, 36)

    $g.Dispose()
    $bmp
}

$sizes = 16, 24, 32, 48, 64, 256
$pngs = foreach ($sz in $sizes) {
    $bmp = New-IconBitmap $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $w.Write($png) }
$w.Dispose()
Write-Host "Wrote $out"

# Optional 256 px PNG preview: set ICON_PREVIEW to a file path.
if ($env:ICON_PREVIEW) { $b = New-IconBitmap 256; $b.Save($env:ICON_PREVIEW); $b.Dispose() }
