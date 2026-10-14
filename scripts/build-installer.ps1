$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskCompiler = Join-Path $taskRoot 'tools\innosetup\tools\ISCC.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) {
    $taskTools = Join-Path $taskRoot 'tools'
    New-Item -ItemType Directory -Path $taskTools -Force | Out-Null
    $taskArchive = Join-Path $taskTools 'innosetup.zip'
    Invoke-WebRequest 'https://api.nuget.org/v3-flatcontainer/tools.innosetup/6.7.3/tools.innosetup.6.7.3.nupkg' -OutFile $taskArchive
    Expand-Archive -LiteralPath $taskArchive -DestinationPath (Join-Path $taskTools 'innosetup') -Force
}
if (-not (Test-Path -LiteralPath (Join-Path $taskRoot 'artifacts\win-x64\NetEaseNEXT.pri'))) { throw 'Run scripts\build.ps1 -Publish first.' }
& $taskCompiler /Q (Join-Path $PSScriptRoot 'setup.iss')
if ($LASTEXITCODE) { throw 'Installer compilation failed.' }
