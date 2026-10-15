param(
    [ValidateSet('x64','ARM64')][string]$Architecture = 'x64',
    [string]$CertificateThumbprint,
    [switch]$Unsigned
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'msix-signature.ps1')
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskRid = if ($Architecture -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$taskPublish = Join-Path $taskRoot "artifacts\$taskRid"
if (-not (Test-Path -LiteralPath (Join-Path $taskPublish 'NetEaseNEXT.pri'))) { throw 'Run scripts/build.ps1 -Publish first.' }
$taskSdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$taskSdk = Get-ChildItem -LiteralPath $taskSdkRoot -Directory | Where-Object { $_.Name -match '^10\.0\.\d+\.0$' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'x64\makeappx.exe')) } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $taskSdk) { throw 'Install the Windows 10/11 SDK with MakeAppx, MakePri and SignTool.' }
$taskTools = Join-Path $taskSdk.FullName 'x64'
[xml]$taskManifest = Get-Content -LiteralPath (Join-Path $taskRoot 'packaging\AppxManifest.xml') -Raw
$taskManifest.Package.Identity.ProcessorArchitecture = $Architecture.ToLowerInvariant()
$taskVersion = $taskManifest.Package.Identity.Version
$taskCert = $null
if (-not $Unsigned) {
    if (-not $CertificateThumbprint) {
        $taskBinding = Join-Path $taskRoot 'certificates\signing.json'
        if (-not (Test-Path -LiteralPath $taskBinding)) { throw 'Run scripts/new-certificate.ps1 to create and bind the 17-year signing certificate.' }
        $CertificateThumbprint = (Get-Content -LiteralPath $taskBinding -Raw | ConvertFrom-Json).Thumbprint
    }
    $taskCert = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
    if (-not $taskCert.HasPrivateKey -or $taskCert.NotBefore -gt (Get-Date) -or $taskCert.NotAfter -le (Get-Date)) { throw 'Signing certificate has no private key or is outside its validity period.' }
    if ($taskCert.Subject -cne $taskManifest.Package.Identity.Publisher) { throw 'Certificate Subject must exactly match the manifest Publisher.' }
}
$taskOutput = Join-Path $taskRoot 'artifacts\msix'
$taskStage = Join-Path $taskOutput ('staging\' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskStage -Force | Out-Null
& robocopy $taskPublish $taskStage /E /XF '*.pdb' '*.log' /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'Copying the published application failed.' }
$global:LASTEXITCODE = 0
$taskManifest.Save((Join-Path $taskStage 'AppxManifest.xml'))
& (Join-Path $PSScriptRoot 'create-package-assets.ps1') -Destination (Join-Path $taskStage 'PackageAssets')
$taskPriConfig = Join-Path $taskOutput 'priconfig.xml'
& (Join-Path $taskTools 'makepri.exe') createconfig /cf $taskPriConfig /dq en-US /o | Out-Null
if ($LASTEXITCODE) { throw 'Creating the package resource configuration failed.' }
# Index shell assets and the already merged WinUI PRI exactly once. Scanning
# every self-contained framework PRI again duplicates XAML resources.
[xml]$taskPri = Get-Content -LiteralPath $taskPriConfig -Raw
[void]$taskPri.resources.RemoveChild($taskPri.resources.packaging)
$taskAssetIndex = $taskPri.resources.index
$taskAssetIndex.SetAttribute('startIndexAt','PackageAssets')
foreach ($taskIndexer in @($taskAssetIndex.'indexer-config')) { if ($taskIndexer.type -ne 'folder') { [void]$taskAssetIndex.RemoveChild($taskIndexer) } }
$taskAppIndex = $taskAssetIndex.CloneNode($true)
$taskAppIndex.SetAttribute('startIndexAt','NetEaseNEXT.pri')
foreach ($taskIndexer in @($taskAppIndex.'indexer-config')) { [void]$taskAppIndex.RemoveChild($taskIndexer) }
$taskPriIndexer = $taskPri.CreateElement('indexer-config'); $taskPriIndexer.SetAttribute('type','PRI')
[void]$taskAppIndex.AppendChild($taskPriIndexer); [void]$taskPri.resources.AppendChild($taskAppIndex)
$taskPri.Save($taskPriConfig)
& (Join-Path $taskTools 'makepri.exe') new /pr $taskStage /cf $taskPriConfig /of (Join-Path $taskStage 'resources.pri') /mn (Join-Path $taskStage 'AppxManifest.xml') /o | Out-Null
if ($LASTEXITCODE) { throw 'Generating the package resource index failed.' }
$taskSuffix = if ($Unsigned) { '.unsigned' } else { '' }
$taskPackage = Join-Path $taskOutput "NetEaseNEXT-$taskVersion-$($Architecture.ToLowerInvariant())$taskSuffix.msix"
& (Join-Path $taskTools 'makeappx.exe') pack /d $taskStage /p $taskPackage /o
if ($LASTEXITCODE) { throw 'MSIX validation or packaging failed.' }
if ($taskCert) {
    & (Join-Path $taskTools 'signtool.exe') sign /fd SHA256 /s My /sha1 $taskCert.Thumbprint $taskPackage
    if ($LASTEXITCODE) { throw 'MSIX signing failed.' }
    $taskSignature = Get-MsixSignature $taskPackage
    if ($taskSignature.SignerCertificate.Thumbprint -ne $taskCert.Thumbprint) { throw 'The package signer does not match the bound certificate.' }
    Export-Certificate -Cert $taskCert -FilePath (Join-Path $taskOutput 'NetEaseNEXT.cer') | Out-Null
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install-msix.ps1') -Destination $taskOutput -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'msix-signature.ps1') -Destination $taskOutput -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'trust-msix-certificate.ps1') -Destination $taskOutput -Force
[ordered]@{ Package = (Split-Path $taskPackage -Leaf); Version = $taskVersion; Architecture = $Architecture; Signed = [bool]$taskCert; Subject = $taskCert.Subject; Thumbprint = $taskCert.Thumbprint; NotAfter = if ($taskCert) { $taskCert.NotAfter.ToString('o') } else { $null }; Sha256 = (Get-FileHash -LiteralPath $taskPackage -Algorithm SHA256).Hash } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskOutput 'package-info.json') -Encoding utf8
Write-Host "MSIX package: $taskPackage"
