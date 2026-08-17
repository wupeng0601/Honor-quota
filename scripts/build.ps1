[CmdletBinding()]
param(
    [string]$WebView2Version = "1.0.4022.49",
    [switch]$NoPackage
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$BuildRoot = Join-Path $RepoRoot "build"
$OutputDir = Join-Path $BuildRoot "HonorQuota"
$NugetDir = Join-Path $RepoRoot ".build\webview2"
$PackageFile = Join-Path $NugetDir ("Microsoft.Web.WebView2-" + $WebView2Version + ".zip")
$PackageDir = Join-Path $NugetDir $WebView2Version
$Csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (-not (Test-Path -LiteralPath $Csc)) {
    throw "The .NET Framework C# compiler was not found: $Csc"
}

New-Item -ItemType Directory -Force -Path $NugetDir | Out-Null
if (-not (Test-Path -LiteralPath (Join-Path $PackageDir "lib\net462\Microsoft.Web.WebView2.Core.dll"))) {
    if (-not (Test-Path -LiteralPath $PackageFile)) {
        $url = "https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/$WebView2Version"
        Write-Host "Downloading Microsoft.Web.WebView2 $WebView2Version..."
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $PackageFile
    }
    New-Item -ItemType Directory -Force -Path $PackageDir | Out-Null
    Expand-Archive -LiteralPath $PackageFile -DestinationPath $PackageDir -Force
}

if (Test-Path -LiteralPath $OutputDir) {
    Remove-Item -LiteralPath $OutputDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$webViewCore = Join-Path $PackageDir "lib\net462\Microsoft.Web.WebView2.Core.dll"
$webViewForms = Join-Path $PackageDir "lib\net462\Microsoft.Web.WebView2.WinForms.dll"
$loader = Join-Path $PackageDir "build\native\x64\WebView2Loader.dll"
$source = Join-Path $RepoRoot "src\HonorQuotaApp.cs"
$refs = @(
    (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\System.dll"),
    (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\System.Core.dll"),
    (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\System.Drawing.dll"),
    (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\System.Windows.Forms.dll"),
    (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\System.Web.Extensions.dll"),
    $webViewCore,
    $webViewForms
)

$arguments = @("/nologo", "/target:winexe", "/platform:anycpu", ("/out:" + (Join-Path $OutputDir "HonorQuota.exe")))
foreach ($reference in $refs) { $arguments += ("/reference:" + $reference) }
$arguments += $source
& $Csc @arguments
if ($LASTEXITCODE -ne 0) { throw "C# compilation failed with exit code $LASTEXITCODE" }

Copy-Item -LiteralPath $webViewCore -Destination $OutputDir -Force
Copy-Item -LiteralPath $webViewForms -Destination $OutputDir -Force
Copy-Item -LiteralPath $loader -Destination (Join-Path $OutputDir "WebView2Loader.dll") -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot "src\honor_quota_cli.py") -Destination $OutputDir -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot "assets\HonorQuota.ico") -Destination $OutputDir -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot "assets\HonorQuota.logo.png") -Destination $OutputDir -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot "scripts\install-honor-quota.ps1") -Destination $OutputDir -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot "scripts\start-honor-quota.ps1") -Destination $OutputDir -Force

if (-not $NoPackage) {
    $dist = Join-Path $RepoRoot "dist"
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    $zip = Join-Path $dist "HonorQuota-0.1.0-win-x64.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $OutputDir "*") -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "Package: $zip"
}

Write-Host "Build output: $OutputDir"
