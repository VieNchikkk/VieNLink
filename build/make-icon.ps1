#Requires -Version 5.1
# Рисует иконку VieNLink (V + кольцо-связка) и собирает многоразмерный .ico
# без внешних инструментов. Палитра совпадает с тёмной темой приложения.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'VieNLink\Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null

$size = 1024
$bmp = New-Object System.Drawing.Bitmap($size, $size)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.Clear([System.Drawing.Color]::Transparent)

$graphite = [System.Drawing.ColorTranslator]::FromHtml('#18191B')
$borderColor = [System.Drawing.ColorTranslator]::FromHtml('#2C3035')
$mint = [System.Drawing.ColorTranslator]::FromHtml('#9CCDB0')

function New-RoundPen($color, $width) {
    $pen = New-Object System.Drawing.Pen($color, [float]$width)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    return $pen
}

try {
    # Тёмный фон со скруглёнными углами и тонкой рамкой.
    $r = 220
    $round = New-Object System.Drawing.Drawing2D.GraphicsPath
    $round.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $round.AddArc($size - $r * 2, 0, $r * 2, $r * 2, 270, 90)
    $round.AddArc($size - $r * 2, $size - $r * 2, $r * 2, $r * 2, 0, 90)
    $round.AddArc(0, $size - $r * 2, $r * 2, $r * 2, 90, 90)
    $round.CloseFigure()

    $bg = New-Object System.Drawing.SolidBrush($graphite)
    $g.FillPath($bg, $round)
    $frame = New-Object System.Drawing.Pen($borderColor, 10)
    $g.DrawPath($frame, $round)

    # Буква V.
    $vPen = New-RoundPen $mint 142
    $g.DrawLine($vPen, 300, 290, 512, 650)
    $g.DrawLine($vPen, 512, 650, 724, 290)

    # Связка: короткий поводок и кольцо под вершиной V.
    $linkPen = New-RoundPen $mint 88
    $g.DrawLine($linkPen, 512, 650, 512, 725)

    $hole = New-Object System.Drawing.SolidBrush($graphite)
    $g.FillEllipse($hole, 512 - 104, 800 - 104, 208, 208)
    $ringPen = New-RoundPen $mint 52
    $g.DrawEllipse($ringPen, 512 - 104, 800 - 104, 208, 208)

    $bg.Dispose(); $frame.Dispose(); $vPen.Dispose(); $linkPen.Dispose()
    $hole.Dispose(); $ringPen.Dispose(); $round.Dispose()
}
finally {
    $g.Dispose()
}

$pngPath = Join-Path $assets 'VieNLink.png'
$bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)

# Собираем .ico: DIB-кадры 16…256. PNG-кадры внутри ICO GDI+ читает как «шум»,
# поэтому пишем классический формат: BITMAPINFOHEADER + BGRA + AND-маска.
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = New-Object System.Collections.Generic.List[byte[]]
foreach ($s in $sizes) {
    $small = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $sg = [System.Drawing.Graphics]::FromImage($small)
    $sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $sg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $sg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $sg.Clear([System.Drawing.Color]::Transparent)
    $sg.DrawImage($bmp, 0, 0, $s, $s)
    $sg.Dispose()

    # Читаем BGRA-пиксели и переворачиваем строки: в DIB строки идут снизу вверх.
    $rect = New-Object System.Drawing.Rectangle(0, 0, $s, $s)
    $locked = $small.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = [Math]::Abs($locked.Stride)
    $pixels = New-Object byte[] ($stride * $s)
    [System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $pixels, 0, $pixels.Length)
    $small.UnlockBits($locked)
    $small.Dispose()

    $xor = New-Object byte[] ($s * $s * 4)
    for ($y = 0; $y -lt $s; $y++) {
        [Array]::Copy($pixels, $y * $stride, $xor, ($s - 1 - $y) * $s * 4, $s * 4)
    }

    # AND-маска: для 32bpp она не используется (прозрачность в альфе), но обязательна.
    $maskStride = [int]([Math]::Floor(($s + 31) / 32) * 4)
    $and = New-Object byte[] ($maskStride * $s)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([UInt32]40)          # biSize
    $bw.Write([Int32]$s)           # biWidth
    $bw.Write([Int32]($s * 2))     # biHeight (XOR + AND)
    $bw.Write([UInt16]1)           # biPlanes
    $bw.Write([UInt16]32)          # biBitCount
    $bw.Write([UInt32]0)           # biCompression = BI_RGB
    $bw.Write([UInt32]$xor.Length) # biSizeImage
    $bw.Write([Int32]0); $bw.Write([Int32]0)
    $bw.Write([UInt32]0); $bw.Write([UInt32]0)
    $bw.Write($xor)
    $bw.Write($and)
    $bw.Flush()
    $frames.Add($ms.ToArray())
    $ms.Dispose()
}

$icoPath = Join-Path $assets 'VieNLink.ico'
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
try {
    $bw.Write([UInt16]0)          # reserved
    $bw.Write([UInt16]1)          # icon
    $bw.Write([UInt16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]
        $bw.Write([byte]$(if ($s -ge 256) { 0 } else { $s }))
        $bw.Write([byte]$(if ($s -ge 256) { 0 } else { $s }))
        $bw.Write([byte]0)        # colors
        $bw.Write([byte]0)        # reserved
        $bw.Write([UInt16]1)      # planes
        $bw.Write([UInt16]32)     # bpp
        $bw.Write([UInt32]$frames[$i].Length)
        $bw.Write([UInt32]$offset)
        $offset += $frames[$i].Length
    }

    foreach ($frame in $frames) {
        $bw.Write($frame)
    }
}
finally {
    $bw.Dispose()
    $fs.Dispose()
    $bmp.Dispose()
}

$icon = New-Object System.Drawing.Icon($icoPath)
Write-Host "PNG: $pngPath"
Write-Host "ICO: $icoPath (кадры: $($sizes -join ', '); размер значка: $($icon.Width)x$($icon.Height))"
$icon.Dispose()
