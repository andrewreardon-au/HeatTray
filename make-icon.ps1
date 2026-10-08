<#
Regenerates src\HeatTray.ico (a red/orange thermometer-with-flame glyph) at
16/24/32/48/64/256 px using System.Drawing - no external tools. Only needed if
you want to change the artwork; the committed .ico is what build.ps1 embeds.
#>

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $scriptDir "src\HeatTray.ico"

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 64.0

    # Rounded dark badge
    $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 12 * $s
    $bg.AddArc(2*$s, 2*$s, $r*2, $r*2, 180, 90)
    $bg.AddArc((62*$s)-($r*2), 2*$s, $r*2, $r*2, 270, 90)
    $bg.AddArc((62*$s)-($r*2), (62*$s)-($r*2), $r*2, $r*2, 0, 90)
    $bg.AddArc(2*$s, (62*$s)-($r*2), $r*2, $r*2, 90, 90)
    $bg.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 38, 38, 46))), $bg)

    # Thermometer stem + bulb
    $stem = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 235, 235, 240))
    $g.FillRectangle($stem, 26*$s, 10*$s, 12*$s, 34*$s)
    $g.FillEllipse($stem, 20*$s, 38*$s, 24*$s, 24*$s)

    # Mercury (gradient green->orange->red as it rises)
    $rect = New-Object System.Drawing.RectangleF (28*$s), (12*$s), (8*$s), (46*$s)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 255, 60, 30)), ([System.Drawing.Color]::FromArgb(255, 255, 170, 0)), 90
    $g.FillRectangle($grad, 28*$s, 14*$s, 8*$s, 34*$s)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 90, 30))), 23*$s, 41*$s, 18*$s, 18*$s)

    # Little flame to the right
    $f = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pts = @(
        [System.Drawing.PointF]::new(50*$s, 20*$s), [System.Drawing.PointF]::new(56*$s, 30*$s),
        [System.Drawing.PointF]::new(55*$s, 42*$s), [System.Drawing.PointF]::new(50*$s, 48*$s),
        [System.Drawing.PointF]::new(45*$s, 42*$s), [System.Drawing.PointF]::new(46*$s, 33*$s),
        [System.Drawing.PointF]::new(48*$s, 36*$s)
    )
    $f.AddClosedCurve($pts, 0.5)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 120, 20))), $f)

    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 256
$pngs = foreach ($sz in $sizes) {
    $bmp = New-Frame $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    ,$ms.ToArray()
}

# ICO container with PNG-compressed frames (Vista+).
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $pngs[$i].Length
    $wh = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([byte]$wh); $bw.Write([byte]$wh); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close(); $fs.Close()
Write-Host "Wrote $out ($((Get-Item $out).Length) bytes)"
