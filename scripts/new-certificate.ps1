param([string]$Subject = 'CN=NetEaseNEXT Development')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskDirectory = Join-Path $taskRoot 'certificates'
New-Item -ItemType Directory -Path $taskDirectory -Force | Out-Null
$taskCert = New-SelfSignedCertificate -Type Custom -Subject $Subject -FriendlyName 'NetEaseNEXT Development (17 years)' -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyUsage DigitalSignature -KeyExportPolicy Exportable -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddYears(17) -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}')
Export-Certificate -Cert $taskCert -FilePath (Join-Path $taskDirectory 'NetEaseNEXT.cer') | Out-Null
# Private key stays in the current user's certificate store. No password in source.
$taskCert | Select-Object Subject, Thumbprint, NotBefore, NotAfter
Write-Host 'Public certificate exported. The private key remains in CurrentUser\My. Trust has not been changed.'
