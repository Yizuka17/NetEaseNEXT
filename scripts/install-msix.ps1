param(
    [string]$PackagePath,
    [switch]$TrustCertificate,
    [switch]$RemoveLegacyInstall
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'msix-signature.ps1')
if (-not $PackagePath) {
    $taskSearch = if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'package-info.json')) { $PSScriptRoot } else { Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\msix' }
    $PackagePath = (Get-ChildItem -LiteralPath $taskSearch -Filter '*.msix' | Where-Object Name -NotLike '*.unsigned.msix' | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
if (-not $PackagePath) { throw 'No signed MSIX package found.' }
$PackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
$taskSignature = Get-MsixSignature $PackagePath
if ($taskSignature.SignerCertificate.Subject -cne 'CN=NetEaseNEXT Development') { throw 'Unexpected package publisher.' }
if ($TrustCertificate) {
    $taskPrincipal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Trusting a self-signed MSIX certificate requires an administrator PowerShell window. Re-run with -TrustCertificate there.' }
    $taskCer = Join-Path ([IO.Path]::GetDirectoryName($PackagePath)) 'NetEaseNEXT.cer'
    $taskPublic = [Security.Cryptography.X509Certificates.X509Certificate2]::new($taskCer)
    if ($taskPublic.HasPrivateKey -or $taskPublic.Thumbprint -ne $taskSignature.SignerCertificate.Thumbprint) { throw 'The public certificate does not match the package signature.' }
    Import-Certificate -FilePath $taskCer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
}
$taskLegacyKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{F2431F11-C7B6-4E8C-80FC-C273231D908E}_is1'
$taskLegacy = Get-ItemProperty -LiteralPath $taskLegacyKey -ErrorAction SilentlyContinue
Add-AppxPackage -Path $PackagePath -ErrorAction Stop
$taskInstalled = Get-AppxPackage -Name NetEaseNEXT
if (-not $taskInstalled -or $taskInstalled.Publisher -cne $taskSignature.SignerCertificate.Subject) { throw 'Package installation could not be confirmed. The old installation has been retained.' }
if ($RemoveLegacyInstall -and $taskLegacy) {
    # Remove only our installer-owned deployment after MSIX was installed.
    # Settings, DPAPI credentials and caches are outside that deployment.
    $taskLegacyDir = [IO.Path]::GetFullPath($taskLegacy.InstallLocation).TrimEnd('\')
    $taskLegacyExe = Join-Path $taskLegacyDir 'NetEaseNEXT.exe'
    $taskUninstaller = Join-Path $taskLegacyDir 'unins000.exe'
    if ($taskLegacyDir -eq [IO.Path]::GetPathRoot($taskLegacyDir) -or -not (Test-Path -LiteralPath $taskUninstaller) -or [Diagnostics.FileVersionInfo]::GetVersionInfo($taskLegacyExe).ProductName -ne 'NetEaseNEXT') { throw 'Cannot verify the legacy application directory; no legacy files were removed.' }
    if (Get-Process NetEaseNEXT -ErrorAction SilentlyContinue | Where-Object Path -EQ $taskLegacyExe) { throw 'Exit the old NetEaseNEXT from its tray menu before migrating.' }
    $taskRemoval = Start-Process -FilePath $taskUninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -PassThru -Wait
    if ($taskRemoval.ExitCode -ne 0) { throw "Legacy uninstaller exited with $($taskRemoval.ExitCode)." }
    $taskShell = New-Object -ComObject WScript.Shell
    $taskPrograms = [Environment]::GetFolderPath('Programs')
    foreach ($taskRelative in @('NetEaseNEXT.lnk','NetEaseNEXT\NetEaseNEXT.lnk')) {
        $taskLinkPath = Join-Path $taskPrograms $taskRelative
        if (-not (Test-Path -LiteralPath $taskLinkPath)) { continue }
        $taskLink = $taskShell.CreateShortcut($taskLinkPath)
        if ($taskLink.TargetPath -eq $taskLegacyExe) { Remove-Item -LiteralPath $taskLinkPath }
    }
    # The MSIX package now owns its identity; remove the old manual registration.
    $taskIdentityKey = 'HKCU:\Software\Classes\AppUserModelId\NetEaseNEXT.Desktop'
    if (Test-Path -LiteralPath $taskIdentityKey) {
        $taskIdentity = Get-ItemProperty -LiteralPath $taskIdentityKey
        if ($taskIdentity.DisplayName -eq 'NetEaseNEXT') { Remove-Item -LiteralPath $taskIdentityKey }
    }
}
$taskInstalled | Select-Object Name,Version,PackageFamilyName,InstallLocation
