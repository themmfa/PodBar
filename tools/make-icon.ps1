# Draws the PodBar icon and writes app.ico (multi-size) plus a 512px PNG preview.
# Usage: powershell -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing

$projectDir = Split-Path $PSScriptRoot -Parent

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = [single]$size

    # Background: rounded square with a blue-to-indigo diagonal gradient.
    $inset = $s * 0.03
    $bg = New-RoundedPath $inset $inset ($s - 2 * $inset) ($s - 2 * $inset) ($s * 0.225)
    $rect = New-Object System.Drawing.RectangleF 0, 0, $s, $s
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect,
        ([System.Drawing.Color]::FromArgb(66, 153, 255)), ([System.Drawing.Color]::FromArgb(88, 70, 214)), 55.0
    $g.FillPath($grad, $bg)

    # Soft highlight across the top half for depth.
    $g.SetClip($bg)
    $shine = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect,
        ([System.Drawing.Color]::FromArgb(60, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(0, 255, 255, 255)), 90.0
    $g.FillEllipse($shine, -$s * 0.2, -$s * 0.55, $s * 1.4, $s * 1.0)
    $g.ResetClip()

    # Two earbuds: a round head with a stem, mirrored.
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $headR = $s * 0.135
    $stemW = $s * 0.095
    $top = $s * 0.235
    $stemBottom = $s * 0.775
    foreach ($side in -1, 1) {
        $cx = $s * 0.5 + $side * $s * 0.165          # head centre
        $cy = $top + $headR
        $g.FillEllipse($white, $cx - $headR, $cy - $headR, $headR * 2, $headR * 2)
        # The stem hangs from the inner edge of the head (towards the middle of the icon).
        $stemX = if ($side -lt 0) { $cx + $headR - $stemW } else { $cx - $headR }
        $stem = New-RoundedPath $stemX ($cy - $headR * 0.2) $stemW ($stemBottom - $cy + $headR * 0.2) ($stemW / 2)
        $g.FillPath($white, $stem)
        $stem.Dispose()
    }

    $g.Dispose()
    return $bmp
}

function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray()
}

# Preview
$preview = New-IconBitmap 512
$preview.Save((Join-Path $projectDir 'docs\icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$small = New-IconBitmap 32
$small.Save((Join-Path $projectDir 'docs\icon-32.png'), [System.Drawing.Imaging.ImageFormat]::Png)

# ICO file: header, one directory entry per size, then the PNG images.
$sizes = 16, 20, 24, 32, 48, 64, 256
$images = foreach ($size in $sizes) { , (Get-PngBytes (New-IconBitmap $size)) }

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $projectDir 'app.ico'), $out.ToArray())
"Wrote app.ico ($($out.Length) bytes, sizes: $($sizes -join ', '))"
