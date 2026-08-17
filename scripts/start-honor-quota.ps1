$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Exe = Join-Path $ScriptDir "HonorQuota.exe"
if (-not (Test-Path -LiteralPath $Exe)) { throw "HonorQuota.exe not found: $Exe" }
Start-Process -FilePath $Exe -WorkingDirectory $ScriptDir
