[CmdletBinding()]
param(
    [string]$Publisher = 'CN=WeChatBridge Windows Dev',
    [string]$Version = '1.0.8.0',
    [string]$ExternalContentDirectory,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($output)
if (-not $ExternalContentDirectory) { $ExternalContentDirectory = $outputDirectory }
$ExternalContentDirectory = [IO.Path]::GetFullPath($ExternalContentDirectory)
$sourceDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\packaging\SparsePackage'))
$stageDirectory = Join-Path ([IO.Path]::GetTempPath()) "WeChatBridge-SparsePackage-$([Guid]::NewGuid().ToString('N'))"

if ([string]::IsNullOrWhiteSpace($Publisher)) { throw 'Publisher must not be empty.' }
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "Invalid MSIX version: $Version" }
if (-not (Test-Path (Join-Path $sourceDirectory 'AppxManifest.xml'))) { throw 'Sparse package manifest was not found.' }

function Resolve-SdkTool([string]$name) {
    $command = Get-Command $name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $candidate = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\$name" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if ($candidate) { return $candidate.FullName }
    throw "$name was not found. Install the Windows SDK or add it to PATH."
}

try {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    New-Item -ItemType Directory -Force -Path $stageDirectory | Out-Null
    Get-ChildItem -Path $sourceDirectory -Force | ForEach-Object {
        Copy-Item -Path $_.FullName -Destination (Join-Path $stageDirectory $_.Name) -Recurse -Force
    }

    $manifestPath = Join-Path $stageDirectory 'AppxManifest.xml'
    [xml]$manifest = Get-Content -Raw -Encoding UTF8 $manifestPath
    $manifest.Package.Identity.Publisher = $Publisher
    $manifest.Package.Identity.Version = $Version
    $manifest.Save($manifestPath)

    # A package without a resources index registers cleanly but does not reach the share
    # sheet, even though every name in the manifest is a literal. Ship the generated
    # resources.pri so the shell can resolve the target the same way it resolves the
    # packaged share targets it already lists.
    $makePri = Resolve-SdkTool 'makepri.exe'
    $priConfigPath = Join-Path $stageDirectory 'priconfig.xml'
    & $makePri createconfig /cf $priConfigPath /dq lang-zh-CN /o | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "MakePri createconfig failed with exit code $LASTEXITCODE." }
    $priPath = Join-Path $stageDirectory 'resources.pri'
    & $makePri new /pr $stageDirectory /cf $priConfigPath /of $priPath /in $manifest.Package.Identity.Name /o | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "MakePri new failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path $priPath)) { throw "MakePri did not produce resources.pri." }
    Remove-Item -LiteralPath $priConfigPath -Force -ErrorAction SilentlyContinue

    $makeAppx = Resolve-SdkTool 'makeappx.exe'
    & $makeAppx pack /d $stageDirectory /p $output /nv /o
    if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed with exit code $LASTEXITCODE." }
    # Sparse-package resource lookup uses the external location too. The generated
    # PRI must match the one in this MSIX; copying assets alone is insufficient.
    New-Item -ItemType Directory -Force -Path $ExternalContentDirectory | Out-Null
    Copy-Item -LiteralPath $priPath -Destination (Join-Path $ExternalContentDirectory 'resources.pri') -Force
}
finally {
    if (Test-Path $stageDirectory) {
        # Resolve and constrain the recursive cleanup to this task's temporary folder.
        $resolvedStage = [IO.Path]::GetFullPath($stageDirectory)
        $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
        if (-not [IO.Path]::GetDirectoryName($resolvedStage).Equals($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
            -not [IO.Path]::GetFileName($resolvedStage).StartsWith('WeChatBridge-SparsePackage-', [StringComparison]::Ordinal)) {
            throw 'Refusing to clean an unexpected package staging directory.'
        }
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Output "Packed: $output"
Write-Output "Publisher: $Publisher"
Write-Output "Version: $Version"
