param([Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'msix-signature.ps1')
$taskPrincipal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator confirmation is required to trust the MSIX signing certificate.' }
$PackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
$taskSignature = Get-MsixSignature $PackagePath
$taskCertificatePath = Join-Path ([IO.Path]::GetDirectoryName($PackagePath)) 'NetEaseNEXT.cer'
$taskCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($taskCertificatePath)
try {
    if ($taskCertificate.Subject -cne 'CN=NetEaseNEXT Development' -or $taskCertificate.HasPrivateKey -or $taskCertificate.Thumbprint -ne $taskSignature.SignerCertificate.Thumbprint) { throw 'The public certificate does not match the expected MSIX signer.' }
    Import-Certificate -FilePath $taskCertificatePath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
} finally { $taskCertificate.Dispose() }
