<#
.SYNOPSIS
    Regenerates assets/loupe.ico (16-256 px) and assets/loupe.png from code,
    so the icon has no binary source.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256
$output = Join-Path $PSScriptRoot '..\assets\loupe.ico'
$png = Join-Path $PSScriptRoot '..\assets\loupe.png' # 256 px copy for the README

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $d = $r * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $path
}

function New-IconImage([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $s = $size / 256.0

    # Blue rounded-square tile with a vertical gradient.
    $tile = New-RoundedRect (8 * $s) (8 * $s) (240 * $s) (240 * $s) (56 * $s)
    $top = [System.Drawing.Color]::FromArgb(255, 90, 170, 255)
    $bottom = [System.Drawing.Color]::FromArgb(255, 10, 96, 224)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, $size), $top, $bottom
    $g.FillPath($brush, $tile)

    # White magnifying glass: lens ring plus handle.
    $white = [System.Drawing.Color]::White
    $handle = New-Object System.Drawing.Pen $white, ([Math]::Max(2.0, 30 * $s))
    $handle.StartCap = 'Round'; $handle.EndCap = 'Round'
    $g.DrawLine($handle, 150 * $s, 150 * $s, 196 * $s, 196 * $s)

    $ring = New-Object System.Drawing.Pen $white, ([Math]::Max(1.5, 20 * $s))
    $g.DrawEllipse($ring, 58 * $s, 58 * $s, 104 * $s, 104 * $s)

    # Soft glass highlight inside the lens (skipped at tiny sizes where it would just be noise).
    if ($size -ge 32) {
        $glass = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, 255, 255, 255))
        $g.FillEllipse($glass, 68 * $s, 68 * $s, 84 * $s, 84 * $s)
    }

    $g.Dispose()
    $bitmap
}

# Classic 32-bit DIB icon entry: BITMAPINFOHEADER (double height), bottom-up BGRA rows, empty AND mask.
function ConvertTo-DibEntry([System.Drawing.Bitmap]$bitmap) {
    $w = $bitmap.Width; $h = $bitmap.Height
    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $data = $bitmap.LockBits($rect, 'ReadOnly', [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $pixels = New-Object byte[] ($w * $h * 4)
    for ($y = 0; $y -lt $h; $y++) {
        $row = [IntPtr]::Add($data.Scan0, $y * $data.Stride)
        [System.Runtime.InteropServices.Marshal]::Copy($row, $pixels, ($h - 1 - $y) * $w * 4, $w * 4)
    }
    $bitmap.UnlockBits($data)

    $maskStride = [int][Math]::Ceiling($w / 32.0) * 4
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $stream
    $writer.Write([int]40); $writer.Write([int]$w); $writer.Write([int]($h * 2))
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([int]0); $writer.Write([int]($pixels.Length + $maskStride * $h))
    $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0)
    $writer.Write($pixels)
    $writer.Write((New-Object byte[] ($maskStride * $h)))
    $writer.Flush()
    return , $stream.ToArray() # the comma stops PowerShell from unrolling the byte array
}

# 256 px is stored as PNG (standard since Vista); smaller sizes as DIBs, which every reader
# understands, including System.Drawing.Icon on .NET Framework.
$images = foreach ($size in $sizes) {
    $bitmap = New-IconImage $size
    if ($size -ge 256) {
        $stream = New-Object System.IO.MemoryStream
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $stream.ToArray()
        [System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath($png), $bytes)
    } else {
        $bytes = ConvertTo-DibEntry $bitmap
    }
    $bitmap.Dispose()
    , $bytes
}

# ICO container.
$file = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $file
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($bytes in $images) { $writer.Write($bytes) }
$writer.Flush()

[System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath($output), $file.ToArray())
Write-Host "Wrote $output"
