[CmdletBinding()]
param(
    # Which product is under the directory being checked. Perpetual is the
    # buy-out build and must contain no networking code at all; Subscription is
    # the cloud build and must contain some.
    [ValidateSet('Perpetual', 'Subscription')]
    [string]$Edition = 'Perpetual',

    # Where to look. Defaults to every build output under windows/src, which is
    # what CI uses after a plain `dotnet build`. Point it at a publish folder
    # to check the shipped layout instead.
    [string]$PublishDirectory
)
$ErrorActionPreference = 'Stop'

# This file is deliberately ASCII-only, so it needs no UTF-8 BOM. Windows
# PowerShell 5.1 reads a BOM-less .ps1 as the system ANSI code page (GBK on a
# Chinese Windows), which is fine for ASCII and corrupts anything else. If you
# add Chinese text to this script, save it as UTF-8 with a BOM, the way
# portable/FirstRun.ps1 and packaging/SparsePackage/AppxManifest.xml are.

# Why this scans assembly bytes and not anything cheaper:
#
#   * deps.json is not evidence. It does not list framework assemblies at all,
#     so "System.Net.Http" appears zero times in every deps.json in this
#     repository today, while the subscription feature does not exist yet. A
#     check on it would pass no matter what we compile.
#   * Scanning the payload directory is not evidence either. A self-contained
#     publish ships System.Net.Http.dll as part of the shared framework whether
#     or not this product ever calls it.
#   * So the only thing worth scanning is our own code: WeChatBridge.*.dll and
#     .exe. ReadyToRun does not weaken this: cross-module R2R precompiles
#     method bodies, it does not pull in the callee's IL, and a reference from
#     our code to System.Net.Http is still a TypeRef row in our assembly.
#
# The needle is matched against a Latin-1 view of the file. .NET metadata
# strings live in the #Strings heap as UTF-8, but the characters we are looking
# for are ASCII, so Latin1 is a lossless byte-for-byte mapping for them and
# costs no transcoding. Reading the same bytes as UTF-16 (PowerShell's default
# for a bare ReadAllText on some code paths) would interleave zero bytes and
# miss every hit.

# One regex, not one needle. Every networking namespace and type name we could
# plausibly touch starts with System.Net. -- Http, Sockets, WebSockets,
# Security, Mail, WebRequest -- but System.Net.NetworkInformation does not go
# over a network, and lumping it in with the rest means the check reports the
# hardware-anchor reader in Licensing/IMachineFactsReader.cs (which calls
# NetworkInterface to read MAC addresses for offline licence recovery) as a
# networking violation. A check that fires on the one legitimate use teaches
# people to delete the check.
#
# So the scan extracts every System.Net.* namespace actually named in the
# assembly's own metadata and subtracts the ones below. What is left is the
# report, which is now precise: the exact namespaces, not "somewhere in four
# megabytes there was a string".
#
# The pattern stops at the namespace boundary. A type reference carries its
# full name (System.Net.Http.HttpClient), so the first character that cannot
# continue a namespace is the end of the one being asked about.
$NetworkNamespacePattern = 'System\.Net\.[A-Za-z0-9_.]*'

# The whole allow-list for the buy-out build. Keep it a list of exact
# namespaces, not a prefix: 'System.Net.' would permit everything at once,
# which is the same as permitting nothing.
$PerpetualAllowedNamespaces = @(
    # Local adapter enumeration. NetworkInterface.GetAllNetworkInterfaces()
    # reads the adapter list already in the driver and never opens a socket, so
    # it cannot reach the network. It is here because the licence layer records
    # a hardware anchor and needs MACs to survive a reinstall.
    #
    # It does read hardware identifiers, which is worth a line in the privacy
    # wording even though it is not a network path -- see design doc 10.11.
    #
    'System.Net.NetworkInformation'

    # The redemption client, which has now landed. This is the single named
    # exception to "the buy-out build never goes online", and it is the reason
    # this list is a list of exact namespaces rather than a boolean.
    #
    # It is confined to WeChatBridge.Windows.dll: the whole flow except the
    # verbs lives in Core, which still names nothing under System.Net.Http at
    # all, and EditionIsolationTests still asserts that by exact equality over
    # the Core assembly. The verbs are deliberately in the WPF assembly so that
    # the stronger structural claim about Core stays true -- see the comment on
    # that test's scope.
    #
    # Three namespaces rather than one, and the set is not negotiable upward:
    #
    #   System.Net.Http          HttpClient, SocketsHttpHandler, the request and
    #                            response types, HttpRequestException.
    #   System.Net.Http.Headers  ProductInfoHeaderValue and
    #                            MediaTypeWithQualityHeaderValue, set in the
    #                            transport's constructor. Unavoidable for any
    #                            real User-Agent or Accept header.
    #   System.Net.Primitives    NOT a type this build names. It is the
    #                            resolution scope the compiler emits for
    #                            HttpStatusCode, so it sits in the metadata as a
    #                            bare namespace string. Reading the TypeRef table
    #                            directly confirms this: the only TypeRef in the
    #                            assembly resolving through it is HttpStatusCode,
    #                            and nothing else is reachable that way. It is
    #                            listed because this check is textual and would
    #                            otherwise fire on a string that names no type.
    #
    # If you ever find yourself adding a FOURTH entry here, stop: that is a
    # product decision about what the buy-out build is allowed to reach, not a
    # test fix.
    'System.Net.Http'
    'System.Net.Http.Headers'
    'System.Net.Primitives'
)

# Assembly informational/assembly version is derived from <Version> in
# Directory.Build.props, which differs per edition. Finding it in the bytes is
# how this script proves it is looking at the edition it was asked about, and
# not at Perpetual's output under Subscription's name.
$EditionVersions = @{
    Perpetual    = '1.0.8'
    Subscription = '0.1.0'
}

function Resolve-RepoRoot {
    for ($directory = Get-Item -LiteralPath $PSScriptRoot; $directory -ne $null; $directory = $directory.Parent) {
        if (Test-Path -LiteralPath (Join-Path $directory.FullName 'windows')) { return $directory.FullName }
    }
    throw "Could not find the repository root above $PSScriptRoot."
}

function Get-ProductAssembly {
    param([string]$Root)

    if (-not $Root) { $Root = Join-Path (Resolve-RepoRoot) 'windows\src' }
    if (-not (Test-Path -LiteralPath $Root)) { throw "Assembly search root does not exist: $Root" }

    # Not -Include: -Include is silently ignored when it is combined with
    # -LiteralPath and no wildcard, so the previous form matched every file in
    # the tree (1039 of them, against 23 assemblies) and then required each one
    # to carry the edition's version. Filter on the leaf name instead.
    #
    # obj\ is skipped as well as ref\ and refint\. Intermediate output is not
    # what ships, and an obj copy left over from the other edition is exactly the
    # kind of stale file that makes this check fail for the wrong reason.
    $assemblies = @(
        Get-ChildItem -LiteralPath $Root -Recurse -File |
            Where-Object {
                ($_.Name -like 'WeChatBridge.*.dll' -or $_.Name -like 'WeChatBridge.*.exe') -and
                $_.FullName -notmatch '\\(ref|refint|obj)\\'
            } |
            Sort-Object -Property FullName -Unique
    )
    if ($assemblies.Count -eq 0) {
        throw "No WeChatBridge.* assemblies under $Root. A check that finds nothing proves nothing; build first."
    }
    $script:ProductSearchRoot = $Root
    return $assemblies
}

# A short, unambiguous name for an assembly. Not the leaf name: this repository
# produces two WeChatBridge.ShareTarget.dll and three WeChatBridge.Windows.Core.dll
# under different output folders, and "WeChatBridge.ShareTarget.dll does not carry
# version 0.1.0" is useless when four files could be meant.
function Get-AssemblyLabel {
    param([System.IO.FileInfo]$Assembly)
    if ($Assembly.FullName.StartsWith($script:ProductSearchRoot, [StringComparison]::OrdinalIgnoreCase)) {
        return $Assembly.FullName.Substring($script:ProductSearchRoot.Length).TrimStart('\', '/')
    }
    return $Assembly.FullName
}

# Returns the assembly's bytes as a Latin-1 string, which is a lossless
# byte-for-byte view for the ASCII metadata strings this script searches for
# and costs no transcoding. Reading the same bytes as UTF-16 (PowerShell's
# default on some code paths) would interleave zero bytes and miss every hit.
function Get-AssemblyText {
    param([System.IO.FileInfo]$Assembly)
    return [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($Assembly.FullName))
}

$expectedVersion = $EditionVersions[$Edition]
$assemblies = Get-ProductAssembly -Root $PublishDirectory
Write-Host "Checking $($assemblies.Count) product assemblies in the '$Edition' build for networking namespaces."

$offenders = @()
$seenNetworkNamespaces = @()
foreach ($assembly in $assemblies) {
    $text = Get-AssemblyText -Assembly $assembly

    foreach ($namespace in @([regex]::Matches($text, $NetworkNamespacePattern) | ForEach-Object { $_.Value } | Sort-Object -Unique)) {
        if ($seenNetworkNamespaces -notcontains $namespace) { $seenNetworkNamespaces += $namespace }
        if ($Edition -eq 'Perpetual' -and $PerpetualAllowedNamespaces -notcontains $namespace) {
            $offenders += "$(Get-AssemblyLabel -Assembly $assembly): $namespace"
        }
    }

    # Only the managed assembly carries the version. WeChatBridge.*.exe is the
    # apphost -- a native stub whose job is to find and load the .dll beside
    # it -- and it embeds no AssemblyVersion, so asking it for one fails on a
    # build that is in fact the right edition. The .dll is the thing that ships.
    if ($assembly.Extension -eq '.dll' -and
        $text.IndexOf($expectedVersion, [StringComparison]::Ordinal) -lt 0) {
        # -f binds tighter than +, so the format operator has to be parenthesised
        # on its own. Written the other way round it formats only the last string
        # literal and the placeholders reach the user as "{0} does not carry
        # version {1}", which is worse than no message at all.
        $message = "{0} does not carry version {1}, so this is not a {2} build. Check -p:WeChatBridgeEdition=$Edition on the build that produced it." -f (Get-AssemblyLabel -Assembly $assembly), $expectedVersion, $Edition
        throw $message
    }
}

if ($Edition -eq 'Perpetual') {
    if ($offenders.Count -gt 0) {
        throw ("The buy-out build references networking types. Its promise not to go online is structural " +
            "(design doc 7.3), so this is a packaging bug, not a runtime switch:" + [Environment]::NewLine +
            ($offenders | ForEach-Object { "  $_" } | Out-String))
    }
    if ($seenNetworkNamespaces.Count -gt 0) {
        Write-Host ("Allowed in the buy-out build: " + ($seenNetworkNamespaces -join ', ') +
            '. System.Net.NetworkInformation reads the local adapter list for the licence hardware anchor and never opens a socket; System.Net.Http is the one-shot redemption request, and nothing else is reachable.')
    }
    Write-Host 'OK: the buy-out build references no networking type beyond the two named exceptions.'
    Write-Host 'Note: Redeem/ is allowed to call the redemption endpoint once. That exception is named in the privacy wording and is not a general network path.'
    exit 0
}

# Subscription. Asserting the presence of networking code is what stops a
# mislabelled build from shipping as a working product: if the edition property
# did not take effect, Subscription would be byte-identical to Perpetual and
# would silently have no cloud features at all.
$subscriptionSources = @(Get-ChildItem -LiteralPath (Join-Path (Resolve-RepoRoot) 'windows\src') -Recurse -File -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '\\obj\\' -and $_.FullName -match '\\(Cloud|CloudQr|Billing)\\' })
if ($subscriptionSources.Count -gt 0 -and $seenNetworkNamespaces.Count -eq 0) {
    throw ("$($subscriptionSources.Count) subscription sources were compiled but no assembly references " +
        "any System.Net namespace, so the cloud feature cannot reach the service. The edition property did not take effect.")
}
if ($subscriptionSources.Count -eq 0) {
    Write-Host 'SKIPPED: no Cloud/, CloudQr/ or Billing/ sources exist yet, so there is nothing that should be networked (design doc 18.6, step 5).'
}
Write-Host 'OK: subscription build.'
exit 0
