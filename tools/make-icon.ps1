param([Parameter(Mandatory = $true)][string]$Path)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    $d = 72
    $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bg.AddArc(8, 8, $d, $d, 180, 90)
    $bg.AddArc(248 - $d, 8, $d, $d, 270, 90)
    $bg.AddArc(248 - $d, 248 - $d, $d, $d, 0, 90)
    $bg.AddArc(8, 248 - $d, $d, $d, 90, 90)
    $bg.CloseFigure()
    $blue = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 37, 99, 235))
    $g.FillPath($blue, $bg)

    # an RJ45-style socket silhouette with four contacts
    $socket = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(52, 64), [System.Drawing.PointF]::new(204, 64),
        [System.Drawing.PointF]::new(204, 172), [System.Drawing.PointF]::new(172, 172),
        [System.Drawing.PointF]::new(172, 200), [System.Drawing.PointF]::new(84, 200),
        [System.Drawing.PointF]::new(84, 172), [System.Drawing.PointF]::new(52, 172)
    )
    $g.FillPolygon([System.Drawing.Brushes]::White, $socket)
    foreach ($x in 82, 108, 134, 160) { $g.FillRectangle($blue, $x, 82, 14, 38) }

    $g.Dispose()
    $blue.Dispose()
    return $bmp
}

function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width
    $h = $bmp.Height
    $data = $bmp.LockBits((New-Object System.Drawing.Rectangle(0, 0, $w, $h)), [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $pixels = New-Object byte[] ($data.Stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $stride = $data.Stride
    $bmp.UnlockBits($data)

    $maskStride = [int]([Math]::Ceiling($w / 32.0) * 4)
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2))
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
    $bw.Write([int]($w * $h * 4 + $maskStride * $h))
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($pixels, $y * $stride, $w * 4) }
    $bw.Write((New-Object byte[] ($maskStride * $h)))
    $bw.Flush()
    return , $ms.ToArray()
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 256)
$blobs = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    if ($s -eq 256) {
        $png = New-Object System.IO.MemoryStream
        $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
        $blobs.Add($png.ToArray())
    }
    else { $blobs.Add((Get-DibBytes $bmp)) }
    $bmp.Dispose()
}

$dir = Split-Path -Parent $Path
if ($dir) { New-Item -ItemType Directory -Force $dir | Out-Null }
$fs = [System.IO.File]::Create($Path)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$blobs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $blobs[$i].Length
}
foreach ($b in $blobs) { $bw.Write($b) }
$bw.Close()
Write-Host "Wrote $Path"
