# Regenerates src\VolumeControl.ico (a speaker glyph) as a multi-size PNG-based icon.
param([string]$Out = (Join-Path $PSScriptRoot '..\src\VolumeControl.ico'))
Add-Type -AssemblyName System.Drawing

function New-Png([int]$s) {
    $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $bg = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 37, 99, 235))
    $g.FillEllipse($bg, 0, 0, $s - 1, $s - 1)
    $u = $s / 32.0
    $pts = @(
        (New-Object Drawing.PointF (7 * $u), (13 * $u)),
        (New-Object Drawing.PointF (11 * $u), (13 * $u)),
        (New-Object Drawing.PointF (16 * $u), (8 * $u)),
        (New-Object Drawing.PointF (16 * $u), (24 * $u)),
        (New-Object Drawing.PointF (11 * $u), (19 * $u)),
        (New-Object Drawing.PointF (7 * $u), (19 * $u)))
    $g.FillPolygon([Drawing.Brushes]::White, [Drawing.PointF[]]$pts)
    $pen = New-Object Drawing.Pen ([Drawing.Color]::White), ([Math]::Max(1.0, 2 * $u))
    $g.DrawArc($pen, 14 * $u, 11 * $u, 10 * $u, 10 * $u, -55, 110)
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 256
$pngs = @(foreach ($s in $sizes) { ,(New-Png $s) })
$fs = [IO.File]::Create([IO.Path]::GetFullPath($Out))
$w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write([byte[]]$p) }
$w.Close()
