function Get-MsixSignature([Parameter(Mandatory)][string]$Path) {
    $taskSignature = Get-AuthenticodeSignature -LiteralPath $Path
    $taskUntrustedRootOnly = $false
    # Windows can report CERT_E_UNTRUSTEDROOT as UnknownError rather than
    # NotTrusted. Keep that distinct from digest, expiry and other failures.
    if ($taskSignature.SignerCertificate -and $taskSignature.Status -eq 'UnknownError') {
        $taskChain = [Security.Cryptography.X509Certificates.X509Chain]::new()
        try {
            $taskChain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
            [void]$taskChain.Build($taskSignature.SignerCertificate)
            $taskUntrustedRootOnly = $taskChain.ChainStatus.Count -gt 0 -and @($taskChain.ChainStatus | Where-Object { $_.Status -ne [Security.Cryptography.X509Certificates.X509ChainStatusFlags]::UntrustedRoot }).Count -eq 0
        } finally { $taskChain.Dispose() }
    }
    if (-not $taskSignature.SignerCertificate -or ($taskSignature.Status -notin @('Valid','NotTrusted') -and -not $taskUntrustedRootOnly)) { throw "Invalid MSIX signature: $($taskSignature.Status) $($taskSignature.StatusMessage)" }
    return $taskSignature
}
