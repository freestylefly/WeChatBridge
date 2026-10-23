[CmdletBinding()]
param([string]$InstallRoot, [switch]$TrustCertificateOnly, [switch]$CertificateTrustPrepared)

$ErrorActionPreference = 'Stop'
if (-not $InstallRoot) {
    $InstallRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$logDirectory = Join-Path $env:LOCALAPPDATA 'WeChatBridge\Logs\Install'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$log = Join-Path $logDirectory ("register-sharetarget-{0}-{1}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $PID)

function Log([string]$m) {
    Add-Content -Path $log -Value ("{0:O} {1}" -f [DateTimeOffset]::UtcNow, $m)
}

try {
    Log "user=$([Security.Principal.WindowsIdentity]::GetCurrent().User.Value) installRoot=$InstallRoot trustOnly=$TrustCertificateOnly"
    $msix = Join-Path $InstallRoot 'WeChatBridge.ShareTarget.msix'
    $certDir = Join-Path $InstallRoot 'certs'

    function Test-IsAdmin {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        return ([Security.Principal.WindowsPrincipal]$identity).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
    }

    $leafCer = Join-Path $certDir 'WeChatBridge.Windows.Dev.cer'
    $rootCer = Join-Path $certDir 'WeChatBridge.Windows.Dev.Root.cer'
    if ($TrustCertificateOnly) {
        if (-not (Test-IsAdmin)) { throw 'Certificate trust requires administrator privileges.' }
        if (-not (Test-Path -LiteralPath $leafCer)) { throw 'The public signing certificate is missing.' }
        Import-Certificate -FilePath $leafCer -CertStoreLocation Cert:\LocalMachine\TrustedPeople -Confirm:$false | Out-Null
        if (Test-Path -LiteralPath $rootCer) {
            Import-Certificate -FilePath $rootCer -CertStoreLocation Cert:\LocalMachine\Root -Confirm:$false | Out-Null
        }
        exit 0
    }

    # Appx trust is machine-wide. Elevate only certificate import, then register
    # under the original user; a standard user must not install into the admin account.
    $trustNeeded = $false
    foreach ($check in @(
        @{ File = $leafCer; Store = 'Cert:\LocalMachine\TrustedPeople' },
        @{ File = $rootCer; Store = 'Cert:\LocalMachine\Root' }
    )) {
        if (Test-Path -LiteralPath $check.File) {
            $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new($check.File)
            try {
                if (-not (Get-ChildItem $check.Store | Where-Object Thumbprint -eq $cert.Thumbprint)) { $trustNeeded = $true }
            } finally { $cert.Dispose() }
        }
    }
    if ($trustNeeded) {
        if ($CertificateTrustPrepared) {
            throw 'Signing certificate is not trusted. Run the complete Setup.exe to prepare certificate trust before MSI installation.'
        }
        $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" -InstallRoot "{1}" -TrustCertificateOnly' -f $PSCommandPath, $InstallRoot
        if (Test-IsAdmin) {
            & $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File $PSCommandPath -InstallRoot $InstallRoot -TrustCertificateOnly
            if ($LASTEXITCODE -ne 0) { throw "Certificate import failed: $LASTEXITCODE" }
        } else {
            $process = Start-Process -FilePath $powershell -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -Wait -PassThru
            if ($process.ExitCode -ne 0) { throw "Certificate import failed or was cancelled: $($process.ExitCode)" }
        }
    }
    $signature = Get-AuthenticodeSignature -FilePath $msix
    if ($signature.Status -ne 'Valid') { throw "MSIX signature is not trusted: $($signature.Status)" }

    foreach ($applicationFile in @(
        'WeChatBridge.Windows.exe', 'WeChatBridge.Windows.dll', 'WeChatBridge.Windows.Core.dll',
        'share-target\WeChatBridge.ShareTarget.exe', 'share-target\WeChatBridge.ShareTarget.dll',
        'share-target\WeChatBridge.Windows.Core.dll', 'resources.pri',
        'Assets\Square44x44Logo.png', 'Assets\Square150x150Logo.png'
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $InstallRoot $applicationFile) -PathType Leaf)) {
            throw "Installed application file is missing: $applicationFile"
        }
    }

    Get-Process -Name 'WeChatBridge.ShareTarget', 'WeChatBridge.Windows' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    # Also sweep the short-lived 1.0.7 ChatBridge.* registration — same
    # publisher, so its share-menu row would linger next to 微信流's.
    Get-AppxPackage -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in 'WeChatBridge.Windows.ShareTarget', 'ChatBridge.Windows.ShareTarget' } |
        Remove-AppxPackage -ErrorAction SilentlyContinue
    Add-AppxPackage -Path $msix -ExternalLocation $InstallRoot
    $registered = Get-AppxPackage -Name 'WeChatBridge.Windows.ShareTarget' -ErrorAction SilentlyContinue
    if (-not $registered) { throw 'Package registration did not stick.' }
    if ($registered.Status -ne 'Ok') { throw "Registered package is unhealthy: $($registered.Status)" }
    # A registration record alone does not grant identity to an incorrectly built
    # external EXE. Launch a side-effect-free probe as this same installing user.
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $InstallRoot 'share-target\WeChatBridge.ShareTarget.exe'
    $start.Arguments = '--registration-check'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) { $process.Kill(); throw 'Share identity check timed out.' }
        $output = $stdout.GetAwaiter().GetResult()
        $errors = $stderr.GetAwaiter().GetResult()
        Log "identityCheck exit=$($process.ExitCode) output=$output stderr=$errors"
        if ($process.ExitCode -ne 0) { throw 'Share component did not acquire package identity.' }
        $identity = $output | ConvertFrom-Json
        if ($identity.PackageFullName -cne $registered.PackageFullName -or
            $identity.ApplicationUserModelId -cne ($registered.PackageFamilyName + '!Share.Hub')) {
            throw 'Share component acquired an unexpected package/application identity.'
        }
    } finally { $process.Dispose() }
    Log "registered $($registered.PackageFullName) at $InstallRoot"
}
catch {
    Log "FAILED $($_.Exception.ToString()) $($_.ErrorDetails.Message)"
    throw
}
