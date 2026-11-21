# Генерация иконки VieNLink: монограмма «VL» одной лентой на тёмно-изумрудной
# плашке. Выход: Assets/VieNLink.ico (16–256), Assets/VieNLink.png (512),
# branding/vienlink-icon-src.png (1024) и branding/vienlink-wordmark.png.
# Запуск: powershell -File build\make-icon.ps1
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

Add-Type -AssemblyName System.Drawing

# Палитра: тёмный графит + изумрудный градиент, как в тёмной теме приложения.
$BgTop     = [System.Drawing.Color]::FromArgb(255, 0x14, 0x24, 0x1C)
$BgBottom  = [System.Drawing.Color]::FromArgb(255, 0x06, 0x0D, 0x0A)
$Glow      = [System.Drawing.Color]::FromArgb(255, 0x2E, 0x7D, 0x52)
$Border    = [System.Drawing.Color]::FromArgb(150, 0x3C, 0x5C, 0x4A)
$InkTop    = [System.Drawing.Color]::FromArgb(255, 0xF6, 0xFF, 0xFA)
$InkBottom = [System.Drawing.Color]::FromArgb(255, 0x7F, 0xE0, 0xAB)
$Shadow    = [System.Drawing.Color]::FromArgb(255, 0x02, 0x0A, 0x06)

function New-RoundedRectPath([System.Drawing.RectangleF]$rect, [float]$radius) {
    $d = $radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap([int]$size) {
    # Рисуем в 4× (для крупных — в 2×) и уменьшаем: так сглаживаются края.
    $ss = if ($size -ge 512) { 2 } else { 4 }
    $S = [float]($size * $ss)
    $bmp = New-Object System.Drawing.Bitmap([int]$S, [int]$S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    # --- Плашка: скруглённый квадрат с градиентом ---
    $rect = New-Object System.Drawing.RectangleF(0, 0, $S, $S)
    $radius = [float]($S * 0.225)
    $tile = New-RoundedRectPath $rect $radius
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $BgTop, $BgBottom, 55.0)
    $g.FillPath($bg, $tile)
    $bg.Dispose()

    # Фон уже содержит градиент; дополнительный отсвет даёт заметные полосы
    # на 8-битном градиенте, поэтому обходимся без него.

    # --- Тонкая внутренняя рамка ---
    $inset = [float]($S * 0.045)
    $frameRect = New-Object System.Drawing.RectangleF($inset, $inset, ($S - $inset * 2), ($S - $inset * 2))
    $frame = New-RoundedRectPath $frameRect ([float]($S * 0.195))
    $framePen = New-Object System.Drawing.Pen($Border, [float]($S * 0.0045))
    $g.DrawPath($framePen, $frame)
    $framePen.Dispose()
    $frame.Dispose()

    # --- Монограмма «VL»: одна непрерывная лента V → L ---
    # Точки в системе 1024×1024.
    $u = [float]($S / 1024.0)
    $linePoints = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF([float](198 * $u), [float](322 * $u))),
        (New-Object System.Drawing.PointF([float](398 * $u), [float](706 * $u))),
        (New-Object System.Drawing.PointF([float](598 * $u), [float](322 * $u))),
        (New-Object System.Drawing.PointF([float](598 * $u), [float](712 * $u))),
        (New-Object System.Drawing.PointF([float](846 * $u), [float](712 * $u)))
    )

    $stroke = [float]($S * 0.096)

    # Тень: несколько смещённых полупрозрачных проходов.
    $shadowPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(30, $Shadow.R, $Shadow.G, $Shadow.B), $stroke)
    $shadowPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $shadowPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $shadowPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    foreach ($pass in 1..4) {
        $shadowPen.Width = [float]($stroke * (1.0 + 0.06 * $pass))
        $g.DrawLines($shadowPen, $linePoints)
    }
    $shadowPen.Dispose()

    $g.TranslateTransform(0, [float](14 * $u))
    $shadowPen2 = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(45, $Shadow.R, $Shadow.G, $Shadow.B), [float]($stroke * 1.02))
    $shadowPen2.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $shadowPen2.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $shadowPen2.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawLines($shadowPen2, $linePoints)
    $shadowPen2.Dispose()
    $g.ResetTransform()

    # Сама лента: градиент от тёплого белого к мятному.
    $inkRect = New-Object System.Drawing.RectangleF(0, [float]($S * 0.22), $S, [float]($S * 0.62))
    $inkBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($inkRect, $InkTop, $InkBottom, 90.0)
    $inkPen = New-Object System.Drawing.Pen($inkBrush, $stroke)
    $inkPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $inkPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $inkPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawLines($inkPen, $linePoints)
    $inkPen.Dispose()
    $inkBrush.Dispose()

    $g.Dispose()

    if ($ss -eq 1 -and $S -eq $size) { return $bmp }

    $result = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $rg = [System.Drawing.Graphics]::FromImage($result)
    $rg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $rg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $rg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $rg.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $rg.DrawImage($bmp, (New-Object System.Drawing.RectangleF(0, 0, $size, $size)))
    $rg.Dispose()
    $bmp.Dispose()
    return $result
}

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return $bytes
}

# Маленькие размеры пишем как 32-битный DIB (BMP), а не PNG: PNG-записи
# внутри ICO понимает не каждая оболочка Windows, из-за чего на панели задач
# вместо значка появлялся пустой/шумный квадрат. 256 остаётся PNG.
function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $size = $bmp.Width
    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $data = $bmp.LockBits($rect,
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $rowBytes = $size * 4
    $row = New-Object byte[] $rowBytes
    $maskRow = [int]([math]::Floor(($size + 31) / 32) * 4)
    $mask = New-Object byte[] ($maskRow * $size)
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    try {
        # BITMAPINFOHEADER: высота = две копии (XOR + AND).
        $bw.Write([uint32]40)
        $bw.Write([int32]$size)
        $bw.Write([int32]($size * 2))
        $bw.Write([uint16]1)
        $bw.Write([uint16]32)
        $bw.Write([uint32]0)
        $bw.Write([uint32]($rowBytes * $size + $mask.Length))
        $bw.Write([int32]0)
        $bw.Write([int32]0)
        $bw.Write([uint32]0)
        $bw.Write([uint32]0)
        # Пиксели снизу вверх: BGRA, как в памяти Format32bppArgb.
        for ($y = $size - 1; $y -ge 0; $y--) {
            [System.Runtime.InteropServices.Marshal]::Copy(
                [IntPtr]::Add($data.Scan0, $y * $stride), $row, 0, $rowBytes)
            $bw.Write($row)
        }
        # AND-маска не нужна: прозрачность задаёт альфа-канал.
        $bw.Write($mask)
        $bw.Flush()
        return $ms.ToArray()
    }
    finally {
        $bmp.UnlockBits($data)
        $bw.Dispose()
        $ms.Dispose()
    }
}

# --- Исходник 1024 ---
$src = New-IconBitmap 1024
$src.Save((Join-Path $RepoRoot 'branding\vienlink-icon-src.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$src.Dispose()

# --- PNG 512 для Assets и компоновки логотипа ---
$png512 = New-IconBitmap 512
$png512.Save((Join-Path $RepoRoot 'VieNLink\Assets\VieNLink.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$png512.Dispose()

# --- Многоразмерный ICO: маленькие кадры DIB, 256 — PNG ---
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($sz in $sizes) {
    $b = New-IconBitmap $sz
    $frames.Add($(if ($sz -ge 256) { Get-PngBytes $b } else { Get-DibBytes $b }))
    $b.Dispose()
}

$icoPath = Join-Path $RepoRoot 'VieNLink\Assets\VieNLink.ico'
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
try {
    $bw.Write([uint16]0)          # reserved
    $bw.Write([uint16]1)          # type: icon
    $bw.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $sz = $sizes[$i]
        $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))  # width (0 = 256)
        $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))  # height
        $bw.Write([byte]0)          # palette
        $bw.Write([byte]0)          # reserved
        $bw.Write([uint16]1)        # planes
        $bw.Write([uint16]32)       # bpp
        $bw.Write([uint32]$frames[$i].Length)
        $bw.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($p in $frames) { $bw.Write($p) }
}
finally {
    $bw.Dispose()
    $fs.Dispose()
}

Write-Output 'Иконка сгенерирована:'
Write-Output "  $icoPath"
Write-Output "  $(Join-Path $RepoRoot 'VieNLink\Assets\VieNLink.png')"
Write-Output "  $(Join-Path $RepoRoot 'branding\vienlink-icon-src.png')"
