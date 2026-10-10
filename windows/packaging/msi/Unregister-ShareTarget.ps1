[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Get-Process -Name 'WeChatBridge.ShareTarget', 'WeChatBridge.Windows' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Get-AppxPackage -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in 'WeChatBridge.Windows.ShareTarget', 'ChatBridge.Windows.ShareTarget' } |
    Remove-AppxPackage -ErrorAction SilentlyContinue

function Clear-AppContainerProfiles {
    $code = @"
using System;
using System.Runtime.InteropServices;
public static class UserenvNativeUnregister {
    [DllImport("userenv.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int DeleteAppContainerProfile(string pszAppContainerName);
}
"@
    if (-not ([System.Management.Automation.PSTypeName]'UserenvNativeUnregister').Type) {
        Add-Type -TypeDefinition $code -ErrorAction SilentlyContinue
    }

    $patterns = @('WeChatBridge.Windows.ShareTarget_*', 'ChatBridge.Windows.ShareTarget_*')
    $families = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

    $packagesRoot = Join-Path $env:LOCALAPPDATA 'Packages'
    if (Test-Path -LiteralPath $packagesRoot) {
        foreach ($pat in $patterns) {
            Get-ChildItem -LiteralPath $packagesRoot -Filter $pat -Directory -ErrorAction SilentlyContinue |
                ForEach-Object { [void]$families.Add($_.Name) }
        }
    }

    $mappingPath = 'HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Mappings'
    if (Test-Path -LiteralPath $mappingPath) {
        Get-ItemProperty "$mappingPath\*" -ErrorAction SilentlyContinue |
            Where-Object { $_.Moniker -match '^(wechatbridge|chatbridge)\.windows\.sharetarget_' } |
            ForEach-Object { [void]$families.Add($_.Moniker) }
    }
    $storagePath = 'HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Storage'
    if (Test-Path -LiteralPath $storagePath) {
        Get-ChildItem -LiteralPath $storagePath -ErrorAction SilentlyContinue |
            Where-Object { $_.PSChildName -match '^(wechatbridge|chatbridge)\.windows\.sharetarget_' } |
            ForEach-Object { [void]$families.Add($_.PSChildName) }
    }

    foreach ($family in $families) {
        $packageDir = Join-Path $packagesRoot $family
        if (Test-Path -LiteralPath $packageDir) {
            Get-ChildItem -LiteralPath $packageDir -Recurse -Force -ErrorAction SilentlyContinue |
                Where-Object { $_.Attributes -band [System.IO.FileAttributes]::ReparsePoint } |
                ForEach-Object {
                    try {
                        if ($_.PSIsContainer) {
                            [System.IO.Directory]::Delete($_.FullName, $false)
                        } else {
                            [System.IO.File]::Delete($_.FullName)
                        }
                    } catch {}
                }
            Remove-Item -LiteralPath $packageDir -Recurse -Force -ErrorAction SilentlyContinue
        }

        try {
            if (([System.Management.Automation.PSTypeName]'UserenvNativeUnregister').Type) {
                [void][UserenvNativeUnregister]::DeleteAppContainerProfile($family)
            }
        } catch {}

        if (Test-Path -LiteralPath $mappingPath) {
            Get-ChildItem -LiteralPath $mappingPath -ErrorAction SilentlyContinue | ForEach-Object {
                $m = (Get-ItemProperty -LiteralPath $_.PSPath -Name 'Moniker' -ErrorAction SilentlyContinue).Moniker
                if ($m -and $m.Equals($family, [StringComparison]::OrdinalIgnoreCase)) {
                    Remove-Item -LiteralPath $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue
                }
            }
        }
        $storageDir = Join-Path $storagePath $family
        if (Test-Path -LiteralPath $storageDir) {
            Remove-Item -LiteralPath $storageDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Clear-AppContainerProfiles
