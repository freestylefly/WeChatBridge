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

Version 1.0.1 is the minimum supported installer version: older Windows application files used file version 1.0.0 even when their MSI version was 0.1.0. Publishing with a lower file version can make Windows Installer skip the application components during an upgrade. The builder checks all six application EXE/DLL file versions against the installer version, and registration checks that all six files exist before reporting installation success.

Requirements to build: .NET 10 SDK, Windows SDK (`makepri`, `makeappx`, `signtool`), WiX 6 and UI/Util/Bal/Netfx extensions, and an existing code-signing certificate. Set `-WixExtensionDirectory` and `-SigningThumbprint` to the local tooling and certificate. Only public `.cer` files are embedded; private signing material is rejected.

```powershell
./windows/scripts/build-installer.ps1 -Version 1.0.7 -WixExtensionDirectory <extensions-directory> -SigningThumbprint <certificate-thumbprint>
```

Output is under `windows/artifacts/dist`. Copy the Setup.exe to an Intel/AMD x64 PC running Windows 10 version 2004 or later, or Windows 11. No .NET installation or network connection is needed. Burn obtains administrator approval before installing a small, machine-wide certificate prerequisite. It imports only the public test certificates from an administrator-protected Program Files directory. The application's MSI and MSIX registration remain under the original user. Cancelling approval stops before application installation. Use the complete Setup.exe: the application MSI no longer attempts nested elevation from a deferred custom action.

Setup uses a Simplified Chinese interface, the application logo, one install action, and an application launch action after success. It hides the optional folder settings and license acceptance checkbox; the MIT license remains included in the installed files. The build explicitly embeds both MSIs and signs both the detached Burn engine and the reattached bundle. Certificate trust is a permanent, hidden prerequisite so uninstalling one user's application does not remove trust needed by another user's installation. Registration diagnostics persist under `%LOCALAPPDATA%\WeChatBridge\Logs\Install` even after an MSI rollback. For a cache-only verification without installing or upgrading the application, copy and rename Setup.exe into a separate directory, then run it with `/cache /quiet /log <log-file>` and check for both verified MSI payloads and exit code 0. A cache-only check does not validate MSI execution or Appx registration.

This local test package is self-signed, not signed by a publicly trusted publisher. Windows may display an unrecognized-publisher/SmartScreen prompt. Full fresh-machine install and actual WeChat sharing still require testing on the destination computer.
