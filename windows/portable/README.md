# Windows portable build

The USB-stick distribution: no installer, no `%LOCALAPPDATA%`, no .NET
prerequisite. `windows/packaging/` builds the installed one; this builds the
one you carry.

## Layout

`build-portable.ps1` produces `windows/artifacts/portable/` and packs it into
`windows/artifacts/WeChatBridge-<version>-Windows-Portable.zip`:

```text
<zip root>\
    FirstRun.bat          ASCII on purpose — cmd reads .bat in the ANSI code page
    FirstRun.ps1          UTF-8 WITH BOM — PS 5.1 otherwise reads it as GBK
    使用说明.txt
    LICENSE.txt
    SHA256SUMS.txt
    微信流\                            <- the portable root
        WeChatBridge.Windows.exe / .dll
        share-target\...
        Assets\  Resources\  resources.pri
        WeChatBridge.ShareTarget.msix
        certs\WeChatBridge.Windows.Dev.cer
        certs\WeChatBridge.Windows.Dev.Root.cer
        Register-ShareTarget.ps1  Unregister-ShareTarget.ps1
        portable.flag                <- the sentinel that selects portable mode
        Vault\.obsidian\  Vault\31.微信会话\
        Obsidian\                    <- installed by FirstRun.ps1
        Data\                        <- created at run time
            Inbox\ Config\ Skills\ SkillBackups\
```

The portable root **is** the directory holding the executable. `AppLayout`
resolves it as `AppContext.BaseDirectory` and derives `Data\` from it, so
inserting an `app\` level between them silently strands the data one directory
up.

## Building

```powershell
./windows/portable/build-portable.ps1 -Version 1.0.8
```

`Version` must match `windows/Directory.Build.props` — the same drift guard as
`build-installer.ps1`. `SigningThumbprint` defaults to the newest
`CN=WeChatBridge Windows Dev` certificate in `Cert:\CurrentUser\My`; pass a real
code-signing certificate for anything you distribute.

Tooling comes from the same place as the installed build: `makepri`,
`makeappx`, `signtool` and `mt.exe` on `PATH` or in a Windows Kits `bin`
directory. `mt.exe` is required by `Test-SharePackage.ps1` for the publisher
comparison and is present in `Microsoft.Windows.SDK.BuildTools`, so the full
2 GB SDK is not needed. Under Git Bash, set `MSYS_NO_PATHCONV=1` and
`MSYS2_ARG_CONV_EXCL='*'` or `signtool /fd` is mangled into a path.

Everything else is reused rather than reimplemented: `pack-msix.ps1`,
`Test-SharePackage.ps1`, `new-dev-certificate.ps1`.

Two steps in it are not optional:

- `-p:ApplicationManifest=<generated manifest>` on the ShareTarget publish,
  baking the signing certificate's subject into `msix@publisher`. Without it
  `Test-SharePackage.ps1`'s `mt.exe` comparison rejects the helper.
- Copying all of `SparsePackage/Assets/*.png` into the payload.
  `pack-msix.ps1` copies `resources.pri` and nothing else; without the visual
  assets the manifest's `Logo` / `Square44x44Logo` / `Square150x150Logo`
  dangle and the shell silently declines to enumerate the share target —
  registration succeeds and the menu row never appears.

`-SkipPublish` reuses an existing payload for iterating on the layout.
`-SkipZip` leaves the staged directory without packing.

## How portable mode is decided

`Core/AppLayout.cs` reads a `portable.flag` next to the executable. Everything
else follows from it:

| | installed | portable |
| --- | --- | --- |
| `DataRoot` | `%LOCALAPPDATA%\WeChatBridge` | `<portable root>\Data` |
| `CoordinationSuffix` | `""` | `-P` + hash of the root |
| `Vault` | `null` | `<portable root>\Vault` |

The parent-directory walk that finds the root is gated on the directory being
named `share-target` (the helper's own directory). The unconditional
self-then-parent probe used by `AppLogos.PathFor` is harmless there — a second
image lookup — but here it decides where *all* user data goes, and a stray
marker anywhere above an installed payload would strand its Inbox on a drive
that is not there. `AMarkerAboveADifferentlyNamedDirectoryIsIgnored` guards it.

### Coordination names

Every mutex, named pipe, event and the Run-key value carry the suffix. Without
it a portable copy and an installed copy on the same machine evict each other
in silence: the second launch creates its Inbox, loses the single-instance
mutex, and exits with no window and no message — and that is exactly the
audience most likely to have both.

Installed builds keep an empty suffix, so their names are unchanged and an
upgrade cannot orphan a running instance.

## Share target

The WeChat share menu needs package identity, which a copied-onto-a-stick
build does not have. The Entries page offers a one-time registration per
computer; it is off by default because it costs an administrator prompt.

`Register-ShareTarget.ps1` here is a fork of `packaging/msi/Register-ShareTarget.ps1`
with four changes, and everything that makes registration actually *correct* is
unchanged — Appx trust is machine-wide so only the certificate import
elevates, the package registers under the original user, and success is
confirmed by running the helper's own `--registration-check` and comparing the
identity it reports.

1. **Log directory** follows the drive, with a `%TEMP%` fallback. It is a
   `New-Item -Force` under `$ErrorActionPreference = 'Stop'`, so a
   write-protected stick would otherwise fail on the very first line.
2. **`-SkipProcessKill`.** The MSI version's `Stop-Process -Force` would kill
   the app whose button started it.
3. **No `-CertificateTrustPrepared`** — nothing prepared the trust for us.
4. **Same package identity as the installed build, deliberately.** The WeChat
   menu shows one 微信流 row rather than two. Registering from the stick takes
   the share entry away from an installed copy, and the UI says so.

`ShareRegistrationService` launches the script **without** elevation. Adding
`Verb = "runas"` would run the whole registration as administrator, and on a
standard-user machine the per-user package would land in the wrong hive. The
script elevates the certificate import by itself.

The drive's letter is recorded in `Data\Logs\Install\ShareRegistration.json`.
A sparse package registered with `-ExternalLocation` binds to whatever path the
drive had at that moment, and nothing in the package reports that the path
moved — reading `Package.InstalledLocation` back was not reliable enough to
depend on. The marker travels with the drive it describes.

## Obsidian

Not shipped. Its terms forbid redistributing the binary:

> Licenses are non-transferrable and may not be **distributed**, sold, rented,
> leased or otherwise assigned or transferred to any third party(ies).
>
> Customer shall not … **license, sub-license, sell, transfer, distribute or
> share the Services or Software or make any of them available for access by
> third parties** …

`FirstRun.ps1` downloads the installer from `obsidianmd/obsidian-releases` on
the machine that will use it, which is the only compliant way to have Obsidian
on a stick. The installer is ~326 MB and there is no official portable build.

`Obsidian.exe "<vault>" "<note>"` does **not** work — positional arguments are
a long-standing feature request, never implemented. `obsidian://` is the only
route, and the protocol is registered by Obsidian's *installer*, which a stick
never runs. `Services/ObsidianProtocol.cs` writes the three HKCU values itself,
lazily, and only when nothing already owns the protocol, so
`MainViewModel.OpenInObsidian` needs no change.

Whether NSIS's `/D=<dir>` is honoured under Obsidian's MultiUser plugin
(`/currentuser`, `/allusers`) is not documented by the vendor, so `FirstRun.ps1`
verifies where the executable actually landed and falls back to relocating the
default per-user install.

## Tests

`AppLayoutTests` exercises `AppLayout.Resolve(appDirectory, installedDataRoot)`,
never `AppLayout.Current`. Every other suite in this repo passes its
directories explicitly; resolving the singleton in a test host would make these
the first tests to touch it, in a directory that is the test bin folder, with no
way to vary the root between cases.

`LayoutWiringTests` asserts wiring against the repository's own source, in the
spirit of `PackagingAssetsTests`:

- no file outside a small allowlist resolves `SpecialFolder.LocalApplicationData`
  — the guard against a fourth data root creeping back in;
- `portable.flag` is named only in `AppLayout.cs`, so the build script's copy
  cannot drift from the resolver's;
- every coordination name carries the suffix;
- `FirstRun.ps1` ships with a BOM.

## Known limits

- x64 only, same as the installed build.
- Removing the drive while the app is running can kill the process
  unrecoverably: `PublishReadyToRun` only precompiles, so lazy JIT still
  demand-pages from the PE on the drive. The share-target keeper never stops its
  dispatcher. Exit the app before unplugging; `使用说明.txt` says so.
- exFAT or NTFS only. FAT32's 4 GB per-file limit hits large chat archives, and
  its 2-second timestamp granularity interacts with `PruneStaging`'s
  30-minute threshold.
- No sign-in startup: the Run key would hold a removable drive's path.
