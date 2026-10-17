<#
.SYNOPSIS
  One-time setup for the 微信流 portable build on a USB drive.

.DESCRIPTION
  Fetches Obsidian from its official releases, puts it on the drive, points the
  obsidian:// protocol at it, makes sure the vault exists, and starts the app.

  Obsidian is NOT shipped in the ZIP. Its terms forbid redistributing the
  binary ("Licenses are non-transferrable and may not be distributed...", and
  separately forbid using it to provide a service for others), so the only
  compliant way to have Obsidian on a stick is to download it from the vendor
  on the machine that ends up using it. That is what this script does.

  Must be saved as UTF-8 *with* a BOM. Windows PowerShell 5.1 reads a BOM-less
  .ps1 as the system ANSI code page (GBK on a Chinese Windows), which silently
  mangles the Chinese strings below rather than failing. The .bat next to this
  file is pure ASCII for the same reason.

.PARAMETER SkipObsidian
  Set up everything except Obsidian. Chat records can always be imported by hand
  from the History page; Obsidian is only needed for archiving to a vault.

.PARAMETER ObsidianDirectory
  Where to put Obsidian on the drive. Defaults to <root>\Obsidian.
#>
[CmdletBinding()]
param(
    [switch]$SkipObsidian,
    [string]$ObsidianDirectory
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$appDirectory = Join-Path $root '微信流'
$vaultDirectory = Join-Path $appDirectory 'Vault'
$archiveFolder = Join-Path $vaultDirectory '31.微信会话'
if (-not $ObsidianDirectory) { $ObsidianDirectory = Join-Path $appDirectory 'Obsidian' }

function Step([string]$message) { Write-Host "==> $message" -ForegroundColor Cyan }
function Info([string]$message) { Write-Host "    $message" }
function Warn([string]$message) { Write-Host "    $message" -ForegroundColor Yellow }

function Assert-AppDirectory {
    if (-not (Test-Path -LiteralPath (Join-Path $appDirectory 'WeChatBridge.Windows.exe'))) {
        throw "没有找到微信流主程序。请确认压缩包已经完整解压，并且 FirstRun.bat 和「微信流」文件夹在同一级。"
    }
}

# --- vault -------------------------------------------------------------------
function Initialize-Vault {
    New-Item -ItemType Directory -Path $archiveFolder -Force | Out-Null
    if (-not (Test-Path -LiteralPath (Join-Path $vaultDirectory '.obsidian'))) {
        New-Item -ItemType Directory -Path (Join-Path $vaultDirectory '.obsidian') -Force | Out-Null
        Info "已在 Vault\.obsidian 建立配置目录。"
    }
    Info "知识库：$vaultDirectory"
}

# --- Obsidian ----------------------------------------------------------------
function Get-LatestObsidianInstaller {
    $releases = Invoke-RestMethod -Uri 'https://api.github.com/repos/obsidianmd/obsidian-releases/releases/latest' `
        -Headers @{ 'User-Agent' = 'WeChatBridge-Portable' } -UseBasicParsing
    $asset = $releases.assets |
        Where-Object { $_.name -match '^Obsidian-[\d.]+\.exe$' } |
        Select-Object -First 1
    if (-not $asset) { throw '在 Obsidian 官方发布页没有找到 Windows 安装包。' }
    [pscustomobject]@{ Version = $releases.tag_name.TrimStart('v'); Name = $asset.name; Url = $asset.browser_download_url; Size = $asset.size }
}

function Install-Obsidian {
    $exe = Join-Path $ObsidianDirectory 'Obsidian.exe'
    if (Test-Path -LiteralPath $exe) {
        Info "Obsidian 已存在：$exe"
        return $exe
    }

    Step '正在获取 Obsidian 官方下载信息…'
    $asset = Get-LatestObsidianInstaller
    Info "最新版 $($asset.Version)，$([math]::Round($asset.Size / 1MB)) MB"

    $download = Join-Path $env:TEMP $asset.Name
    if (-not (Test-Path -LiteralPath $download)) {
        Step "正在下载 $($asset.Name)…（约 $([math]::Round($asset.Size / 1MB)) MB，请耐心等待）"
        $ProgressPreference = 'SilentlyContinue'
        try {
            Invoke-WebRequest -Uri $asset.Url -OutFile $download -UseBasicParsing
        } finally { $ProgressPreference = 'Continue' }
    } else {
        Info "复用已下载的安装包：$download"
    }

    Step '正在安装 Obsidian 到 U 盘…'
    New-Item -ItemType Directory -Path $ObsidianDirectory -Force | Out-Null
    # NSIS silent install. /D must be last and unquoted; whether Obsidian's
    # MultiUser plugin honours it under /currentuser is not something the vendor
    # documents, so the result is verified below rather than assumed.
    $process = Start-Process -FilePath $download -ArgumentList @('/S', '/currentuser', "/D=$ObsidianDirectory") -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Obsidian 安装程序返回了 $($process.ExitCode)。" }

    if (-not (Test-Path -LiteralPath $exe)) {
        # /D= was ignored. Fall back to where the per-user install actually landed
        # and relocate it. Obsidian's own settings live under %APPDATA%\obsidian,
        # separately from the install directory, so moving it does not lose them.
        $default = Join-Path $env:LOCALAPPDATA 'Obsidian\Obsidian.exe'
        if (-not (Test-Path -LiteralPath $default)) {
            throw 'Obsidian 没有安装到预期位置，也没有出现在默认的按用户安装目录。请手动安装 Obsidian 后重试。'
        }
        Warn '/D= 被安装程序忽略了，改为从默认安装位置搬过来。'
        Remove-Item -LiteralPath $ObsidianDirectory -Recurse -Force -ErrorAction SilentlyContinue
        Move-Item -LiteralPath (Split-Path -Parent $default) -Destination $ObsidianDirectory -Force
        if (-not (Test-Path -LiteralPath $exe)) { throw '移动 Obsidian 失败。' }
    }
    Info "Obsidian 已安装到 $ObsidianDirectory"
    return $exe
}

function Register-ObsidianProtocol {
    $exe = Join-Path $ObsidianDirectory 'Obsidian.exe'
    if (-not (Test-Path -LiteralPath $exe)) { return }
    # Obsidian takes no positional arguments — "Obsidian.exe <vault> <note>" is a
    # feature request, not a feature — so obsidian:// is the only way in. The
    # installer registers that handler, and a copied-onto-a-stick install never
    # runs one. It is three HKCU values; write them only if nothing owns them.
    if (Test-Path 'HKCU:\Software\Classes\obsidian') {
        Info 'obsidian:// 已被注册，保持现状。'
        return
    }
    New-Item -Path 'HKCU:\Software\Classes\obsidian' -Force | Out-Null
    Set-ItemProperty -Path 'HKCU:\Software\Classes\obsidian' -Name '(default)' -Value 'URL:obsidian'
    New-ItemProperty -Path 'HKCU:\Software\Classes\obsidian' -Name 'URL Protocol' -Value '' -PropertyType String -Force | Out-Null
    New-Item -Path 'HKCU:\Software\Classes\obsidian\shell\open\command' -Force | Out-Null
    Set-ItemProperty -Path 'HKCU:\Software\Classes\obsidian\shell\open\command' -Name '(default)' -Value "`"$exe`" `"%1`""
    Info "已把 obsidian:// 指向 $exe"
}

# --- main --------------------------------------------------------------------
try {
    Step '微信流 · 便携版首次运行'
    Assert-AppDirectory

    Step '正在准备知识库…'
    Initialize-Vault

    if ($SkipObsidian) {
        Warn '已跳过 Obsidian 安装。聊天记录仍可在「记录」页手动导入。'
    } else {
        Install-Obsidian | Out-Null
        Register-ObsidianProtocol
    }

    Step '正在启动微信流…'
    Start-Process -FilePath (Join-Path $appDirectory 'WeChatBridge.Windows.exe') -WorkingDirectory $appDirectory
    Info '如果窗口没有出现，先检查「微信流\Data」目录是否可以写入。'
} catch {
    Write-Host ''
    Write-Host "首次运行失败：$($_.Exception.Message)" -ForegroundColor Red
    Write-Host '你可以直接双击「微信流\WeChatBridge.Windows.exe」使用核心功能，Obsidian 只是可选的归档目标。' -ForegroundColor Yellow
    Read-Host '按回车键退出'
    exit 1
}
