[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "HonorQuota")
)

$ErrorActionPreference = "Stop"
$SourceDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Exe = Join-Path $InstallDir "HonorQuota.exe"
$WebView2ClientId = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
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

function Test-WebView2Runtime {
    $paths = @(
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$WebView2ClientId",
        "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$WebView2ClientId",
        "HKCU:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$WebView2ClientId",
        "HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$WebView2ClientId"
    )
    foreach ($path in $paths) {
        $entry = Get-ItemProperty -Path $path -ErrorAction SilentlyContinue
        if ($entry -and -not [string]::IsNullOrWhiteSpace([string]$entry.pv)) { return $true }
    }
    return $false
}

function Ensure-WebView2Runtime {
    if (Test-WebView2Runtime) { return }

    $bootstrapper = Join-Path ([IO.Path]::GetTempPath()) "HonorQuota-WebView2Bootstrapper.exe"
    $downloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703"
    Write-Output "Microsoft Edge WebView2 Runtime was not detected. Downloading the official Evergreen installer..."
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $downloadUrl -OutFile $bootstrapper
        Start-Process -FilePath $bootstrapper -ArgumentList "/silent", "/install" -Wait
        if (-not (Test-WebView2Runtime)) {
            Write-Warning "WebView2 Runtime could not be confirmed after installation. If the app does not start, install it manually from https://developer.microsoft.com/microsoft-edge/webview2/"
        }
    }
    catch {
        Write-Warning "Automatic WebView2 installation failed: $($_.Exception.Message)"
        Write-Warning "Install Microsoft Edge WebView2 Runtime manually from https://developer.microsoft.com/microsoft-edge/webview2/"
    }
    finally {
        if (Test-Path -LiteralPath $bootstrapper) {
            Remove-Item -LiteralPath $bootstrapper -Force -ErrorAction SilentlyContinue
        }
    }
}

Ensure-WebView2Runtime

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

Write-Output "Installed to: $InstallDir"
Write-Output "Start Menu shortcut: $shortcutPath"
