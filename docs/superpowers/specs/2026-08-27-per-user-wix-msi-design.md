# Per-User WiX MSI Installer Design

## Goal

Publish a standards-based Windows x64 MSI alongside the existing portable ZIP.
The MSI installs QuotaGlass for the current user without elevation, includes the
.NET Desktop Runtime, supports normal Windows Installer upgrades and removal,
and provides a stable artifact contract for a later WinGet submission.

## Starting Point

The implementation starts from `origin/master` commit
`91ab4f4611fed22798e2fc3823ba9b7ab4d191bb`. The current application is a .NET
10 WPF tray executable. Release Please supplies versions to a Windows package
job that currently creates a framework-dependent, single-file portable ZIP and
one SHA-256 checksum.

The portable publish profile and ZIP contents remain unchanged. The installer
uses a separate publish profile and does not change application configuration,
provider behavior, or the `%APPDATA%\QuotaGlass` runtime-data location.

## Installer Project and Payload

Add `src/QuotaGlass.Installer/QuotaGlass.Installer.wixproj` as a standalone
SDK-style WiX project pinned to `WixToolset.Sdk/5.0.2`. The project is restored
and built explicitly because its payload must already exist. It is not added to
`QuotaGlass.slnx`, so the established solution restore, build, and test commands
remain valid without first publishing an installer payload.

Add `win-x64-self-contained.pubxml` beside the existing publish profile. It
produces one self-contained, single-file x64 `QuotaGlass.exe` with no symbols.
The existing `win-x64.pubxml` remains framework-dependent and unchanged.

The WiX project accepts two required MSBuild properties:

- `MsiVersion`, a three-part numeric Release Please version
- `PayloadDir`, the directory containing exactly `QuotaGlass.exe`

The build fails when either value is missing, the version is not valid for
Windows Installer, or the payload executable is absent. Its output name is
`QuotaGlass-vX.Y.Z-win-x64.msi`.

## MSI Identity, Scope, and Metadata

The package has one explicit stable upgrade code for the QuotaGlass product
family. Each MSI build receives a new product code, and Windows Installer
major-upgrade detection relates versions through the stable upgrade code.
Lower-version installs are
rejected. The upgrade schedules removal after `InstallInitialize` so a failed
upgrade can roll back the removal of the previous version.

The package is x64, English, compressed, and fixed to per-user scope. It uses
Windows Installer 5.0 and installs under:

```text
%LOCALAPPDATA%\Programs\QuotaGlass
```

Apps & Features displays `QuotaGlass`, its Release Please version, publisher
`QuotaGlass`, the product icon, install location, project URL, and an uninstall
entry for the current user. Modification is hidden because the package has one
required feature, while repair and uninstall remain available through standard
Windows Installer commands.

The MSI carries no executable or package signature. The accepted result is the
normal unknown-publisher warning.

## Installed Resources

The one required feature owns these resources:

- `QuotaGlass.exe` in the install directory
- a `QuotaGlass` shortcut in the current user's Start Menu Programs directory
- installer registration in Apps & Features through Windows Installer

No desktop shortcut is authored. No Run value is created during installation.
The installer never writes, removes, or migrates `%APPDATA%\QuotaGlass`.

The application component uses an installer-owned value under
`HKCU\Software\QuotaGlass\Installer` as its per-user key path. The shortcut
points to the installed executable and uses the product icon. Empty
installer-owned directories are removed when the component is removed.

## Running Application and Upgrade Behavior

Windows Installer 5.0 Restart Manager detects the installed `QuotaGlass.exe`
when it is in use, but local lifecycle verification showed that it classifies
the WPF tray process as critical and schedules a reboot during a silent major
upgrade. The package therefore uses an immediate, impersonated DTF action when
an MSI version is being upgraded or uninstalled. It enumerates same-named
processes but closes only the process whose executable path equals
`[INSTALLFOLDER]QuotaGlass.exe`. It signals the application's path-derived named
shutdown event so the normal shutdown coordinator can flush state, waits 15
seconds, and terminates that exact process only as a bounded fallback.

The action does not run on a clean first install or repair. An already-absent
process is accepted, the package does not request a reboot, and portable
QuotaGlass processes in other directories remain untouched. The action exists
only because the standard Restart Manager path did not satisfy the required
no-reboot running-upgrade behavior. QuotaGlass is not automatically relaunched.

## Autostart Cleanup

An uninstall must remove the current-user value:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run\QuotaGlass
```

An authored Windows Installer registry row cannot remove an unowned value only
when a component is uninstalled. The MSI therefore includes one narrowly scoped
immediate DTF custom action. On a complete uninstall that is not the removal
phase of a major upgrade, it inserts a temporary Registry table row for that
exact value before the standard `RemoveRegistryValues` action. Windows Installer
then performs the removal transactionally. An absent value is accepted. The
action does not run during install, repair, or major upgrade removal, and it
does not delete the shared Run key.

The process-stop and registry-row actions are limited to the two required
behaviors that the authored package tables did not satisfy: deterministic
no-reboot process handling and deletion of one unowned registry value on true
uninstall.

## Installer Verification Tools

Add focused PowerShell tools under `eng/installer/`:

- `Test-MsiMetadata.ps1` opens the built MSI read-only through the Windows
  Installer API and verifies product metadata, x64 architecture, per-user scope,
  version, stable upgrade identity, product code, install directory, component
  bitness, payload inventory, Start Menu shortcut, absence of a desktop
  shortcut, major-upgrade rows, and the bounded uninstall cleanup action.
- `Test-MsiLifecycle.ps1` performs silent install, running-process upgrade, and
  uninstall tests with verbose logs in a temporary directory. It verifies the
  executable, shortcut, Apps & Features entry, product-code replacement,
  autostart cleanup, installer-owned resource removal, and preservation of a
  unique settings sentinel. Its `finally` block restores any pre-existing Run
  value, removes only its sentinel, stops only its installed and portable test
  processes, and attempts MSI cleanup.

The scripts accept explicit MSI paths and expected versions. They do not read
credential files, provider response bodies, account identifiers, or live usage
values. Failure output points to temporary MSI logs without copying sensitive
runtime data into the repository.

## Pull Request Build Integration

Extend the existing Windows job in `.github/workflows/build.yml` after the
normal solution build, tests, and portable publish:

1. Restore the standalone WiX project.
2. Publish the self-contained MSI payload.
3. Verify the payload contains only `QuotaGlass.exe`.
4. Publish separately version-stamped `0.0.1` and `0.0.2` payloads, then build
   and inspect the matching installer versions.
5. Run the silent lifecycle test from `0.0.1` to `0.0.2` with QuotaGlass
   running.
6. Upload the `0.0.2` MSI with the existing build artifact.

The artificial versions are CI-only and exercise major-upgrade behavior without
depending on a published historical MSI. The job remains on `windows-latest`
and retains its existing required-check name.

## Release Packaging

The Release Please package job remains the only source of release versions. It
passes the same version properties to the solution build, portable publish, and
self-contained publish, and passes the Release Please version as `MsiVersion`
to WiX.

The release candidate contains exactly four files:

- `QuotaGlass-vX.Y.Z-win-x64.zip`
- `QuotaGlass-vX.Y.Z-win-x64.sha256`
- `QuotaGlass-vX.Y.Z-win-x64.msi`
- `QuotaGlass-vX.Y.Z-win-x64.msi.sha256`

The existing checksum continues to hash the ZIP. The new checksum hashes the
MSI. Both files use lowercase SHA-256, two spaces, the corresponding file name,
UTF-8 without a byte-order mark, and one final newline.

Before upload, the package job verifies DLL and executable versions, both
publish inventories, ZIP inventory, MSI metadata and inventory, and both
checksums. The self-contained payload is not included as a separate release
asset.

## Protected Finalization and Recovery

The protected `release` environment and manual candidate-acceptance gate remain
unchanged. Finalization downloads the exact four-file workflow artifact,
validates both checksums and the ZIP inventory, and reconciles all four assets
against the draft release.

During the Windows package job, each existing package becomes the canonical
candidate for its package/checksum pair. An existing checksum must verify that
package; if only the package exists, the job generates its missing checksum. A
checksum without its package is accepted only when it verifies the rebuilt
package. The canonical ZIP inventory and MSI metadata are validated before the
four-file candidate artifact is uploaded. Finalization uploads missing assets,
never overwrites an existing asset, and publishes only when the target, tag, and
exact asset set match.

Manual recovery still accepts only the manifest's current draft tag, resolves
that tag to a commit contained in `master`, and rebuilds that exact commit. It
then reconciles the rebuilt candidate with any canonical package pairs already
uploaded by the previous attempt. The same four-file validation and no-overwrite
rules apply to normal and recovery runs.

## Local OpenCode Validation

After automated lifecycle tests, install the locally built MSI into the current
interactive Windows user and launch the installed QuotaGlass executable while
the existing OpenCode process remains running. Confirm the installed application
stays alive through its first provider poll and normal tray initialization.
Do not inspect or report credential contents, account identifiers, response
bodies, or live quota values. Uninstall the test MSI afterward and verify the
local QuotaGlass data directory remains.

## Documentation

Update `README.md` so users can choose either the self-contained MSI or the
portable framework-dependent ZIP. State the MSI install scope, location,
unknown-publisher warning, and standard silent commands.

Update `docs/manual-test-checklist.md` with MSI metadata, clean install,
interactive launch, running upgrade, silent uninstall, autostart cleanup, and
settings-preservation checks. Keep the file as a reusable release template
rather than recording local credentials or usage data.

Update `docs/release-process.md` with the four-file candidate contract, WiX
build and version stamping, MSI acceptance, final asset reconciliation, and
recovery expectations. Add a short WinGet-readiness note that a later manifest
will consume the immutable versioned MSI URL and checksum.

## Security and Failure Handling

- WiX SDK and utility extension packages are pinned to `5.0.2`. WiX 7 is not
  used because its build requires explicit acceptance of a separate OSMF EULA.
- Existing GitHub Actions remain pinned to full commit SHAs.
- The MSI contains only the self-contained QuotaGlass executable and installer
  metadata.
- The process action targets only `QuotaGlass.exe`, runs only for an installed
  version or detected upgrade, and requests no reboot.
- The uninstall custom action touches only one named HKCU value and ignores an
  already-absent value.
- Lifecycle tests restore any pre-existing Run value and preserve all existing
  `%APPDATA%\QuotaGlass` files.
- Release jobs fail on unexpected payloads, metadata, checksums, asset names,
  tag targets, or existing asset contents.
- MSI return codes that require or initiate a reboot fail automated tests.
- No signing key, certificate, credential, provider response, or live user data
  enters the repository, workflow artifact, MSI, logs, or pull request.

## Verification

Completion requires:

- clean solution restore and Release build
- all xUnit tests passing
- unchanged framework-dependent portable publish inventory
- one-file self-contained publish inventory
- WiX project restore and Release builds for two versions
- MSI metadata verification for both versions
- silent clean install, running-process major upgrade, and silent uninstall
- Start Menu, Apps & Features, install-directory, Run-value, and settings checks
- local installed launch with the existing OpenCode process running
- YAML parsing and `actionlint`
- checksum and exact release-candidate inventory checks
- `git diff --check`, prohibited-character scan, and sensitive-artifact review
- independent specification and code-quality reviews
