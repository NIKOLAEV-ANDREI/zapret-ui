param(
    [string]$TargetPath = "..\zapret.exe"
)

$ErrorActionPreference = "Stop"

$resolvedTarget = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot $TargetPath)
$desktop = [Environment]::GetFolderPath("DesktopDirectory")
$shortcutPath = Join-Path $desktop "Zapret.lnk"
$legacyShortcutPath = Join-Path $desktop "Zapret Fix.lnk"

if (Test-Path -LiteralPath $legacyShortcutPath) {
    Remove-Item -LiteralPath $legacyShortcutPath -Force
}

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $resolvedTarget.Path
$shortcut.WorkingDirectory = Split-Path -Parent $resolvedTarget.Path
$shortcut.Description = "Zapret UI"
$shortcut.IconLocation = $resolvedTarget.Path
$shortcut.Save()

Write-Host "Shortcut created: $shortcutPath"

