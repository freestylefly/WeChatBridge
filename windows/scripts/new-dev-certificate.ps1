[CmdletBinding()]
param(
    [string]$Subject = 'CN=WeChatBridge Windows Dev'
)

$ErrorActionPreference = 'Stop'
$rootSubject = "$Subject Root"
$root = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object Subject -eq $rootSubject |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
if (-not $root -or $root.NotAfter -le (Get-Date).AddDays(30)) {
    $root = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $rootSubject `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -KeyExportPolicy NonExportable `
        -KeyUsage CertSign, CRLSign, DigitalSignature `
        -TextExtension @('2.5.29.19={critical}{text}ca=true&pathlength=1') `
        -NotAfter (Get-Date).AddYears(5)
}

$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $Subject -and $_.Issuer -eq $root.Subject } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
if (-not $certificate -or $certificate.NotAfter -le (Get-Date).AddDays(30)) {
    $certificate = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -Signer $root `
        -CertStoreLocation Cert:\CurrentUser\My `
        -NotAfter (Get-Date).AddYears(2)
}

$cer = Join-Path $env:TEMP 'WeChatBridge.Windows.Dev.cer'
$rootCer = Join-Path $env:TEMP 'WeChatBridge.Windows.Dev.Root.cer'
Export-Certificate -Cert $certificate -FilePath $cer -Force | Out-Null
Export-Certificate -Cert $root -FilePath $rootCer -Force | Out-Null
$trusted = Get-ChildItem Cert:\CurrentUser\TrustedPeople -ErrorAction SilentlyContinue |
    Where-Object Thumbprint -eq $certificate.Thumbprint
if (-not $trusted) {
    Import-Certificate -FilePath $cer -CertStoreLocation Cert:\CurrentUser\TrustedPeople -Confirm:$false | Out-Null
}
$rootTrusted = Get-ChildItem Cert:\CurrentUser\Root -ErrorAction SilentlyContinue |
    Where-Object Thumbprint -eq $root.Thumbprint
if (-not $rootTrusted) {
    $rootTrusted = Get-ChildItem Cert:\LocalMachine\Root, Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
        Where-Object Thumbprint -eq $root.Thumbprint
}
if (-not $rootTrusted) {
    Write-Warning "Root CA is not trusted. Import $rootCer into the Local Computer Root or Trusted People store before registering the MSIX."
}

Write-Output "Subject: $($certificate.Subject)"
Write-Output "Issuer: $($certificate.Issuer)"
Write-Output "Thumbprint: $($certificate.Thumbprint)"
Write-Output "Certificate store: Cert:\CurrentUser\My"
Write-Output "Exported public certificate: $cer"
Write-Output "Exported root certificate: $rootCer"
Write-Output "Before registration, import the leaf .cer into Local Computer > Trusted People and the root .cer into Local Computer > Root or Trusted People, then rerun register-dev.ps1."
