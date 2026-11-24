# Общие версии Windows/Android: единственный источник — build\Version.props.
function Get-VieNLinkVersion {
    param([Parameter(Mandatory = $true)][string]$Root)
    $propsPath = Join-Path $Root 'build\Version.props'
    if (-not (Test-Path -LiteralPath $propsPath)) {
        return $null
    }

    [xml]$props = Get-Content -LiteralPath $propsPath -Encoding UTF8
    $version = @($props.Project.PropertyGroup | ForEach-Object { $_.VieNLinkVersion } |
        Where-Object { $_ })[0]
    $code = @($props.Project.PropertyGroup | ForEach-Object { $_.VieNLinkVersionCode } |
        Where-Object { $_ })[0]
    if (-not $version) {
        return $null
    }

    return @{
        Version = [string]$version
        VersionCode = if ($code) { [int]$code } else { 0 }
    }
}

# Версия отдельного приложения «VieNLink Браузер»: build\BrowserVersion.props.
function Get-VieNLinkBrowserVersion {
    param([Parameter(Mandatory = $true)][string]$Root)

    $propsPath = Join-Path $Root 'build\BrowserVersion.props'
    if (-not (Test-Path -LiteralPath $propsPath)) {
        return $null
    }

    [xml]$props = Get-Content -LiteralPath $propsPath -Encoding UTF8
    $version = @($props.Project.PropertyGroup | ForEach-Object { $_.VieNLinkBrowserVersion } |
        Where-Object { $_ })[0]
    $code = @($props.Project.PropertyGroup | ForEach-Object { $_.VieNLinkBrowserVersionCode } |
        Where-Object { $_ })[0]
    if (-not $version) {
        return $null
    }

    return @{
        Version = [string]$version
        VersionCode = if ($code) { [int]$code } else { 0 }
    }
}