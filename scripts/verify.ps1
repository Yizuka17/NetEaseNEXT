param([switch]$Network, [switch]$MeasureBackground, [switch]$SocialRead)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
Push-Location $taskRoot
try {
    $taskApiArguments = @('run','--project','tests\NetEaseNEXT.Api.Checks','-c','Release','--')
    if ($Network) { $taskApiArguments += '--network' }
    & dotnet @taskApiArguments
    if ($LASTEXITCODE) { throw 'Native API checks failed.' }
} finally { Pop-Location }
$taskExe = Join-Path $taskRoot 'artifacts\win-x64\NetEaseNEXT.exe'
if (-not (Test-Path -LiteralPath $taskExe)) { throw 'Run scripts\build.ps1 -Publish first.' }
$taskResult = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'NetEaseNEXT\smoke-test.json'
$taskStamp = Get-Date
$taskArguments = @('--smoke-test')
if ($MeasureBackground) { $taskArguments += '--measure-background' }
if ($SocialRead) { $taskArguments += '--social-read' }
$taskApp = Start-Process -FilePath $taskExe -ArgumentList $taskArguments -PassThru
if (-not $taskApp.WaitForExit(120000)) { $taskApp.Kill(); throw 'App integration checks timed out.' }
if (-not (Test-Path -LiteralPath $taskResult) -or (Get-Item -LiteralPath $taskResult).LastWriteTime -lt $taskStamp) { throw 'App failed before producing fresh test results.' }
$taskChecks = Get-Content -LiteralPath $taskResult -Raw | ConvertFrom-Json
$taskFailures = @($taskChecks | Where-Object { $_.PSObject.Properties.Name -contains 'passed' -and -not $_.passed })
$taskChecks | ConvertTo-Json -Depth 6
if ($taskFailures.Count) { throw "$($taskFailures.Count) integration checks failed." }
