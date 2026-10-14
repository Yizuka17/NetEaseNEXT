param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [ValidateSet('x64','ARM64')][string]$Architecture = 'x64',
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
Push-Location $taskRoot
try {
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'Install Node.js 22 or newer first.' }
    if ([int]((& node -p 'process.versions.node.split(".")[0]')) -lt 22) { throw 'Node.js 22+ is required.' }
    Push-Location api
    try { & npm ci --no-audit --no-fund; if ($LASTEXITCODE) { throw 'npm ci failed.' } }
    finally { Pop-Location }
    $taskProject = Join-Path $taskRoot 'NetEaseNEXT\NetEaseNEXT.csproj'
    & dotnet build $taskProject -c $Configuration "-p:Platform=$Architecture"
    if ($LASTEXITCODE) { throw 'Build failed.' }
    if ($Publish) {
        $taskRid = if ($Architecture -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
        $taskOutput = Join-Path $taskRoot "artifacts\$taskRid"
        & dotnet publish $taskProject -c $Configuration "-p:Platform=$Architecture" -r $taskRid -o $taskOutput --no-restore
        if ($LASTEXITCODE) { throw 'Publish failed.' }
        & robocopy (Join-Path $taskRoot 'api\node_modules') (Join-Path $taskOutput 'api\node_modules') /E /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw 'Copying API dependencies failed.' }
        $global:LASTEXITCODE = 0 # Robocopy 1-7 are successful copy results.
        New-Item -ItemType Directory -Path (Join-Path $taskOutput 'runtime') -Force | Out-Null
        # Bundle the installed Node runtime only when its architecture matches.
        $taskNodeArch = & node -p 'process.arch'
        $taskExpectedArch = if ($Architecture -eq 'ARM64') { 'arm64' } else { 'x64' }
        if ($taskNodeArch -ne $taskExpectedArch) { throw "Publish needs a $taskExpectedArch Node runtime; current Node is $taskNodeArch." }
        Copy-Item -LiteralPath (Get-Command node).Source -Destination (Join-Path $taskOutput 'runtime\node.exe') -Force
        & robocopy (Join-Path $taskRoot 'third-party') (Join-Path $taskOutput 'third-party') /E /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw 'Copying licenses failed.' }
        $global:LASTEXITCODE = 0
        Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination $taskOutput -Force
        Write-Host "Portable app: $taskOutput\NetEaseNEXT.exe"
    }
}
finally { Pop-Location }
