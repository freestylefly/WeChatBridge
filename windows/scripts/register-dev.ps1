[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallRoot,
    [string]$CertificateThumbprint,
    # Which of the two products is being registered. Only Subscription carries
    # the internetClient capability; Perpetual's manifest deliberately does not,
    # because its binaries contain no network code path to begin with.
    [ValidateSet('Perpetual', 'Subscription')]
    [string]$Edition = 'Perpetual'
)

$ErrorActionPreference = 'Stop'
$installRoot = [IO.Path]::GetFullPath($InstallRoot)
$installRoot = New-Item -ItemType Directory -Force -Path $installRoot | Select-Object -ExpandProperty FullName
$registrationLog = Join-Path $installRoot 'registration.log'
function Write-RegistrationLog([string]$message) {
    Add-Content -Path $registrationLog -Value ("{0:O} {1}" -f [DateTimeOffset]::UtcNow, $message)
}
trap {
    $hresult = ('0x{0:X8}' -f ($_.Exception.HResult -band 0xffffffff))
    Write-RegistrationLog "失败 $hresult $($_.Exception.Message)"
    throw
}

$helper = Join-Path $installRoot 'share-target\WeChatBridge.ShareTarget.exe'
$manifestDir = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\packaging\SparsePackage'))
$manifestPath = Join-Path $manifestDir 'AppxManifest.xml'
$packagePath = Join-Path $installRoot 'WeChatBridge.ShareTarget.msix'

if (-not (Test-Path $helper)) { throw "Share Target helper not found: $helper" }
if (-not (Test-Path (Join-Path $installRoot 'WeChatBridge.Windows.exe'))) { throw 'WPF host not found under InstallRoot.' }
if (-not (Test-Path $manifestPath)) { throw "Sparse package manifest not found: $manifestPath" }

# An AllowExternalContent package resolves the paths its manifest declares against the
# external location, not against the payload copy under WindowsApps. The logos therefore
# have to exist under the install root as well, or the shell cannot resolve the share
# target and silently leaves it out of the share sheet.
$assetSource = Join-Path $manifestDir 'Assets'
if (Test-Path $assetSource) {
    $assetTarget = Join-Path $installRoot 'Assets'
    New-Item -ItemType Directory -Force -Path $assetTarget | Out-Null
    Get-ChildItem -Path $assetSource -Force | ForEach-Object {
        Copy-Item -Path $_.FullName -Destination (Join-Path $assetTarget $_.Name) -Recurse -Force
    }
    Write-RegistrationLog "已同步图标资源到外部内容目录：$assetTarget"
}

if (-not $CertificateThumbprint) {
    $CertificateThumbprint = (Get-ChildItem Cert:\CurrentUser\My |
        Where-Object Subject -eq 'CN=WeChatBridge Windows Dev' |
        Sort-Object NotAfter -Descending | Select-Object -First 1).Thumbprint
}
if (-not $CertificateThumbprint) { throw 'Run new-dev-certificate.ps1 first.' }
$certificate = Get-ChildItem Cert:\CurrentUser\My\$CertificateThumbprint -ErrorAction SilentlyContinue
if (-not $certificate) { throw "Development certificate not found: $CertificateThumbprint" }

$packScript = Join-Path $PSScriptRoot 'pack-msix.ps1'

# pack-msix.ps1 takes no Version default on purpose: a default is one more
# place a stale number can survive, and an Appx Identity/Version that disagrees
# with the binaries underneath produces a share target that fails at run time.
# So read this edition's version out of the build properties — the same file the
# compiler read it from, so the two cannot drift.
#
# -Encoding UTF8 is required, not decorative: Directory.Build.props carries
# Chinese and has a BOM, and Windows PowerShell 5.1 decodes a file without one
# as the system ANSI code page (GBK on a Chinese Windows), which corrupts the
# XML outright and surfaces as "the props file is damaged".
[xml]$buildProps = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\Directory.Build.props') -Raw -Encoding UTF8
$version = ($buildProps.Project.PropertyGroup |
    Where-Object { $_.WeChatBridgeEdition -eq $Edition } |
    ForEach-Object { $_.Version } |
    Where-Object { $_ } |
    Select-Object -First 1)
if (-not $version) { throw "Directory.Build.props has no PropertyGroup for WeChatBridgeEdition='$Edition'." }
$apxVersion = [version]$version
& $packScript -Publisher 'CN=WeChatBridge Windows Dev' -Edition $Edition -Version "$apxVersion.0" -OutputPath $packagePath -ExternalContentDirectory $installRoot
$signTool = Get-Command signtool.exe -ErrorAction SilentlyContinue
if (-not $signTool) {
    $signTool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
}
if (-not $signTool) { throw 'signtool.exe was not found. Install the Windows SDK or add it to PATH.' }
$signToolPath = if ($signTool.FullName) { $signTool.FullName } else { $signTool.Source }
& $signToolPath sign /fd SHA256 /sha1 $CertificateThumbprint $packagePath
if ($LASTEXITCODE -ne 0) { throw "SignTool failed with exit code $LASTEXITCODE." }

$signature = Get-AuthenticodeSignature -FilePath $packagePath
if ($signature.Status -ne 'Valid') {
    throw "The MSIX signature is not trusted ($($signature.Status): $($signature.StatusMessage)). Trust the development certificate in Cert:\CurrentUser\Root and retry."
}

Get-AppxPackage -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in 'WeChatBridge.Windows.ShareTarget', 'ChatBridge.Windows.ShareTarget' } |
    Remove-AppxPackage -ErrorAction SilentlyContinue
try {
    Add-AppxPackage -Path $packagePath -ExternalLocation $installRoot
}
catch {
    $hresult = ('0x{0:X8}' -f ($_.Exception.HResult -band 0xffffffff))
    throw "Add-AppxPackage failed ($hresult): $($_.Exception.Message)"
}
$registered = Get-AppxPackage -Name 'WeChatBridge.Windows.ShareTarget' -ErrorAction SilentlyContinue
if (-not $registered) { throw 'Add-AppxPackage returned but the package is not registered.' }
Write-RegistrationLog "成功 $($registered.PackageFullName)"
Write-Output "Registered: $($registered.PackageFullName)"
