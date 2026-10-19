<#
.SYNOPSIS
  Register 微信流 as a WeChat share target on this computer, from a portable
  installation on removable media.

.DESCRIPTION
  Portable counterpart of windows/packaging/msi/Register-ShareTarget.ps1. The
  MSI version runs inside a deferred custom action, so it reads its
  certificates from the installed payload and logs under %LOCALAPPDATA%. This
  one is launched by hand from the Entries page of a running portable app,
  which changes four things:

    * the log directory follows the drive instead of %LOCALAPPDATA%, with a
      %TEMP% fallback — a write-protected stick would otherwise fail at the very
      first New-Item, before anything has been attempted;
    * -SkipProcessKill, because Stop-Process on WeChatBridge.ShareTarget and
      WeChatBridge.Windows would kill the app whose button started this;
    * the package identity is deliberately the same as the installed build's,
      so the WeChat menu shows one 微信流 row rather than two;
    * no -CertificateTrustPrepared: nothing prepared the trust for us.

  Everything that makes registration actually correct is unchanged: Appx trust
  is machine-wide so only the certificate import elevates, the package is
  registered under the original user, and success is confirmed by launching the
  helper's own --registration-check and comparing the identity it reports — a
  package record on its own does not mean the executable got identity.
#>
[CmdletBinding()]
param(
    [string]$InstallRoot,
    [switch]$SkipProcessKill
)
$ErrorActionPreference = 'Stop'
if (-not $InstallRoot) { $InstallRoot = Split-Path -Parent $MyInvocation.MyCommand.Path }
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)

$logDirectory = Join-Path $InstallRoot 'Data\Logs\Install'
try {
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
} catch {
    # A read-only or full drive must not abort the registration before it starts.
    $logDirectory = Join-Path $env:TEMP 'WeChatBridge\Logs\Install'
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
}
$log = Join-Path $logDirectory ("register-sharetarget-{0}-{1}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $PID)

function Log([string]$m) { Add-Content -Path $log -Value ("{0:O} {1}" -f [DateTimeOffset]::UtcNow, $m) }

try {
    Log "user=$([Security.Principal.WindowsIdentity]::GetCurrent().User.Value) installRoot=$InstallRoot skipProcessKill=$($SkipProcessKill.IsPresent)"
    $msix = Join-Path $InstallRoot 'WeChatBridge.ShareTarget.msix'
    $certDir = Join-Path $InstallRoot 'certs'

    function Test-IsAdmin {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        return ([Security.Principal.WindowsPrincipal]$identity).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
    }

    $leafCer = Join-Path $certDir 'WeChatBridge.Windows.Dev.cer'
    $rootCer = Join-Path $certDir 'WeChatBridge.Windows.Dev.Root.cer'
    if (-not (Test-Path -LiteralPath $leafCer)) { throw 'The public signing certificate is missing.' }

    # Appx trust is machine-wide — AppXSVC runs as SYSTEM and reads only the
    # computer-level stores, so a user-level import is worth nothing to
    # Add-AppxPackage. Elevate the import alone and keep the per-user package
    # registration under the original user; a standard user must not end up
    # registering into the administrator's account.
    $trustNeeded = $false
    foreach ($check in @(
        @{ File = $leafCer; Store = 'Cert:\LocalMachine\TrustedPeople' },
        @{ File = $rootCer; Store = 'Cert:\LocalMachine\Root' }
    )) {
        if (Test-Path -LiteralPath $check.File) {
            $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($check.File)
            try {
                if (-not (Get-ChildItem $check.Store | Where-Object Thumbprint -eq $cert.Thumbprint)) { $trustNeeded = $true }
            } finally { $cert.Dispose() }
        }
    }
    if ($trustNeeded) {
        $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        if (Test-IsAdmin) {
            & $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -Command `
                "Import-Certificate -FilePath '$leafCer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople -Confirm:`$false | Out-Null; Import-Certificate -FilePath '$rootCer' -CertStoreLocation Cert:\LocalMachine\Root -Confirm:`$false | Out-Null"
            if ($LASTEXITCODE -ne 0) { throw "Certificate import failed: $LASTEXITCODE" }
        } else {
            $process = Start-Process -FilePath $powershell -Verb RunAs -WindowStyle Hidden -Wait -PassThru `
                -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command',
                    "Import-Certificate -FilePath '$leafCer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople -Confirm:`$false | Out-Null; Import-Certificate -FilePath '$rootCer' -CertStoreLocation Cert:\LocalMachine\Root -Confirm:`$false | Out-Null")
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

    if (-not $SkipProcessKill) {
        Get-Process -Name 'WeChatBridge.ShareTarget', 'WeChatBridge.Windows' -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
    }

    Get-AppxPackage -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in 'WeChatBridge.Windows.ShareTarget', 'ChatBridge.Windows.ShareTarget' } |
        Remove-AppxPackage -ErrorAction SilentlyContinue
    Add-AppxPackage -Path $msix -ExternalLocation $InstallRoot
    $registered = Get-AppxPackage -Name 'WeChatBridge.Windows.ShareTarget' -ErrorAction SilentlyContinue
    if (-not $registered) { throw 'Package registration did not stick.' }
    if ($registered.Status -ne 'Ok') { throw "Registered package is unhealthy: $($registered.Status)" }

    # A registration record alone does not grant identity to an incorrectly built
    # external EXE. Launch a side-effect-free probe as this same user.
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
        Log "identityCheck exit=$($process.ExitCode) output=$output stderr=$($stderr.GetAwaiter().GetResult())"
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
