param(
    [ValidateSet('x64','ARM64')][string]$Architecture = 'x64',
    [string]$CertificateThumbprint,
    [string]$SdkTools,
    [switch]$Unsigned
)
# MSIX is the default installer. Portable publishing remains available separately.
& (Join-Path $PSScriptRoot 'build-msix.ps1') @PSBoundParameters
