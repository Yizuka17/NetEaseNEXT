param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [ValidateSet('x64','ARM64')][string]$Architecture = 'x64',
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
Push-Location $taskRoot
try {
    $taskProject = Join-Path $taskRoot 'NetEaseNEXT\NetEaseNEXT.csproj'
    & dotnet build $taskProject -c $Configuration "-p:Platform=$Architecture"
    if ($LASTEXITCODE) { throw 'Build failed.' }
    if ($Publish) {
        $taskRid = if ($Architecture -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
        # Each publish gets a clean directory so legacy API/runtime files cannot
        # leak into the new MSIX. Preserve previous outputs for recovery.
        $taskOutput = Join-Path $taskRoot "artifacts\$taskRid"
        if (Test-Path -LiteralPath $taskOutput) {
            $taskResolved = [IO.Path]::GetFullPath($taskOutput)
            $taskExpected = [IO.Path]::GetFullPath((Join-Path $taskRoot "artifacts\$taskRid"))
            if ($taskResolved -cne $taskExpected -or (Get-Item -LiteralPath $taskOutput).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unexpected publish directory.' }
            Move-Item -LiteralPath $taskOutput -Destination ($taskOutput + '-previous-' + [guid]::NewGuid().ToString('N'))
        }
        & dotnet publish $taskProject -c $Configuration "-p:Platform=$Architecture" -r $taskRid -o $taskOutput --no-restore
        if ($LASTEXITCODE) { throw 'Publish failed.' }
        & robocopy (Join-Path $taskRoot 'third-party') (Join-Path $taskOutput 'third-party') /E /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw 'Copying licenses failed.' }
        $global:LASTEXITCODE = 0
        Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination $taskOutput -Force
        Write-Host "Portable app: $taskOutput\NetEaseNEXT.exe"
    }
}
finally { Pop-Location }
