# Windows sparse MSIX

The package in `SparsePackage/` carries package identity and Share Target
registration. The executables remain outside the package:

```text
<install-root>/WeChatBridge.Windows.exe
<install-root>/share-target/WeChatBridge.ShareTarget.exe
<install-root>/resources.pri
<install-root>/Assets/
```

The share helper embeds `windows/share-target.manifest` with an `msix` identity matching
the package's publisher, name, and `Share.Hub` application ID. `pack-msix.ps1` copies the
generated resource index into the external installation directory as well as the MSIX.
`build-installer.ps1` verifies the embedded EXE identity, matching resource-index hashes,
and external visual assets before signing. After registration, the installer runs the
helper's side-effect-free `--registration-check` and rejects missing or mismatched runtime
identity; finding an Appx registration record alone is insufficient.

For local development:

1. Run `scripts/new-dev-certificate.ps1`.
2. Publish the WPF app and helper into the layout above.
3. Run `scripts/register-dev.ps1 -InstallRoot <install-root>`.

Before registration, import the leaf `.cer` printed by
`new-dev-certificate.ps1` into **Local Computer > Trusted People**, and import
the root `.cer` into **Local Computer > Root** or **Trusted People**, with
administrator rights. Then run the registration script.

The package is signed with a local development certificate. No generated
certificate or MSIX belongs in Git.

For a release or tester build, use the repository workflow
`.github/workflows/windows-sign.yml` with SignPath Foundation. Configure the
SignPath organization, project, signing policy, artifact configuration, API
token, and the exact MSIX publisher subject as repository variables and secrets
before dispatching it. The artifact configuration in this directory signs the
external WPF/helper binaries and the sparse MSIX in one ZIP bundle.

## Offline installer for another Windows PC

`windows/scripts/build-installer.ps1` produces a self-contained x64 MSI and Setup.exe with both the main app and ShareTarget runtime included. Setup has no runtime download step when built with `-SelfContained`.

Version 1.0.1 is the minimum supported Perpetual installer version: older Windows application files used file version 1.0.0 even when their MSI version was 0.1.0. Publishing with a lower file version can make Windows Installer skip the application components during an upgrade. The guard is scoped to Perpetual, whose install history it is about; Subscription has none and starts at 0.1.0. The builder checks all six application EXE/DLL file versions against the installer version, and registration checks that all six files exist before reporting installation success.

Requirements to build: .NET 10 SDK, Windows SDK (`makepri`, `makeappx`, `signtool`), WiX 6 and UI/Util/Bal/Netfx extensions, and an existing code-signing certificate. Set `-WixExtensionDirectory` and `-SigningThumbprint` to the local tooling and certificate. Only public `.cer` files are embedded; private signing material is rejected.

The product version comes from `windows/Directory.Build.props`, matched by edition. Do not pass `-Version`: the builder reads it, and passing one only re-asserts what the props file already says.

```powershell
./windows/scripts/build-installer.ps1 -WixExtensionDirectory <extensions-directory> -SigningThumbprint <certificate-thumbprint>
```

## Two editions

There are two products, not two tiers of one product: **Perpetual** (buy-out, no
network code path at all) and **Subscription** (the cloud product). `-Edition`
selects one and everything else follows from it:

| | Perpetual | Subscription |
| --- | --- | --- |
| `Directory.Build.props` `<Version>` | `1.0.8` | `0.1.0` |
| `<EditionToken>` | `Buyout` | `Subscription` |
| `<MsiPackageName>` (MSI `Package/@Name`) | 微信流（买断版） | 微信流（订阅版） |
| artifact names | `WeChatBridge-Buyout-1.0.8-*` | `WeChatBridge-Subscription-0.1.0-*` |
| MSIX capabilities | `runFullTrust`, `unvirtualizedResources` | the same, plus `internetClient` |
| portable ZIP | `WeChatBridge-Buyout-1.0.8-Windows-Portable.zip` | not built |

```powershell
./windows/scripts/build-installer.ps1 -Edition Subscription -WixExtensionDirectory <extensions-directory> -SigningThumbprint <certificate-thumbprint>
```

Every one of those values lives in the same conditioned `PropertyGroup` in
`Directory.Build.props`, and every reader matches on `WeChatBridgeEdition`
exactly. Taking "the first `<Version>` in the file" is not a harmless
shortcut: it returns Perpetual's every single time and raises nothing, so a
Subscription build would ship under the buy-out's version number.

`SparsePackage/AppxManifest.xml` ships in the **Perpetual** shape and does not
declare `internetClient` — those binaries contain no network code path, so the
manifest should not claim a capability that does not exist. `pack-msix.ps1
-Edition Subscription` inserts the capability into the staged copy only; the
file on disk is never edited. `internetClient` is not a restricted capability
and takes no `rescap:` prefix, unlike the two that stay restricted.

`UpgradeCode` is deliberately **shared** between the two editions, so they are
mutually exclusive on one machine: installing the second supersedes the first
and the Share Target registration path is unchanged. That is also why moving
between editions is uninstall-then-install — Subscription's `0.1.0` is *lower*
than an installed Perpetual `1.0.8`, and Windows Installer reports
`WIX_DOWNGRADE_DETECTED` rather than downgrading.

`build-bundle.ps1` takes the same `-Edition`; it recomputes the MSI and Setup
file names from it and refuses a `-Version` that does not match that edition.

## Signing certificates

Everything awkward about installing 微信流 on Windows traces back to one fact: the package is self-signed by default. A self-signed certificate is not in any trust store, so:

- the certificate has to be imported into `LocalMachine\TrustedPeople` and `LocalMachine\Root`, which is the only reason the bundle asks for administrator approval at all;
- that import is what `CertificateTrust.msi` exists for — a hidden, `Permanent="yes"` prerequisite package that deliberately survives uninstall, so one user's uninstall does not strip trust another user still needs;
- Windows shows an unrecognized-publisher / SmartScreen prompt, because there is no reputation behind the signature;
- the Burn engine has to be detached, signed separately, and reattached, since Burn will not run an unsigned inner engine.

Replacing the development certificate with a real OV code-signing certificate removes all four. That is the single highest-leverage change to the install experience, and it requires no code change: both `build-installer.ps1` and `windows/portable/build-portable.ps1` already accept `-SigningThumbprint`.

An EV certificate is not required. OV combined with RFC 3161 timestamping accumulates SmartScreen reputation the same way.

`new-dev-certificate.ps1` mints a self-signed root valid for five years whose private key stays on the build machine. Anything that ships that root to users — which both installers do, via `certs/` — is asking every downloader to trust that build machine's key for five years with no revocation path. `build-portable.ps1` therefore mints a fresh ephemeral root and leaf per build so a leak's blast radius is one ZIP.

Output is under `windows/artifacts/dist`. Copy the Setup.exe to an Intel/AMD x64 PC running Windows 10 version 2004 or later, or Windows 11. No .NET installation or network connection is needed. Burn obtains administrator approval before installing a small, machine-wide certificate prerequisite. It imports only the public test certificates from an administrator-protected Program Files directory. The application's MSI and MSIX registration remain under the original user. Cancelling approval stops before application installation. Use the complete Setup.exe: the application MSI no longer attempts nested elevation from a deferred custom action.

Setup uses a Simplified Chinese interface, the application logo, one install action, and an application launch action after success. It hides the optional folder settings and license acceptance checkbox; the MIT license remains included in the installed files. The build explicitly embeds both MSIs and signs both the detached Burn engine and the reattached bundle. Certificate trust is a permanent, hidden prerequisite so uninstalling one user's application does not remove trust needed by another user's installation. Registration diagnostics persist under `%LOCALAPPDATA%\WeChatBridge\Logs\Install` even after an MSI rollback. For a cache-only verification without installing or upgrading the application, copy and rename Setup.exe into a separate directory, then run it with `/cache /quiet /log <log-file>` and check for both verified MSI payloads and exit code 0. A cache-only check does not validate MSI execution or Appx registration.

This local test package is self-signed, not signed by a publicly trusted publisher. Windows may display an unrecognized-publisher/SmartScreen prompt. Full fresh-machine install and actual WeChat sharing still require testing on the destination computer.
