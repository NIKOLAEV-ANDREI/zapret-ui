$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$current = Join-Path $root "zapret.exe"
$next = Join-Path $root "zapret.next.exe"
$utils = Join-Path $root "utils"
$pendingVersion = Join-Path $utils "app_update_pending.version"
$installedNotice = Join-Path $utils "app_update_installed.notice"
$appVersion = Join-Path $utils "app_version.txt"

if (-not (Test-Path $next)) {
    throw "Update file not found: $next"
}

Write-Host "Close Zapret if it is running. Waiting for the executable to unlock..."

while ($true) {
    try {
        $stream = [System.IO.File]::Open($current, "OpenOrCreate", "ReadWrite", "None")
        $stream.Close()
        break
    }
    catch {
        Start-Sleep -Seconds 1
    }
}

Copy-Item -LiteralPath $next -Destination $current -Force
Remove-Item -LiteralPath $next -Force

if (Test-Path $pendingVersion) {
    $version = (Get-Content -LiteralPath $pendingVersion -Raw).Trim()
    if ($version) {
        New-Item -ItemType Directory -Path $utils -Force | Out-Null
        Set-Content -LiteralPath $appVersion -Value $version -Encoding UTF8
        Set-Content -LiteralPath $installedNotice -Value "Приложение обновлено до версии $version." -Encoding UTF8
    }
    Remove-Item -LiteralPath $pendingVersion -Force
}

Write-Host "Updated: $current"

Start-Process -FilePath $current

