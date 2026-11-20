#Requires -Version 5.1
# Собирает логотип VieNLink: горизонтальный вордмарк для README и обложку 1280x640
# для GitHub Social Preview. Использует готовую иконку VieNLink.png.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$iconPath = Join-Path $root 'VieNLink\Assets\VieNLink.png'
$outDir = Join-Path $root 'branding'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$graphite = [System.Drawing.ColorTranslator]::FromHtml('#18191B')
$borderColor = [System.Drawing.ColorTranslator]::FromHtml('#2C3035')
$white = [System.Drawing.ColorTranslator]::FromHtml('#F2F3F5')
$mint = [System.Drawing.ColorTranslator]::FromHtml('#9CCDB0')
$muted = [System.Drawing.ColorTranslator]::FromHtml('#A3A9AF')

function New-RoundedPath([int]$w, [int]$h, [int]$radius) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($w - $d, 0, $d, $d, 270, 90)
    $path.AddArc($w - $d, $h - $d, $d, $d, 0, 90)
    $path.AddArc(0, $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-Logo([int]$w, [int]$h, [int]$iconSize, [int]$iconX, [int]$iconY,
                 [single]$titleSize, [single]$titleX, [single]$titleY,
                 [single]$subSize, [single]$subX, [single]$subY) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear($graphite)

    $panel = New-RoundedPath $w $h 46
    $bg = New-Object System.Drawing.SolidBrush($graphite)
    $g.FillPath($bg, $panel)
    $frame = New-Object System.Drawing.Pen($borderColor, 4)
    $g.DrawPath($frame, $panel)

    $icon = [System.Drawing.Image]::FromFile($iconPath)
    $g.DrawImage($icon, $iconX, $iconY, $iconSize, $iconSize)

    $titleFont = New-Object System.Drawing.Font('Segoe UI', $titleSize,
        [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $subFont = New-Object System.Drawing.Font('Segoe UI', $subSize,
        [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $format = [System.Drawing.StringFormat]::GenericTypographic

    $whiteBrush = New-Object System.Drawing.SolidBrush($white)
    $mintBrush = New-Object System.Drawing.SolidBrush($mint)
    $mutedBrush = New-Object System.Drawing.SolidBrush($muted)

    $g.DrawString('VieN', $titleFont, $whiteBrush, $titleX, $titleY, $format)
    $vienWidth = $g.MeasureString('VieN', $titleFont, [System.Drawing.PointF]::Empty, $format).Width
    $g.DrawString('Link', $titleFont, $mintBrush, $titleX + $vienWidth - 2, $titleY, $format)
    $g.DrawString('свободные узлы · бета', $subFont, $mutedBrush, $subX, $subY, $format)

    $icon.Dispose(); $bg.Dispose(); $frame.Dispose(); $panel.Dispose()
    $titleFont.Dispose(); $subFont.Dispose()
    $whiteBrush.Dispose(); $mintBrush.Dispose(); $mutedBrush.Dispose()
    $g.Dispose()
    return $bmp
}

$wordmark = New-Logo 1600 480 300 78 90 120 432 118 38 436 306
$wordmarkPath = Join-Path $outDir 'vienlink-wordmark.png'
$wordmark.Save($wordmarkPath, [System.Drawing.Imaging.ImageFormat]::Png)
$wordmark.Dispose()

$social = New-Logo 1280 640 360 86 140 150 500 200 46 506 402
$socialPath = Join-Path $outDir 'vienlink-social.png'
$social.Save($socialPath, [System.Drawing.Imaging.ImageFormat]::Png)
$social.Dispose()

Write-Host "Wordmark: $wordmarkPath"
Write-Host "Social:   $socialPath"
