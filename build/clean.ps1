#Requires -Version 5.1
<#
.SYNOPSIS
    Убирает мусор от сборок: старые артефакты в release\, промежуточные bin/obj и временные файлы.

.DESCRIPTION
    Оставляет в release\ только файлы указанной версии (по умолчанию — версия из
    build\Version.props) и SHA256SUMS.txt. Всё остальное (старые установщики, ZIP, APK,
    распакованные папки win-x64) удаляется.

    Промежуточные каталоги bin/obj удаляются, если приложение не запущено: занятые файлы
    Windows-сборки пропускаются, чтобы не мешать работающему VieNLink.

.EXAMPLE
    powershell -File build\clean.ps1

.EXAMPLE
    powershell -File build\clean.ps1 -Keep 1.3.46 -DryRun

.EXAMPLE
    powershell -File build\clean.ps1 -All      # удалить всё, включая артефакты текущей версии
#>
[CmdletBinding()]
param(
    # Версия, артефакты которой остаются в release\ (по умолчанию — из build\Version.props).
    [string]$Keep,
    # Удалить всё в release\, включая текущую версию.
    [switch]$All,
    # Только показать, что будет удалено.
    [switch]$DryRun
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'version.ps1')

if (-not $Keep -and -not $All) {
    $shared = Get-VieNLinkVersion -Root $root
    if ($shared) {
        $Keep = $shared.Version
    }
}

# Браузер — отдельное приложение со своей версией: его артефакты тоже не трогаем.
$browserVersion = $null
$browserShared = Get-VieNLinkBrowserVersion -Root $root
if ($browserShared) {
    $browserVersion = $browserShared.Version
}

$keepPrefixes = @()
if (-not $All) {
    if ($Keep) { $keepPrefixes += ('VieNLink-' + $Keep + '-') }
    if ($browserVersion) { $keepPrefixes += ('VieNLink-Browser-' + $browserVersion + '-') }
}

$freed = 0

function Get-SizeMb([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return 0 }
    $sum = (Get-ChildItem -LiteralPath $path -Recurse -File -ErrorAction SilentlyContinue |
        Measure-Object Length -Sum).Sum
    if (-not $sum) { return 0 }
    return [math]::Round($sum / 1MB, 0)
}

function Remove-ItemSafe([string]$path) {
    $mb = Get-SizeMb $path
    if ($DryRun) {
        Write-Host ("  [показать] " + $path + " (" + $mb + " МБ)")
        return 0
    }

    Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
    return $mb
}

# --- release\ ---
$release = Join-Path $root 'release'
if (Test-Path -LiteralPath $release) {
    $before = Get-SizeMb $release
    Write-Output ("release: до чистки " + $before + " МБ" + $(if ($Keep) { ", оставляю версию " + $Keep } else { ", оставляю всё" }))

    foreach ($item in (Get-ChildItem -LiteralPath $release -Force)) {
        $keepByName = $false
        foreach ($prefix in $keepPrefixes) {
            if ($item.Name.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                $keepByName = $true
                break
            }
        }

        if ($item.PSIsContainer) {
            if ($keepByName) {
                continue
            }

            $freed += Remove-ItemSafe $item.FullName
            continue
        }

        if ($keepByName -or ($item.Name -eq 'SHA256SUMS.txt' -and -not $All)) {
            continue
        }

        $freed += Remove-ItemSafe $item.FullName
    }

    if (-not $DryRun) {
        Write-Output ("release: после чистки " + (Get-SizeMb $release) + " МБ")
    }
}

# --- bin/obj ---
$running = Get-Process VieNLink -ErrorAction SilentlyContinue
$buildDirs = @(
    'VieNLink\bin', 'VieNLink\obj',
    'VieNLink.Android\bin', 'VieNLink.Android\obj',
    'VieNLink.Core\bin', 'VieNLink.Core\obj',
    'VieNLink.SelfTest\bin', 'VieNLink.SelfTest\obj')
foreach ($relative in $buildDirs) {
    $path = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $path)) { continue }
    if ($running -and $relative.StartsWith('VieNLink\', [StringComparison]::OrdinalIgnoreCase)) {
        Write-Output ("пропущено (приложение запущено): " + $relative)
        continue
    }

    $freed += Remove-ItemSafe $path
}

# --- Временные файлы сборки ---
foreach ($pattern in 'VieNLink*', 'vienlink-*', 'libgojni-*') {
    foreach ($item in (Get-ChildItem $env:TEMP -Filter $pattern -ErrorAction SilentlyContinue)) {
        $freed += Remove-ItemSafe $item.FullName
    }
}

Write-Output ""
if ($DryRun) {
    Write-Output ("Будет освобождено примерно " + $freed + " МБ (режим -DryRun)")
} else {
    Write-Output ("Освобождено: " + [math]::Round($freed / 1024, 1) + " ГБ")
}
