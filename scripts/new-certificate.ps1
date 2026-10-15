param([string]$Subject = 'CN=NetEaseNEXT Development', [switch]$ForceNew)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskDirectory = Join-Path $taskRoot 'certificates'
New-Item -ItemType Directory -Path $taskDirectory -Force | Out-Null
$taskBinding = Join-Path $taskDirectory 'signing.json'
$taskCert = $null
if (-not $ForceNew -and (Test-Path -LiteralPath $taskBinding)) {
    $taskSaved = Get-Content -LiteralPath $taskBinding -Raw | ConvertFrom-Json
    $taskCert = Get-Item -LiteralPath "Cert:\CurrentUser\My\$($taskSaved.Thumbprint)" -ErrorAction SilentlyContinue
    if (-not $taskCert -or -not $taskCert.HasPrivateKey -or $taskCert.Subject -ne $Subject -or $taskCert.NotAfter -le (Get-Date)) {
        throw 'The bound signing certificate is unavailable or invalid. Restore its private key, or explicitly use -ForceNew.'
    }
}
if (-not $taskCert) {
    $taskCert = New-SelfSignedCertificate -Type Custom -Subject $Subject -FriendlyName 'NetEaseNEXT Development (17 years)' -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyUsage DigitalSignature -KeyExportPolicy NonExportable -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddYears(17) -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}')
}
Export-Certificate -Cert $taskCert -FilePath (Join-Path $taskDirectory 'NetEaseNEXT.cer') | Out-Null
[ordered]@{ Subject = $taskCert.Subject; Thumbprint = $taskCert.Thumbprint; NotAfter = $taskCert.NotAfter.ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath $taskBinding -Encoding utf8
# Only a public certificate and a local thumbprint binding are written to disk.
$taskCert | Select-Object Subject, Thumbprint, NotBefore, NotAfter
Write-Host 'MSIX signing is bound to this certificate. Its private key remains in CurrentUser\My. Trust has not been changed.'
