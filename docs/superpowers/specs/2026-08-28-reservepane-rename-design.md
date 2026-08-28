# ReservePane Clean-Break Rename Design

## Outcome

Rename the application and repository from QuotaGlass to ReservePane across every active product surface. ReservePane is a new product identity. It does not read, migrate, upgrade, close, remove, or otherwise interact with installations or runtime data owned by QuotaGlass.

The public description is:

> ReservePane is a Windows tray application that monitors Claude, Codex, OpenCode, and Ollama usage at a glance.

## Baseline

The rename starts from released `master` at `6b97e2153b6f878e920155dc962ceba486b66474`, tagged `v0.1.2`.

Before any rename changes:

- The Release Please workflow for `v0.1.2` reached a successful terminal state.
- No pull request was open.
- `dotnet restore QuotaGlass.slnx` succeeded.
- `dotnet restore src/QuotaGlass.Installer/QuotaGlass.Installer.wixproj` succeeded.
- The Release build succeeded with zero warnings and zero errors.
- All 505 tests passed.

## Identity Contract

| Surface | Required value |
|---|---|
| Product and repository | `ReservePane` |
| Repository URL | `https://github.com/medokin/ReservePane` |
| Executable | `ReservePane.exe` |
| Solution | `ReservePane.slnx` |
| Main namespace | `ReservePane` |
| Test assembly | `ReservePane.Tests` |
| Install directory | `%LOCALAPPDATA%\Programs\ReservePane` |
| Start Menu directory and shortcut | `ReservePane` |
| Runtime data directory | `%APPDATA%\ReservePane` |
| Autostart value | `ReservePane` |
| Installer marker | `HKCU\Software\ReservePane\Installer` |
| MSI UpgradeCode | `0E117091-74A9-47E2-A7E5-16C3720AC18D` |
| MSI application component GUID | `1105C941-2A6C-44A2-8C3E-9FA32AFEB314` |
| Shutdown signal prefix | `Local\ReservePane.InstallerShutdown` |
| Release assets | `ReservePane-vX.Y.Z-win-x64.*` |
| Branding files | `reservepane-logo.svg`, generated PNGs, and `reservepane.ico` |

## Clean-Break Boundary

ReservePane owns only ReservePane state and identities:

- Runtime paths start under `%APPDATA%\ReservePane` without probing `%APPDATA%\QuotaGlass`.
- Autostart reads, writes, and removes only the `ReservePane` Run value.
- The installer targets only `ReservePane.exe` at the exact installed path.
- Installer shutdown signals use only the ReservePane prefix.
- The installer receives a new UpgradeCode and application component GUID.
- No legacy constants, aliases, fallback paths, redirects, compatibility UI, or migration tests remain.
- Existing QuotaGlass tags, releases, and generated changelog entries remain immutable historical records.
- Historical implementation specs and plans may retain the old name when they describe historical work. They are not active product documentation.

## Architecture and Change Sequence

### 1. Mechanical codebase rename

Rename the solution, production project, test project, installer project, installer-actions project, directories, namespaces, XAML type references, project references, assembly metadata, friend assembly declaration, fixture paths, and publish profiles as one coherent mechanical change.

Build immediately after this step. This isolates missing namespace, linked-source, project-reference, and generated-XAML updates before runtime behavior or release automation changes are introduced.

### 2. Runtime identity

Replace product-facing strings and platform identities at their existing ownership points:

- `AppPaths` owns the ReservePane application-data directory.
- `AutostartService` owns the ReservePane Run value.
- `InstallerShutdownSignalName` owns the ReservePane signal prefix.
- WPF windows and tray integration own ReservePane titles, tooltip text, and the hidden hotkey-window name.
- Project metadata owns executable and product identity.

Provider behavior and application architecture remain unchanged.

### 3. Branding assets

Replace the Q-shaped logo with a status-pane-and-gauge symbol. The source SVG retains the existing dark rounded-square background `#161C23`, green gauge `#35C46A`, and grey structure `#8F9CAA`. The interior becomes a simple inset pane or frame containing the gauge arc and needle, without an R or P monogram.

The SVG remains the source of truth. The existing PowerShell asset pipeline generates PNGs at 16, 24, 32, 48, 64, 128, 256, 512, and 1024 pixels plus the multi-resolution ICO. Source, output, temporary profile, and cleanup names all use ReservePane.

Manual inspection covers every generated size on representative light and dark Windows backgrounds. The key checks are recognizable silhouette, clean frame spacing, gauge readability, and absence of a Q-like tail.

### 4. Installer identity

The WiX package becomes a fresh ReservePane product family:

- Product, manufacturer, summary, icon, feature, component, file, shortcut, binary, and custom-action identifiers use ReservePane naming.
- Install and Start Menu directories use ReservePane.
- ARP metadata points to the ReservePane repository.
- The installer marker uses `HKCU\Software\ReservePane\Installer`.
- The package uses the required new UpgradeCode and component GUID.
- The MSI filename is `ReservePane-v$(MsiVersion)-win-x64.msi`.
- Upgrade and uninstall process handling targets only an exact-path installed `ReservePane.exe`.
- Uninstall cleanup removes only the owned `ReservePane` autostart value.

The existing two-version lifecycle test structure remains, but it validates ReservePane `0.0.1` to `0.0.2`. It does not install or inspect QuotaGlass.

### 5. Build and release contract

All restore, build, test, publish, packaging, reconciliation, upload, download, and finalization paths use the renamed solution and projects. Portable publishing must produce exactly `ReservePane.exe`. Release packaging must produce exactly the versioned ZIP, ZIP checksum, MSI, and MSI checksum named in the identity contract.

Release Please uses package name `ReservePane` and generated branch `release-please--branches--master--components--ReservePane`. Release Please continues to own the manifest, `version.txt`, generated changelog, tags, and releases.

### 6. Documentation and repository metadata

README, security guidance, contributor instructions, release process, manual test checklist, branding documentation, commands, paths, URLs, and current screenshots describe ReservePane. Historical changelog entries and historical implementation records remain accurate to their original release context.

The GitHub repository rename is a deployment-gate operation, not an early implementation step. It happens only after the implementation pull request is green and immediately before squash merge. The repository description, topics, local remote, rulesets, merge settings, Actions permissions, release immutability, and protected release environment are verified after the rename.

## Testing Strategy

Behavioral identity changes use test-first development:

- Application paths resolve only to `%APPDATA%\ReservePane`.
- Autostart reads, writes, and removes only the ReservePane value.
- Shutdown signal names contain only the ReservePane prefix and retain deterministic hashing behavior.
- UI metadata and executable-resource tests load and expose ReservePane identity.
- Installer action tests target only ReservePane processes and registry values.

Pure file moves, namespace renames, XAML renames, workflow edits, documentation, and generated assets do not receive artificial source-text tests. They are verified through compilation, existing behavior tests, publish inventory checks, MSI metadata and lifecycle scripts, asset regeneration, manual visual inspection, `git diff --check`, and scoped repository searches.

## Verification

The implementation gate requires fresh successful runs of:

```powershell
dotnet restore ReservePane.slnx
dotnet restore src/ReservePane.Installer/ReservePane.Installer.wixproj
dotnet build ReservePane.slnx -c Release --no-restore
dotnet test ReservePane.slnx -c Release --no-build
dotnet publish src/ReservePane/ReservePane.csproj -c Release -p:PublishProfile=win-x64
./assets/branding/build-logo-assets.ps1
git diff --check
```

Additional verification includes:

- The framework-dependent publish directory contains only `ReservePane.exe`.
- The self-contained MSI payload contains only `ReservePane.exe`.
- MSI metadata uses the required product identity, UpgradeCode, component GUID, paths, and ARP URL.
- The ReservePane `0.0.1` to `0.0.2` lifecycle passes install, running-process upgrade, uninstall, shortcut cleanup, Apps & Features cleanup, data preservation, and no-reboot checks.
- Every generated PNG and ICO size passes manual visual inspection.
- Active source, tests, assets, workflows, installer scripts, README, SECURITY, AGENTS, and active release documentation contain no `QuotaGlass`, `quotaglass`, or old repository URL reference.
- Fixtures, screenshots, logs, and evidence contain no credentials, account identifiers, live responses, or sensitive data.

## Delivery

Implementation uses focused Conventional Commits on `codex/rename-reservepane`. The pull request title is `feat(branding): rename quotaglass to reservepane`. Its body describes the user-visible outcome, links issue #34, records verification commands and results, includes representative icon screenshots, and ends with `Created with Codex`.

After the pull request is green, the repository is renamed at the deployment gate, settings are revalidated, invalidated checks are rerun if necessary, and the pull request is squash merged. The subsequent Release Please pull request and protected release flow must publish only ReservePane artifacts.
