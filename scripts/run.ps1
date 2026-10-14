param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskExe = Join-Path $taskRoot "NetEaseNEXT\bin\x64\$Configuration\net9.0-windows10.0.26100.0\win-x64\NetEaseNEXT.exe"
if (-not (Test-Path -LiteralPath $taskExe)) { & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration }
Start-Process -FilePath $taskExe
