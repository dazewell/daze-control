# Regenerates assets/app.ico (exe icons, editor window, installer) + app.png (About) from one mark: a flow, one step fanning out to two
# that run together, joined by the canvas's horizontal Béziers (UX §5.3). Colour tile = the OSD's violet→blue
# gradient (family look, OSD scripts/make-icon.ps1), white steps, the second parallel step amber.
# Geometry is on a 16-unit grid, scaled per frame. -PreviewDir writes every frame at 4x on dark and light grounds.
param([string]$OutDir = "$PSScriptRoot/../assets", [string]$PreviewDir)

Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 28, 32, 40, 48, 64, 256
$top = [System.Drawing.Color]::FromArgb(255, 0x8B, 0x5C, 0xF6)    # violet
$bottom = [System.Drawing.Color]::FromArgb(255, 0x25, 0x63, 0xEB) # blue
$warm = [System.Drawing.Color]::FromArgb(255, 0xFC, 0xD3, 0x4D)   # amber
$white = [System.Drawing.Color]::White

function New-RoundRect([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = [Math]::Min(2 * $r, [Math]::Min($w, $h))
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    , $path
}

function New-Bitmap([int]$size) {
    $k = $size / 16
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half

    $tile = New-RoundRect 0 0 $size $size ([Math]::Max(3, 3.5 * $k))
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new([System.Drawing.RectangleF]::new(0, 0, $size, $size), $top, $bottom, 60)
    $g.FillPath($brush, $tile)
    $brush.Dispose(); $tile.Dispose()

    # Steps (centre x, centre y) in grid units: start → two together → done (amber). Edges run centre to centre
    # under the steps, so their ends hide inside the dots.
    $r = 1.7; $start = 3.2, 8; $upper = 8, 4.3; $lower = 8, 11.7; $done = 12.8, 8
    $pen = [System.Drawing.Pen]::new($white, [Math]::Max(1, 1.2 * $k))
    foreach ($e in @(($start, $upper), ($start, $lower), ($upper, $done), ($lower, $done))) {
        $x0 = $e[0][0] * $k; $y0 = $e[0][1] * $k; $x1 = $e[1][0] * $k; $y1 = $e[1][1] * $k
        $mid = ($x0 + $x1) / 2
        $g.DrawBezier($pen, $x0, $y0, $mid, $y0, $mid, $y1, $x1, $y1)
    }
    $pen.Dispose()
    $steps = @(@{ C = $start; Ink = $white }, @{ C = $upper; Ink = $white }, @{ C = $lower; Ink = $white }, @{ C = $done; Ink = $warm })
    foreach ($s in $steps) {
        $fill = [System.Drawing.SolidBrush]::new($s.Ink)
        $g.FillEllipse($fill, ($s.C[0] - $r) * $k, ($s.C[1] - $r) * $k, 2 * $r * $k, 2 * $r * $k)
        $fill.Dispose()
    }
    $g.Dispose()
    $bitmap
}

function New-Png([int]$size) {
    $bitmap = New-Bitmap $size
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    , $stream.ToArray()
}

function Write-Ico([string]$path) {
    $images = foreach ($size in $sizes) { , (New-Png $size) }
    $out = [System.IO.BinaryWriter]::new([System.IO.File]::Create($path))
    $out.Write([uint16]0); $out.Write([uint16]1); $out.Write([uint16]$sizes.Count) # ICONDIR
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] } # 0 means 256
        $out.Write([byte]$dim); $out.Write([byte]$dim); $out.Write([byte]0); $out.Write([byte]0)
        $out.Write([uint16]1); $out.Write([uint16]32); $out.Write([uint32]$images[$i].Length); $out.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $out.Write($image) }
    $out.Dispose()
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
Write-Ico (Join-Path $OutDir 'app.ico')
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'app.png'), (New-Png 256))

if ($PreviewDir) {
    $scale = 4
    $drawn = foreach ($size in $sizes) { if ($size -eq 256) { 256 } else { $size * $scale } }
    [int]$width = ($drawn | Measure-Object -Sum).Sum + 16 * ($sizes.Count + 1)
    [int]$rowH = 256 + 16
    $grounds = [System.Drawing.Color]::FromArgb(32, 32, 32), [System.Drawing.Color]::FromArgb(243, 243, 243)
    $sheet = [System.Drawing.Bitmap]::new($width, [int]($grounds.Count * $rowH))
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    for ($r = 0; $r -lt $grounds.Count; $r++) {
        $ground = [System.Drawing.SolidBrush]::new($grounds[$r])
        $g.FillRectangle($ground, 0, $r * $rowH, $width, $rowH)
        $ground.Dispose()
        $x = 16
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $bitmap = New-Bitmap $sizes[$i]
            $g.DrawImage($bitmap, [System.Drawing.Rectangle]::new($x, $r * $rowH + 8, $drawn[$i], $drawn[$i]))
            $bitmap.Dispose()
            $x += $drawn[$i] + 16
        }
    }
    $g.Dispose()
    New-Item -ItemType Directory -Force $PreviewDir | Out-Null
    $sheet.Save((Join-Path $PreviewDir 'icons.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $sheet.Dispose()
}
