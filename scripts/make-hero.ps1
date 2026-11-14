# Regenerates assets/hero.png (README banner, 1280×640, also fits GitHub's social preview): three keycaps "Ctrl",
# "Alt" and the orange app keycap with the flow mark (make-icon.ps1, D-153), the name and one line on what it does.
# Windows PowerShell (System.Drawing); Segoe UI Variable ships with Windows 11.
param([string]$Out = "$PSScriptRoot/../assets/hero.png")

Add-Type -AssemblyName System.Drawing
$W = 1280; $H = 640
function C([int]$rgb) { [System.Drawing.Color]::FromArgb(255, ($rgb -shr 16) -band 255, ($rgb -shr 8) -band 255, $rgb -band 255) }
$ground = C 0x16191D; $capTop = C 0x2E343B; $capSide = C 0x1F2328; $capEdge = C 0x3B424A
$orange = C 0xFF8A3D; $deep = C 0xF25C24; $lip = C 0xB83A0E; $charcoal = C 0x24292F
$white = [System.Drawing.Color]::White; $muted = C 0xA7B0BA

function New-RoundRect([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = 2 * $r
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    , $path
}

# A keycap: side (full height), then the top face shorter by the lip; returns the face's rectangle.
function Draw-Cap($g, [double]$x, [double]$y, [double]$w, [double]$h, $face1, $face2, $side, $edge) {
    $lipH = 14; $r = 22
    $shadow = New-RoundRect ($x + 4) ($y + 14) $w $h $r
    $sb = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(90, 0, 0, 0))
    $g.FillPath($sb, $shadow); $sb.Dispose(); $shadow.Dispose()
    $body = New-RoundRect $x $y $w $h $r
    $b = [System.Drawing.SolidBrush]::new($side); $g.FillPath($b, $body); $b.Dispose(); $body.Dispose()
    $face = New-RoundRect $x $y $w ($h - $lipH) $r
    $fb = [System.Drawing.Drawing2D.LinearGradientBrush]::new([System.Drawing.RectangleF]::new($x, $y, $w, $h), $face1, $face2, 70)
    $g.FillPath($fb, $face); $fb.Dispose()
    if ($edge) { $p = [System.Drawing.Pen]::new($edge, 2); $g.DrawPath($p, $face); $p.Dispose() }
    $face.Dispose()
    [System.Drawing.RectangleF]::new($x, $y, $w, $h - $lipH)
}

function Draw-Label($g, [string]$text, $rect, [double]$size, $color) {
    $font = [System.Drawing.Font]::new('Segoe UI Variable Display', [single]$size, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $format = [System.Drawing.StringFormat]::new(); $format.Alignment = 'Center'; $format.LineAlignment = 'Center'
    $brush = [System.Drawing.SolidBrush]::new($color)
    $g.DrawString($text, $font, $brush, $rect, $format)
    $brush.Dispose(); $font.Dispose(); $format.Dispose()
}

$bitmap = [System.Drawing.Bitmap]::new($W, $H)
$g = [System.Drawing.Graphics]::FromImage($bitmap)
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.PixelOffsetMode = 'Half'
$g.Clear($ground)
# A faint warm glow behind the app key.
$glow = [System.Drawing.Drawing2D.GraphicsPath]::new(); $glow.AddEllipse(700, 20, 560, 420)
$gb = [System.Drawing.Drawing2D.PathGradientBrush]::new($glow)
$gb.CenterColor = [System.Drawing.Color]::FromArgb(70, 255, 120, 50); $gb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 255, 120, 50))
$g.FillPath($gb, $glow); $gb.Dispose(); $glow.Dispose()

$y = 120; $h = 200
$ctrl = Draw-Cap $g 150 $y 300 $h $capTop $capTop $capSide $capEdge
Draw-Label $g 'Ctrl' $ctrl 64 $white
$alt = Draw-Cap $g 490 $y 240 $h $capTop $capTop $capSide $capEdge
Draw-Label $g 'Alt' $alt 64 $white
$app = Draw-Cap $g 770 $y 360 $h $orange $deep $lip $null

# The flow mark on the app key (make-icon.ps1's 16 grid, fitted to the face's height).
$k = $app.Height / 16; $ox = $app.X + ($app.Width - 16 * $k) / 2; $oy = $app.Y
$start = 3.2, 8; $upper = 8, 4.3; $lower = 8, 11.7; $done = 12.8, 8; $r = 1.7
$pen = [System.Drawing.Pen]::new($white, 1.2 * $k)
foreach ($e in @(($start, $upper), ($start, $lower), ($upper, $done), ($lower, $done))) {
    $x0 = $ox + $e[0][0] * $k; $y0 = $oy + $e[0][1] * $k; $x1 = $ox + $e[1][0] * $k; $y1 = $oy + $e[1][1] * $k
    $mid = ($x0 + $x1) / 2
    $g.DrawBezier($pen, $x0, $y0, $mid, $y0, $mid, $y1, $x1, $y1)
}
$pen.Dispose()
foreach ($s in @(@($start, $white), @($upper, $white), @($lower, $white), @($done, $charcoal))) {
    $b = [System.Drawing.SolidBrush]::new($s[1])
    $g.FillEllipse($b, $ox + ($s[0][0] - $r) * $k, $oy + ($s[0][1] - $r) * $k, 2 * $r * $k, 2 * $r * $k)
    $b.Dispose()
}

$title = [System.Drawing.RectangleF]::new(0, 380, $W, 110)
Draw-Label $g 'Ctrl Alt Daze' $title 92 $white
$font = [System.Drawing.Font]::new('Segoe UI Variable Text', [single]30, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$format = [System.Drawing.StringFormat]::new(); $format.Alignment = 'Center'
$brush = [System.Drawing.SolidBrush]::new($muted)
$g.DrawString('Your PC routines on one key: Bluetooth, audio, displays, Android and apps.', $font, $brush, [System.Drawing.RectangleF]::new(0, 500, $W, 60), $format)
$brush.Dispose(); $font.Dispose(); $format.Dispose()

$g.Dispose()
$bitmap.Save((Resolve-Path (Split-Path $Out)).Path + '\' + (Split-Path $Out -Leaf), [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
