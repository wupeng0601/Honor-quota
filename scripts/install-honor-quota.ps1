[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "HonorQuota")
)

$ErrorActionPreference = "Stop"
$SourceDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Exe = Join-Path $InstallDir "HonorQuota.exe"
$Preserve = @(
    "opencode_go_models.json",
    "opencode_go_cache.json",
    "honor_quota_cli_cache.json",
    "usage_history.json",
    "honor-quota-app.log"
)

if (-not (Test-Path -LiteralPath (Join-Path $SourceDir "HonorQuota.exe"))) {
    throw "HonorQuota.exe was not found beside this installer: $SourceDir"
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $SourceDir -File) {
    if ($Preserve -contains $file.Name) { continue }
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $InstallDir $file.Name) -Force
}

$startMenuDir = Join-Path ([Environment]::GetFolderPath("StartMenu")) "Programs\Honor Quota"
New-Item -ItemType Directory -Force -Path $startMenuDir | Out-Null
$shortcutPath = Join-Path $startMenuDir "Honor Quota.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $Exe
$shortcut.WorkingDirectory = $InstallDir
$shortcut.IconLocation = "$Exe,0"
$shortcut.Description = "Honor Quota local quota dashboard"
$shortcut.Save()

$webViewRuntime = Get-ItemProperty -Path "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" -ErrorAction SilentlyContinue
if (-not $webViewRuntime) {
    Write-Warning "Microsoft Edge WebView2 Runtime was not detected. Install it from https://developer.microsoft.com/microsoft-edge/webview2/"
}

Write-Output "Installed to: $InstallDir"
Write-Output "Start Menu shortcut: $shortcutPath"
