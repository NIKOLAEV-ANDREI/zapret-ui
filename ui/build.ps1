param(
    [string]$OutDir = ".."
)

$ErrorActionPreference = "Stop"

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    throw "csc.exe was not found at $csc"
}

$root = Split-Path -Parent $PSScriptRoot
$resolvedOut = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot $OutDir)
$exe = Join-Path $resolvedOut "zapret.exe"
$manifest = Join-Path $PSScriptRoot "app.manifest"
$icon = Join-Path $PSScriptRoot "assets\zapret.ico"
$sources = Get-ChildItem -LiteralPath $PSScriptRoot -Filter "*.cs" | Sort-Object Name | ForEach-Object { $_.FullName }
$framework = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"
$wpf = Join-Path $framework "WPF"

& $csc `
    /nologo `
    /codepage:65001 `
    /target:winexe `
    /out:$exe `
    /win32manifest:$manifest `
    /win32icon:$icon `
    /reference:"$wpf\PresentationCore.dll" `
    /reference:"$wpf\PresentationFramework.dll" `
    /reference:"$wpf\WindowsBase.dll" `
    /reference:"$framework\System.Xaml.dll" `
    /reference:"$framework\System.IO.Compression.dll" `
    /reference:"$framework\System.IO.Compression.FileSystem.dll" `
    /reference:"$framework\System.ServiceProcess.dll" `
    /reference:"$framework\System.Windows.Forms.dll" `
    /reference:"$framework\System.Drawing.dll" `
    /reference:"$framework\System.Net.Http.dll" `
    /reference:"$framework\Microsoft.CSharp.dll" `
    $sources

if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

Write-Host "Built: $exe"

