# Per-User WiX MSI Installer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a self-contained per-user Windows x64 MSI, exercise its full lifecycle, and publish it beside the existing portable ZIP through the Release Please workflow.

**Architecture:** A standalone WiX 5 project consumes a separate self-contained single-file publish. PowerShell verification tools inspect the resulting MSI and exercise clean install, running-process major upgrade, and uninstall. Existing Windows CI builds both installer test versions, while Release Please builds the versioned MSI and finalizes an exact four-asset release.

**Tech Stack:** .NET 10, C# 14, WPF, WiX Toolset SDK 5.0.2, WixToolset.Util.wixext 5.0.2, PowerShell 7, Windows Installer 5.0, GitHub Actions

**Spec:** `docs/superpowers/specs/2026-08-27-per-user-wix-msi-design.md`

## Global Constraints

- Work in `E:\ai-status\.worktrees\issue-18-wix-msi` on `codex/issue-18-wix-msi`, based on `91ab4f4611fed22798e2fc3823ba9b7ab4d191bb`.
- Pin `WixToolset.Sdk` and `WixToolset.Util.wixext` to exact version `5.0.2`.
- Do not use WiX 7 because its build requires explicit acceptance of a separate OSMF EULA.
- Preserve `src/QuotaGlass/Properties/PublishProfiles/win-x64.pubxml` and the portable ZIP contract unchanged.
- The MSI payload must be one self-contained `QuotaGlass.exe` for Windows x64.
- Install per-user under `%LOCALAPPDATA%\Programs\QuotaGlass` without elevation.
- Keep `%APPDATA%\QuotaGlass` settings and logs through install, upgrade, and uninstall.
- Do not create a desktop shortcut or enable Start with Windows during install.
- Use an immediate, impersonated WiX utility action to stop `QuotaGlass.exe` only because lifecycle verification proved that standard Restart Manager classifies the running WPF tray process as critical and schedules a reboot.
- The only planned custom action removes the single current-user `QuotaGlass` Run value on a true uninstall because Windows Installer cannot express that operation in standard registry tables.
- Do not sign the executable or MSI.
- Release Please output is the only release version source.
- Third-party GitHub Actions remain pinned to full commit SHAs.
- Commits use Conventional Commit format and include `Created with Codex` in the body.
- Repository text must not contain the em dash character.

---

### Task 1: Self-Contained Payload and WiX Package

**Files:**
- Create: `src/QuotaGlass/Properties/PublishProfiles/win-x64-self-contained.pubxml`
- Create: `src/QuotaGlass.Installer/QuotaGlass.Installer.wixproj`
- Create: `src/QuotaGlass.Installer/Package.wxs`

**Interfaces:**
- Consumes: `MsiVersion=X.Y.Z`, `PayloadDir=<absolute-or-relative-directory>`, `assets/branding/quotaglass.ico`, and one published `QuotaGlass.exe`
- Produces: `QuotaGlass-vX.Y.Z-win-x64.msi` with package identity `QuotaGlass`, x64 per-user scope, one required feature, Start Menu shortcut, and uninstall cleanup action

- [ ] **Step 1: Prove the missing installer contract fails**

Run before creating the files:

```powershell
$required = @(
    'src/QuotaGlass/Properties/PublishProfiles/win-x64-self-contained.pubxml',
    'src/QuotaGlass.Installer/QuotaGlass.Installer.wixproj',
    'src/QuotaGlass.Installer/Package.wxs'
)
$missing = @($required | Where-Object { -not (Test-Path -LiteralPath $_) })
if ($missing.Count -gt 0) {
    throw "Missing installer files: $($missing -join ', ')"
}
```

Expected: FAIL listing all three missing files.

- [ ] **Step 2: Add the self-contained publish profile**

Create an SDK publish profile with `Release`, the existing Windows target
framework, `win-x64`, `SelfContained=true`, `PublishSingleFile=true`, and no
debug symbols. Publish to `artifacts/QuotaGlass-msi-payload` and assert the
recursive inventory is exactly `QuotaGlass.exe`.

- [ ] **Step 3: Add the pinned WiX project**

Use this project boundary:

```xml
<Project Sdk="WixToolset.Sdk/5.0.2">
  <PropertyGroup>
    <InstallerPlatform>x64</InstallerPlatform>
    <OutputName>QuotaGlass-v$(MsiVersion)-win-x64</OutputName>
    <IntermediateOutputPath>obj\$(Configuration)\$(MsiVersion)\</IntermediateOutputPath>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <DefineConstants>PayloadDir=$(PayloadDir);MsiVersion=$(MsiVersion)</DefineConstants>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="WixToolset.Util.wixext" Version="5.0.2" />
  </ItemGroup>
</Project>
```

Add pre-build errors for an empty `MsiVersion`, a version outside the
`major.minor.patch` Windows Installer range, an empty `PayloadDir`, and a
missing `QuotaGlass.exe`.

- [ ] **Step 4: Author the package**

Use one explicit stable `UpgradeCode`, `ProductCode="*"`,
`Version="$(var.MsiVersion)"`, `Manufacturer="QuotaGlass"`,
`Scope="perUser"`, and x64 components. Set
`ARPNOMODIFY=1`, install location and project URL properties, product icon,
`MajorUpgrade Schedule="afterInstallInitialize"`, and one embedded cabinet.

Author `LocalAppDataFolder/Programs/QuotaGlass` and a current-user Start Menu
folder. Install one file and one Start Menu shortcut. Add no desktop shortcut
and no Run registry value. Use one installer-owned value under
`HKCU\Software\QuotaGlass\Installer` as the component key path, and add explicit
empty-directory cleanup rows required for a per-user component. Suppress only
ICE91 because the package is already fixed to per-user scope.

Author an immediate DTF custom action that inserts a temporary Registry table
row for the exact current-user `QuotaGlass` Run value. Sequence it before the
standard `RemoveRegistryValues` action with condition
`REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE`. Use current-user impersonation and
accept an already-absent value.

Author an immediate, impersonated WiX utility action that invokes the system
`taskkill.exe` for `QuotaGlass.exe` with condition
`Installed OR WIX_UPGRADE_DETECTED`. Ignore an already-absent process and disable
Restart Manager reboot handling. This action is justified by the recorded
standard Restart Manager failure and must not run on a clean first install.

- [ ] **Step 5: Restore and build two MSIs**

Run:

```powershell
dotnet restore src/QuotaGlass.Installer/QuotaGlass.Installer.wixproj
dotnet publish src/QuotaGlass/QuotaGlass.csproj -c Release `
    -p:PublishProfile=win-x64-self-contained `
    -p:Version=0.0.1 `
    -p:AssemblyVersion=0.0.0.0 `
    -p:FileVersion=0.0.1.0 `
    -p:InformationalVersion=0.0.1 `
    -p:IncludeSourceRevisionInInformationalVersion=false `
    -o artifacts/QuotaGlass-msi-payload-0.0.1
dotnet publish src/QuotaGlass/QuotaGlass.csproj -c Release `
    -p:PublishProfile=win-x64-self-contained `
    -p:Version=0.0.2 `
    -p:AssemblyVersion=0.0.0.0 `
    -p:FileVersion=0.0.2.0 `
    -p:InformationalVersion=0.0.2 `
    -p:IncludeSourceRevisionInInformationalVersion=false `
    -o artifacts/QuotaGlass-msi-payload-0.0.2
dotnet build src/QuotaGlass.Installer/QuotaGlass.Installer.wixproj -c Release `
    --no-restore `
    -p:MsiVersion=0.0.1 `
    -p:PayloadDir="$PWD\artifacts\QuotaGlass-msi-payload-0.0.1" `
    -p:OutputPath="$PWD\artifacts\msi-0.0.1"
dotnet build src/QuotaGlass.Installer/QuotaGlass.Installer.wixproj -c Release `
    --no-restore `
    -p:MsiVersion=0.0.2 `
    -p:PayloadDir="$PWD\artifacts\QuotaGlass-msi-payload-0.0.2" `
    -p:OutputPath="$PWD\artifacts\msi-0.0.2"
```

Expected: both builds succeed with zero warnings, and the two MSI files exist.

- [ ] **Step 6: Commit the package foundation**

```powershell
git add src/QuotaGlass/Properties/PublishProfiles/win-x64-self-contained.pubxml `
    src/QuotaGlass.Installer
git commit -m "feat(installer): add per-user wix package" -m "Created with Codex"
```

### Task 2: MSI Metadata and Lifecycle Verification

**Files:**
- Create: `eng/installer/Test-MsiMetadata.ps1`
- Create: `eng/installer/Test-MsiLifecycle.ps1`

**Interfaces:**
- Consumes: explicit MSI paths and expected `major.minor.patch` versions
- Produces: nonzero exit on metadata, scope, inventory, install, upgrade, process, cleanup, or persistence contract failures; verbose MSI logs in a temporary directory

- [ ] **Step 1: Write the metadata test before its implementation is usable**

Create `Test-MsiMetadata.ps1` with parameters `MsiPath` and `ExpectedVersion`.
First make it require a `Get-MsiTable` helper that does not exist, then run:

```powershell
pwsh -NoProfile -File eng/installer/Test-MsiMetadata.ps1 `
    -MsiPath artifacts/msi-0.0.1/QuotaGlass-v0.0.1-win-x64.msi `
    -ExpectedVersion 0.0.1
```

Expected: FAIL because the MSI database helper is missing.

- [ ] **Step 2: Implement read-only MSI inspection**

Use the `WindowsInstaller.Installer` COM API with read-only database mode and
helpers that safely execute fixed SQL statements. Assert literal expected
values for `ProductName`, `ProductVersion`, `Manufacturer`, per-user properties,
package identity, ProductCode, x64 summary template, x64 component attributes,
directory rows, exactly one `QuotaGlass.exe` File row, one Start Menu shortcut,
zero DesktopFolder shortcuts, major-upgrade rows, and the single bounded custom
action plus uninstall-only sequence condition.

- [ ] **Step 3: Verify red-green metadata behavior**

Run the script for `0.0.1` and `0.0.2`. Temporarily pass the wrong expected
version and confirm that call fails with a version mismatch, then rerun both
correct versions and confirm they pass. Assert the ProductCodes differ while
the package upgrade identity matches.

- [ ] **Step 4: Write the lifecycle test**

Create `Test-MsiLifecycle.ps1` with parameters `BaseMsiPath`,
`BaseVersion`, `UpgradeMsiPath`, and `UpgradeVersion`. Implement one checked
`msiexec` wrapper that accepts only success code 0 and writes verbose logs.
Before the implementation is complete, run it and observe failure on the first
missing resource assertion.

- [ ] **Step 5: Implement clean install, running upgrade, and uninstall checks**

The script must:

1. Capture any current Run value named `QuotaGlass` without logging its data.
2. Create one GUID-named sentinel under `%APPDATA%\QuotaGlass`.
3. Silently install the base MSI with `/qn /norestart`.
4. Verify the installed executable, current-user Start Menu shortcut, install
   directory, and HKCU Apps & Features entry.
5. Start the installed executable and verify that exact path has a live process.
6. Silently install the upgrade MSI, reject reboot scheduling in its verbose
   log, and verify the old ProductCode is gone, the
   new ProductCode is registered, the file version changed, and no reboot code
   was returned.
7. Set the Run value to a harmless test command, silently uninstall the upgrade
   MSI, and verify that value, installer resources, shortcut, and registration
   are gone while the sentinel remains.
8. In `finally`, stop only a process whose executable path is the test install
   path, attempt uninstall of either test ProductCode, restore the captured Run
   value, delete only the sentinel, and retain logs on failure.

- [ ] **Step 6: Run the lifecycle test twice**

```powershell
pwsh -NoProfile -File eng/installer/Test-MsiLifecycle.ps1 `
    -BaseMsiPath artifacts/msi-0.0.1/QuotaGlass-v0.0.1-win-x64.msi `
    -BaseVersion 0.0.1 `
    -UpgradeMsiPath artifacts/msi-0.0.2/QuotaGlass-v0.0.2-win-x64.msi `
    -UpgradeVersion 0.0.2
```

Expected: both runs pass, proving cleanup is idempotent and leaves no installed
test product.

- [ ] **Step 7: Commit verification tools**

```powershell
git add eng/installer
git commit -m "test(installer): verify msi metadata and lifecycle" -m "Created with Codex"
```

### Task 3: Pull Request Build Integration

**Files:**
- Modify: `.github/workflows/build.yml`

**Interfaces:**
- Consumes: standalone WiX project, self-contained publish profile, and both installer test scripts
- Produces: required `Build, test, and publish` job coverage for two MSI versions and an uploaded current test MSI

- [ ] **Step 1: Record the missing workflow behavior**

Run a YAML query that requires steps named `Restore installer`, `Publish MSI
payload`, `Build MSI test versions`, `Verify MSI test versions`, and `Test MSI
lifecycle`. Expected: FAIL because the steps are absent.

- [ ] **Step 2: Add MSI build and test steps**

Keep the existing solution steps and portable publish unchanged. Restore the
WiX project, publish separately version-stamped self-contained `0.0.1` and
`0.0.2` payloads, assert both one-file inventories, build matching MSIs into
separate directories, run metadata verification on both, compare product and
upgrade identities, and run the lifecycle script.
Add the `0.0.2` MSI to the existing upload artifact path list.

- [ ] **Step 3: Validate the workflow**

Run YAML parsing and `actionlint`. Re-run the exact local commands from the new
steps. Confirm the required job names and existing action SHAs are unchanged.

- [ ] **Step 4: Commit build integration**

```powershell
git add .github/workflows/build.yml
git commit -m "ci(installer): test msi lifecycle on windows" -m "Created with Codex"
```

### Task 4: Release Packaging and Immutable Finalization

**Files:**
- Modify: `.github/workflows/release-please.yml`

**Interfaces:**
- Consumes: Release Please `version`, `major`, `tag_name`, and tagged commit SHA
- Produces: exact four-file candidate and no-overwrite reconciliation of ZIP, ZIP checksum, MSI, and MSI checksum

- [ ] **Step 1: Record the old two-file release contract**

Run a structural assertion that expects four upload paths and four final asset
names. Expected: FAIL because the workflow handles only the ZIP and its checksum.

- [ ] **Step 2: Restore, publish, build, and verify the release MSI**

Restore the standalone WiX project. Publish the self-contained payload with the
same Release Please version properties used for the portable executable. Build
the WiX project with `MsiVersion=$env:RELEASE_VERSION`, verify metadata and the
one-file MSI payload, and copy the MSI into `artifacts/release` under the exact
tagged name.

- [ ] **Step 3: Create and upload the MSI checksum**

Write `QuotaGlass-$env:RELEASE_TAG-win-x64.msi.sha256` as lowercase SHA-256,
two spaces, the MSI filename, UTF-8 without BOM, and one newline. Upload all
four exact paths in the release-candidate artifact.

- [ ] **Step 4: Extend protected verification and finalization**

Validate both checksum files after download. Reconcile all four assets with the
existing no-clobber SHA-256 comparison. Replace the two-entry expected asset set
with the exact four-entry set. Preserve tag/SHA binding, draft checks, protected
`release` environment, manual gate, and immutable publication check.

- [ ] **Step 5: Validate release behavior locally**

Run YAML parsing and `actionlint`. Rehearse version `0.1.0`: build both publishes
and the MSI, create both checksums, run `sha256sum --check` or equivalent on
both, and assert the release directory contains exactly four expected names.
Exercise local no-overwrite comparison with a matching MSI and a deliberately
mismatching MSI fixture.

- [ ] **Step 6: Commit release integration**

```powershell
git add .github/workflows/release-please.yml
git commit -m "feat(release): publish wix msi artifacts" -m "Created with Codex"
```

### Task 5: Installer and Release Documentation

**Files:**
- Modify: `README.md`
- Modify: `docs/manual-test-checklist.md`
- Modify: `docs/release-process.md`

**Interfaces:**
- Consumes: implemented MSI scope, paths, commands, artifact names, acceptance steps, and recovery behavior
- Produces: user installation guidance and maintainer release acceptance instructions

- [ ] **Step 1: Update the README installation path**

Add an `Installation` section before `Build`. Recommend the self-contained MSI
for normal users, retain the portable ZIP and .NET runtime requirement, state
the per-user install directory and unknown-publisher warning, and include exact
silent commands:

```powershell
msiexec /i QuotaGlass-vX.Y.Z-win-x64.msi /qn /norestart
msiexec /i QuotaGlass-vNEW-win-x64.msi /qn /norestart
msiexec /x QuotaGlass-vX.Y.Z-win-x64.msi /qn /norestart
```

- [ ] **Step 2: Extend the reusable acceptance checklist**

Add checks for MSI metadata, interactive and silent clean install, installed
launch, Start Menu and Apps & Features registration, no desktop shortcut, no
default autostart, running-process upgrade without reboot, silent uninstall,
Run-value cleanup, resource removal, and settings/log preservation. Do not add
local account data or live provider values.

- [ ] **Step 3: Update release and recovery documentation**

Document WiX restore/build, separate self-contained payload, four candidate
files, both checksum formats, MSI acceptance before environment approval,
four-asset no-overwrite finalization, tagged-commit recovery, and later WinGet
use of the immutable MSI URL and checksum.

- [ ] **Step 4: Validate documentation**

Search for stale two-file candidate counts, incorrect runtime requirements,
placeholders, prohibited control markers, and em dash characters. Run
`git diff --check`.

- [ ] **Step 5: Commit documentation**

```powershell
git add README.md docs/manual-test-checklist.md docs/release-process.md
git commit -m "docs(installer): document msi installation and release" -m "Created with Codex"
```

### Task 6: Local OpenCode Validation, Reviews, and Pull Request

**Files:**
- Modify only previously listed files when verification or review identifies a defect

**Interfaces:**
- Consumes: complete branch diff from `91ab4f4611fed22798e2fc3823ba9b7ab4d191bb`
- Produces: verified pushed branch and ready-for-review pull request targeting `master`

- [ ] **Step 1: Run full clean verification**

Run solution restore, Release build, all tests, existing portable publish,
self-contained publish, two WiX builds, metadata checks, lifecycle test, YAML
parsing, `actionlint`, checksum rehearsal, `git diff --check`, prohibited
character scan, and repository status review. Confirm no credentials, MSI logs,
runtime data, or generated artifacts are tracked.

- [ ] **Step 2: Test installed QuotaGlass with local OpenCode**

Confirm at least one `opencode` process is running without reading its command
line or credentials. Install the locally built `0.0.2` MSI silently, launch the
installed QuotaGlass executable in the interactive session, and verify the
process remains alive through first tray initialization and provider polling.
Do not print live quota values or account data. Uninstall silently and confirm
the existing `%APPDATA%\QuotaGlass` directory remains.

- [ ] **Step 3: Request independent specification and code-quality reviews**

Dispatch two read-only reviewer subagents with clean context. Give both the
issue requirements, spec path, base SHA, head SHA, and verification commands.
One reviewer checks requirement coverage and release correctness. The other
checks WiX, PowerShell safety, lifecycle cleanup, and maintainability. Fix all
valid critical and important findings, then rerun affected verification and one
scoped re-review of the fixes.

- [ ] **Step 4: Run final verification after review fixes**

Repeat the full Release build and test suite, both publish inventories, WiX
build and metadata verification, lifecycle test, workflow validation, diff
checks, and status inspection on the exact tree to be pushed.

- [ ] **Step 5: Push the branch**

```powershell
git push --set-upstream origin codex/issue-18-wix-msi
```

- [ ] **Step 6: Open the pull request**

Create a non-draft pull request targeting `master` with title:

```text
feat(release): add a per-user wix msi installer
```

The body must state the user-visible outcome, link `#18`, list exact local
verification, call out the standard Restart Manager running-upgrade result,
describe the one justified uninstall custom action, note the unsigned
unknown-publisher warning, and end with `Created with Codex`.

- [ ] **Step 7: Verify the remote pull request**

Read back the PR title, base, head, body, changed-file list, and check status.
Confirm it is ready for review, points to the pushed head SHA, and contains no
forbidden release-control markers.
