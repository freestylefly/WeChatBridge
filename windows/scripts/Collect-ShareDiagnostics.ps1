[CmdletBinding()]
param([string]$OutputDirectory = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$report = New-Object System.Collections.Generic.List[string]
function Record([string]$text) { $report.Add($text.Replace($env:USERPROFILE, '%USERPROFILE%')) }
function Section([string]$title, [scriptblock]$action) {
    Record "`r`n=== $title ==="
    try { & $action } catch { Record "CHECK ERROR: $($_.Exception.Message)" }
}
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ShareDiagnosticToken {
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("advapi32.dll", SetLastError=true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError=true)] static extern bool GetTokenInformation(IntPtr token, int infoClass, out int value, int size, out int length);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    public static string Elevated(int pid) {
        IntPtr process = OpenProcess(0x1000, false, pid), token = IntPtr.Zero;
        if (process == IntPtr.Zero) return "Unknown";
        try {
            if (!OpenProcessToken(process, 8, out token)) return "Unknown";
            int value, length;
            return GetTokenInformation(token, 20, out value, 4, out length) ? (value != 0 ? "Yes" : "No") : "Unknown";
        } finally { if (token != IntPtr.Zero) CloseHandle(token); CloseHandle(process); }
    }
}
'@
Section 'System' {
    $os = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    Record "Collected=$(Get-Date -Format o)"
    Record "Windows=$($os.ProductName) DisplayVersion=$($os.DisplayVersion) Build=$($os.CurrentBuild).$($os.UBR)"
    Record "PowerShell=$($PSVersionTable.PSVersion) CollectorElevated=$([ShareDiagnosticToken]::Elevated($PID))"
}
$appRoot = Join-Path $env:LOCALAPPDATA 'WeChatBridge\App'
Section 'Installed files' {
    foreach ($relative in @('WeChatBridge.Windows.exe','WeChatBridge.Windows.dll','WeChatBridge.Windows.Core.dll',
        'share-target\WeChatBridge.ShareTarget.exe','share-target\WeChatBridge.ShareTarget.dll',
        'WeChatBridge.ShareTarget.msix','resources.pri','Assets\Square44x44Logo.png','Assets\Square150x150Logo.png')) {
        $path = Join-Path $appRoot $relative
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $file=Get-Item -LiteralPath $path
            Record "$relative Present=True Bytes=$($file.Length) Version=$($file.VersionInfo.FileVersion)"
        } else { Record "$relative Present=False" }
    }
}
Section 'Share component runtime identity (1.0.5 or later)' {
    $helper=Join-Path $appRoot 'share-target\WeChatBridge.ShareTarget.exe'
    if (-not (Test-Path -LiteralPath $helper)) { Record 'HelperMissing'; return }
    $version=(Get-Item -LiteralPath $helper).VersionInfo.FileVersion
    if ([version]$version -lt [version]'1.0.5.0') { Record 'Older build has no side-effect-free identity probe.'; return }
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$helper
    $start.Arguments='--registration-check'
    $start.UseShellExecute=$false
    $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true
    $start.RedirectStandardError=$true
    $process=[Diagnostics.Process]::Start($start)
    try {
        $stdout=$process.StandardOutput.ReadToEndAsync()
        $stderr=$process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) { $process.Kill(); throw 'Identity probe timed out.' }
        Record "Exit=$($process.ExitCode) Output=$($stdout.GetAwaiter().GetResult()) Error=$($stderr.GetAwaiter().GetResult())"
    } finally { $process.Dispose() }
}
Section 'Running WeChat and WeChatBridge processes' {
    $processes = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match '^(WeChat|Weixin|WeChatApp|WeChatBridge\.Windows|WeChatBridge\.ShareTarget)$' })
    if (-not $processes.Count) { Record 'No matching processes. Start WeChat before collecting.' }
    foreach ($process in $processes) {
        $path = ''; $version = ''; $errorText = ''
        try { $path=$process.Path; if ($path) { $version=(Get-Item -LiteralPath $path).VersionInfo.FileVersion } } catch { $errorText=$_.Exception.Message }
        Record "$($process.ProcessName) PID=$($process.Id) Elevated=$([ShareDiagnosticToken]::Elevated($process.Id)) Version=$version Path=$path Error=$errorText"
    }
}
Section 'Current user ShareTarget registration' {
    $packages=@(Get-AppxPackage -Name 'WeChatBridge.Windows.ShareTarget')
    Record "RegisteredPackageCount=$($packages.Count)"
    foreach($package in $packages) {
        Record "Package=$($package.PackageFullName) Status=$($package.Status) Location=$($package.InstallLocation)"
        $manifest = Get-AppxPackageManifest -Package $package.PackageFullName
        Record $manifest.OuterXml
    }
}
Section 'Public certificate trust' {
    foreach($check in @(
        @{ File='WeChatBridge.Windows.Dev.cer'; Store='Cert:\LocalMachine\TrustedPeople' },
        @{ File='WeChatBridge.Windows.Dev.Root.cer'; Store='Cert:\LocalMachine\Root' }
    )) {
        $path=Join-Path $appRoot ('certs\'+$check.File)
        if (-not (Test-Path -LiteralPath $path)) { Record "$($check.File) Missing"; continue }
        $cert=New-Object Security.Cryptography.X509Certificates.X509Certificate2($path)
        try { Record "$($check.File) Thumbprint=$($cert.Thumbprint) Trusted=$(Test-Path -LiteralPath ($check.Store+'\'+$cert.Thumbprint))" }
        finally { $cert.Dispose() }
    }
}
Section 'Recent registration diagnostics' {
    $directory=Join-Path $env:LOCALAPPDATA 'WeChatBridge\Logs\Install'
    $files=@(Get-ChildItem -LiteralPath $directory -Filter 'register-sharetarget-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 4)
    foreach($file in $files) { Record "Log=$($file.Name)"; Get-Content -LiteralPath $file.FullName -Tail 50 | ForEach-Object { Record $_ } }
}
Section 'Recent Appx deployment events for this application' {
    Get-WinEvent -LogName 'Microsoft-Windows-AppXDeploymentServer/Operational' -MaxEvents 150 -ErrorAction Stop |
        Where-Object { $_.Message -match 'WeChatBridge.Windows.ShareTarget' } | Select-Object -First 15 |
        ForEach-Object { Record "Time=$($_.TimeCreated) ID=$($_.Id) Level=$($_.LevelDisplayName) $($_.Message)" }
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
$directory=Join-Path $OutputDirectory "WeChatBridge-Diagnostics-$stamp"
New-Item -ItemType Directory -Path $directory | Out-Null
$report | Set-Content -LiteralPath (Join-Path $directory 'diagnostics.txt') -Encoding UTF8
$zip=Join-Path $OutputDirectory "WeChatBridge-Diagnostics-$stamp.zip"
Compress-Archive -LiteralPath (Join-Path $directory 'diagnostics.txt') -DestinationPath $zip
Write-Host "Diagnostics saved: $zip"
Write-Output $zip
