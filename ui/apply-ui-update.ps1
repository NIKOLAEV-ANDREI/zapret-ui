$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$current = Join-Path $root "zapret.exe"
$next = Join-Path $root "zapret.next.exe"

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

Write-Host "Updated: $current"

