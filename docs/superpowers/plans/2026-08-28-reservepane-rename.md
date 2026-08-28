# ReservePane Clean-Break Rename Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rename QuotaGlass completely to ReservePane as a new product identity with no migration or compatibility behavior.

**Architecture:** Preserve the existing WPF, provider, installer, and release architecture while replacing every active identity at its current ownership boundary. Establish consumer-visible ReservePane expectations first, then perform the mechanical project rename, replace branding and installer identity, update release automation and documentation, and finish with full build, publish, MSI, visual, and repository verification.

**Tech Stack:** .NET 10, C# 14, WPF, xUnit, WiX Toolset 4, PowerShell, GitHub Actions, Release Please, SVG, Pillow

**Spec:** `docs/superpowers/specs/2026-08-28-reservepane-rename-design.md`

## Global Constraints

- The public product and repository name is exactly `ReservePane`.
- The public repository URL is exactly `https://github.com/medokin/ReservePane`.
- This is a clean break with no settings, logs, autostart, executable, installer, or release compatibility for QuotaGlass.
- Runtime state lives only under `%APPDATA%\ReservePane`.
- The autostart value is exactly `ReservePane`.
- The MSI UpgradeCode is `0E117091-74A9-47E2-A7E5-16C3720AC18D`.
- The MSI application component GUID is `1105C941-2A6C-44A2-8C3E-9FA32AFEB314`.
- The shutdown signal prefix is exactly `Local\ReservePane.InstallerShutdown`.
- Portable and MSI payload inventories contain only `ReservePane.exe`.
- Release assets use `ReservePane-vX.Y.Z-win-x64.*`.
- Release Please continues to own `.release-please-manifest.json`, `version.txt`, generated `CHANGELOG.md`, version tags, and GitHub Releases.
- Existing QuotaGlass tags, releases, generated changelog entries, and historical implementation records remain unchanged.
- Code, code comments, tests, configuration, generated artifacts, and repository documentation are written in English and do not receive AI disclosure notes.
- Commits and pull request titles use Conventional Commit format. Commit bodies and the pull request body include `Created with Codex`.

---

### Task 1: Establish ReservePane identity expectations

**Files:**
- Modify: `tests/QuotaGlass.Tests/Core/AppPathsTests.cs`
- Modify: `tests/QuotaGlass.Tests/Platform/AutostartServiceTests.cs`
- Modify: `tests/QuotaGlass.Tests/Platform/InstallerShutdownSignalTests.cs`
- Modify: `tests/QuotaGlass.Tests/Ui/AppManifestTests.cs`
- Modify: `tests/QuotaGlass.Tests/Ui/TrayIconHostTests.cs`
- Modify: `tests/QuotaGlass.Tests/Ui/UiConstructionSmokeTests.cs`

**Interfaces:**
- Consumes: existing `AppPaths.FromEnvironment()`, `AutostartService`, `InstallerShutdownSignalName.FromExecutablePath(string)`, WPF window construction, and executable metadata
- Produces: failing behavioral expectations for the ReservePane data directory, autostart value, shutdown prefix, window titles, tray tooltip, and executable identity

- [ ] **Step 1: Change the application-path expectation to ReservePane**

Rename the test and use a hand-derived literal:

```csharp
[Fact]
public void FromEnvironment_StoresApplicationStateUnderReservePaneDirectory()
{
    // Break caught: the clean rename leaves settings or logs under another product directory.
    AppPaths paths = AppPaths.FromEnvironment();
    string? settingsDirectory = Path.GetDirectoryName(paths.SettingsPath);

    Assert.Equal("ReservePane", Path.GetFileName(settingsDirectory));
    Assert.Equal(settingsDirectory, Path.GetDirectoryName(paths.LogPath));
    Assert.EndsWith(
        Path.Combine(".local", "share", "opencode", "auth.json"),
        paths.OpenCodeAuthPath,
        StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Change autostart expectations to the ReservePane-owned value**

Use `C:\Program Files\ReservePane\ReservePane.exe` and `C:\Apps\ReservePane.exe` in setup. Assert `ReservePane` for every name passed to `IRunKey`, including the assertion inside `FakeRunKey.GetValue`.

```csharp
Assert.Equal("ReservePane", runKey.SetName);
Assert.Equal("\"C:\\Program Files\\ReservePane\\ReservePane.exe\"", runKey.SetValueText);
Assert.Equal("ReservePane", runKey.DeletedName);
```

- [ ] **Step 3: Add an explicit shutdown-prefix regression test**

```csharp
[Fact]
public void FromExecutablePath_UsesReservePaneSignalPrefix()
{
    string signal = InstallerShutdownSignalName.FromExecutablePath(
        @"C:\Users\tester\AppData\Local\Programs\ReservePane\ReservePane.exe");

    Assert.StartsWith(
        @"Local\ReservePane.InstallerShutdown.",
        signal,
        StringComparison.Ordinal);
}
```

Also replace temporary and sample executable paths in the existing signal tests with ReservePane paths.

- [ ] **Step 4: Change existing UI and executable identity assertions to ReservePane**

Change both executable paths in `AppManifestTests` to `ReservePane.exe`. In `UiConstructionSmokeTests`, capture and assert the real WPF titles:

```csharp
string? popupTitle = null;
string? overlayTitle = null;
// Inside the STA thread, after constructing both windows:
popupTitle = popup.Title;
overlayTitle = overlay.Title;
// After joining the thread:
Assert.Equal("ReservePane", popupTitle);
Assert.Equal("ReservePane overlay", overlayTitle);
```

Add a real tray-view test to `TrayIconHostTests`:

```csharp
[Fact]
public void WinFormsTrayIconView_InitialTooltipUsesReservePaneIdentity()
{
    using var view = new WinFormsTrayIconView();

    Assert.Equal("ReservePane", view.Tooltip);
}
```

The hidden hotkey-window name is verified by the active-surface search after production is renamed. It does not receive a constant-only change-detector test.

- [ ] **Step 5: Run the targeted tests and verify RED**

Run:

```powershell
dotnet test QuotaGlass.slnx -c Release --no-restore --filter "FullyQualifiedName~AppPathsTests|FullyQualifiedName~AutostartServiceTests|FullyQualifiedName~InstallerShutdownSignalTests|FullyQualifiedName~AppManifestTests|FullyQualifiedName~TrayIconHostTests|FullyQualifiedName~UiConstructionSmokeTests"
```

Expected: failures show old QuotaGlass directory, registry value, signal prefix, UI copy, or executable identity. Compile errors are not an acceptable RED result.

- [ ] **Step 6: Commit the failing expectations**

```powershell
git add tests/QuotaGlass.Tests
git commit -m "test(branding): define reservepane identity contract" -m "Created with Codex"
```

---

### Task 2: Rename solution, projects, assemblies, namespaces, and runtime identity

**Files:**
- Move: `QuotaGlass.slnx` to `ReservePane.slnx`
- Move: `src/QuotaGlass/` to `src/ReservePane/`
- Move: `src/ReservePane/QuotaGlass.csproj` to `src/ReservePane/ReservePane.csproj`
- Move: `tests/QuotaGlass.Tests/` to `tests/ReservePane.Tests/`
- Move: `tests/ReservePane.Tests/QuotaGlass.Tests.csproj` to `tests/ReservePane.Tests/ReservePane.Tests.csproj`
- Move: `src/QuotaGlass.Installer/` to `src/ReservePane.Installer/`
- Move: `src/ReservePane.Installer/QuotaGlass.Installer.wixproj` to `src/ReservePane.Installer/ReservePane.Installer.wixproj`
- Move: `src/QuotaGlass.InstallerActions/` to `src/ReservePane.InstallerActions/`
- Move: `src/ReservePane.InstallerActions/QuotaGlass.InstallerActions.csproj` to `src/ReservePane.InstallerActions/ReservePane.InstallerActions.csproj`
- Modify: all files under the moved production and test directories
- Modify: `ReservePane.slnx`

**Interfaces:**
- Consumes: failing ReservePane expectations from Task 1
- Produces: `ReservePane.slnx`, `ReservePane` production namespace and assembly, `ReservePane.Tests` test namespace and assembly, renamed WPF/XAML types, and renamed runtime identity strings

- [ ] **Step 1: Move the solution and project directories**

```powershell
git mv QuotaGlass.slnx ReservePane.slnx
git mv src/QuotaGlass src/ReservePane
git mv src/ReservePane/QuotaGlass.csproj src/ReservePane/ReservePane.csproj
git mv tests/QuotaGlass.Tests tests/ReservePane.Tests
git mv tests/ReservePane.Tests/QuotaGlass.Tests.csproj tests/ReservePane.Tests/ReservePane.Tests.csproj
git mv src/QuotaGlass.Installer src/ReservePane.Installer
git mv src/ReservePane.Installer/QuotaGlass.Installer.wixproj src/ReservePane.Installer/ReservePane.Installer.wixproj
git mv src/QuotaGlass.InstallerActions src/ReservePane.InstallerActions
git mv src/ReservePane.InstallerActions/QuotaGlass.InstallerActions.csproj src/ReservePane.InstallerActions/ReservePane.InstallerActions.csproj
```

- [ ] **Step 2: Perform the active source and test token rename**

Apply the exact case-sensitive token replacement `QuotaGlass` to `ReservePane` to text files under `src/ReservePane`, `src/ReservePane.Installer`, `src/ReservePane.InstallerActions`, and `tests/ReservePane.Tests`, plus `ReservePane.slnx`. Preserve UTF-8 encoding and existing line endings. Do not touch `CHANGELOG.md` or historical files under `docs/superpowers`.

The mechanical change must cover:

```text
namespace QuotaGlass          -> namespace ReservePane
using QuotaGlass              -> using ReservePane
QuotaGlass.Tests              -> ReservePane.Tests
x:Class="QuotaGlass.          -> x:Class="ReservePane.
clr-namespace:QuotaGlass.     -> clr-namespace:ReservePane.
QuotaGlass.exe                -> ReservePane.exe
QuotaGlass.csproj             -> ReservePane.csproj
quotaglass.ico                -> reservepane.ico
tests/QuotaGlass.Tests        -> tests/ReservePane.Tests
```

- [ ] **Step 3: Verify project metadata and references**

`src/ReservePane/ReservePane.csproj` must contain:

```xml
<AssemblyName>ReservePane</AssemblyName>
<RootNamespace>ReservePane</RootNamespace>
<AssemblyTitle>ReservePane</AssemblyTitle>
<Product>ReservePane</Product>
<Description>ReservePane is a Windows tray application that monitors Claude, Codex, OpenCode, and Ollama usage at a glance.</Description>
<ApplicationIcon>..\..\assets\branding\reservepane.ico</ApplicationIcon>
```

`ReservePane.slnx` must reference `src/ReservePane/ReservePane.csproj` and `tests/ReservePane.Tests/ReservePane.Tests.csproj`. The test project must reference the renamed production project, and `InternalsVisibleTo` must name `ReservePane.Tests`.

- [ ] **Step 4: Verify runtime identity owners**

Production code must contain these exact values:

```csharp
string applicationDirectory = Path.Combine(appData, "ReservePane");
internal const string ValueName = "ReservePane";
return $"Local\\ReservePane.InstallerShutdown.{hexHash}";
```

WPF and tray code must use `ReservePane`, `ReservePane overlay`, and `ReservePane Hotkey` at the same ownership points as the old values.

- [ ] **Step 5: Restore and build after the mechanical rename**

```powershell
dotnet restore ReservePane.slnx
dotnet restore src/ReservePane.Installer/ReservePane.Installer.wixproj
dotnet build ReservePane.slnx -c Release --no-restore
```

Expected: build succeeds with zero warnings and zero errors. Fix only rename-related compiler, project-reference, linked-source, and generated-XAML failures.

- [ ] **Step 6: Run all renamed tests and verify GREEN**

```powershell
dotnet test ReservePane.slnx -c Release --no-build
```

Expected: 507 tests pass, including the new shutdown-prefix and initial tray-tooltip tests.

- [ ] **Step 7: Commit the codebase and runtime rename**

```powershell
git add ReservePane.slnx src tests
git commit -m "feat(branding): rename application to reservepane" -m "Created with Codex"
```

---

### Task 3: Replace branding assets with the pane-and-gauge design

**Files:**
- Move: `assets/branding/quotaglass-logo.svg` to `assets/branding/reservepane-logo.svg`
- Move: every `assets/branding/quotaglass-logo-*.png` to `assets/branding/reservepane-logo-*.png`
- Existing: `assets/branding/reservepane.ico`, moved with the project rename so the Task 2 build could resolve the renamed application icon
- Modify: `assets/branding/reservepane-logo.svg`
- Modify: `assets/branding/build-logo-assets.ps1`
- Modify: `assets/branding/README.md`

**Interfaces:**
- Consumes: `reservepane.ico` path introduced by Task 2
- Produces: SVG source, PNG renditions at 16 through 1024 pixels, and a multi-resolution ICO with ReservePane filenames

- [ ] **Step 1: Move the tracked branding files**

```powershell
git mv assets/branding/quotaglass-logo.svg assets/branding/reservepane-logo.svg
Get-ChildItem assets/branding/quotaglass-logo-*.png | ForEach-Object {
    git mv $_.FullName ($_.FullName -replace 'quotaglass-logo-', 'reservepane-logo-')
}
```

- [ ] **Step 2: Replace the SVG geometry**

Use the existing 1024 view box and outer rounded square. Replace the Q outline and tail with an inset rounded pane, gauge arc, hub, and needle:

```xml
<title id="title">ReservePane logo</title>
<desc id="description">A compact status pane containing a green usage gauge on a dark rounded square.</desc>
<rect x="64" y="64" width="896" height="896" rx="200" fill="#161C23" />
<rect x="220" y="218" width="584" height="588" rx="76"
      fill="none" stroke="#8F9CAA" stroke-width="72" />
<path d="M 318 594 A 194 194 0 0 1 706 594"
      fill="none" stroke="#35C46A" stroke-width="72" stroke-linecap="butt" />
<circle cx="512" cy="594" r="48" fill="#35C46A" />
<path d="M 499 580 L 680 432 L 535 613 Z" fill="#35C46A" />
```

- [ ] **Step 3: Rename every asset-pipeline path**

In `build-logo-assets.ps1`, use:

```powershell
[string]$Source = (Join-Path $PSScriptRoot "reservepane-logo.svg")
$masterPng = Join-Path $outputPath "reservepane-logo-1024.png"
$temporaryProfile = Join-Path ([System.IO.Path]::GetTempPath()) ("reservepane-logo-" + [Guid]::NewGuid().ToString("N"))
$temporaryMasterPng = Join-Path $temporaryProfile "reservepane-logo-1024.png"
```

The embedded Pillow script must read and write `reservepane-logo-{size}.png` and `reservepane.ico`. Cleanup must accept only temporary names beginning with `reservepane-logo-`.

- [ ] **Step 4: Regenerate all raster assets**

```powershell
./assets/branding/build-logo-assets.ps1
```

Expected files: `reservepane-logo-{16,24,32,48,64,128,256,512,1024}.png` and `reservepane.ico`.

- [ ] **Step 5: Inspect the generated assets**

Create a temporary contact sheet outside the repository or inspect each PNG directly. Check all nine sizes against light and dark backgrounds. Confirm the frame remains visible, the gauge reads clearly, and the silhouette has no Q tail or R/P monogram.

- [ ] **Step 6: Verify filenames and embedded SVG identity**

```powershell
rg -n -i 'quotaglass' assets/branding
Get-ChildItem assets/branding | Sort-Object Name | Select-Object Name,Length
```

Expected: no old-name match and exactly the ReservePane SVG, nine PNGs, ICO, script, and README.

- [ ] **Step 7: Commit the branding assets**

```powershell
git add assets/branding
git commit -m "feat(branding): replace logo with reservepane gauge" -m "Created with Codex"
```

---

### Task 4: Replace installer identity and lifecycle verification

**Files:**
- Modify: `src/ReservePane.Installer/Package.wxs`
- Modify: `src/ReservePane.Installer/ReservePane.Installer.wixproj`
- Modify: `src/ReservePane.InstallerActions/RunningApplicationAction.cs`
- Modify: `src/ReservePane.InstallerActions/AutostartCleanupAction.cs`
- Modify: `src/ReservePane.InstallerActions/ReservePane.InstallerActions.csproj`
- Modify: `eng/installer/Test-MsiMetadata.ps1`
- Modify: `eng/installer/Test-MsiLifecycle.ps1`
- Modify: `tests/ReservePane.Tests/Platform/InstallerShutdownSignalTests.cs`

**Interfaces:**
- Consumes: `ReservePane.exe`, ReservePane shutdown signal, and `reservepane.ico`
- Produces: a fresh ReservePane MSI product family and ReservePane-only `0.0.1` to `0.0.2` lifecycle checks

- [ ] **Step 1: Set the new package identity**

Use these exact WiX values:

```xml
<Package Name="ReservePane"
         Manufacturer="ReservePane"
         UpgradeCode="0E117091-74A9-47E2-A7E5-16C3720AC18D">
<SummaryInformation Description="ReservePane Windows x64 installer" />
<Property Id="ARPURLINFOABOUT" Value="https://github.com/medokin/ReservePane" />
<Icon Id="ReservePaneIcon" SourceFile="..\..\assets\branding\reservepane.ico" />
<Directory Id="INSTALLFOLDER" Name="ReservePane" />
<Directory Id="ApplicationProgramsFolder" Name="ReservePane" />
<Component Id="ReservePaneApplication"
           Directory="INSTALLFOLDER"
           Guid="1105C941-2A6C-44A2-8C3E-9FA32AFEB314"
           Bitness="always64">
```

The payload, shortcut, marker, binary, feature, and custom-action IDs use ReservePane consistently. The marker key is `Software\ReservePane\Installer` and the payload source is `$(var.PayloadDir)\ReservePane.exe`.

- [ ] **Step 2: Make installer actions ReservePane-only**

`RunningApplicationAction.CloseInstalledReservePane` constructs `ReservePane.exe`, enumerates only process name `ReservePane`, logs ReservePane copy, and uses the ReservePane signal.

`AutostartCleanupAction.AddReservePaneAutostartCleanup` inserts a temporary MSI Registry row that removes only Run value `ReservePane`, with value `"[INSTALLFOLDER]ReservePane.exe"` and component `ReservePaneApplication`. Rename the record identifier and remove the word `Legacy` from method, identifier, and log copy because the cleanup applies to the current ReservePane product.

- [ ] **Step 3: Set project links and MSI output name**

The installer project references `ReservePane.InstallerActions.csproj`, links `..\ReservePane\Platform\InstallerShutdownSignalName.cs`, and writes:

```xml
<OutputName>ReservePane-v$(MsiVersion)-win-x64</OutputName>
```

- [ ] **Step 4: Update MSI metadata assertions**

Set:

```powershell
$expectedUpgradeCode = '{0E117091-74A9-47E2-A7E5-16C3720AC18D}'
$expectedComponentGuid = '{1105C941-2A6C-44A2-8C3E-9FA32AFEB314}'
```

Assert ReservePane product/manufacturer, ARP URL, icon, directory, component, payload, shortcut, registry marker, custom actions, binary, feature, and summary description. Assert the application component GUID equals `$expectedComponentGuid` and the only Run value referenced by uninstall cleanup is `ReservePane`.

- [ ] **Step 5: Update lifecycle isolation and assertions**

Use only ReservePane temporary install directories, data directories, registry keys, process names, executable names, shortcuts, MSI paths, and log names. Build and test versions `0.0.1` and `0.0.2`. Preserve the existing checks for exact-path running-process upgrade, uninstall cleanup, Apps & Features cleanup, Start Menu cleanup, data preservation under `%APPDATA%\ReservePane`, and no reboot.

- [ ] **Step 6: Build and verify MSI metadata**

```powershell
dotnet publish src/ReservePane/ReservePane.csproj -c Release -p:PublishProfile=win-x64-self-contained -p:Version=0.0.1 -p:AssemblyVersion=0.0.0.0 -p:FileVersion=0.0.1.0 -p:InformationalVersion=0.0.1 -p:IncludeSourceRevisionInInformationalVersion=false -o artifacts/ReservePane-msi-payload-0.0.1
dotnet publish src/ReservePane/ReservePane.csproj -c Release -p:PublishProfile=win-x64-self-contained -p:Version=0.0.2 -p:AssemblyVersion=0.0.0.0 -p:FileVersion=0.0.2.0 -p:InformationalVersion=0.0.2 -p:IncludeSourceRevisionInInformationalVersion=false -o artifacts/ReservePane-msi-payload-0.0.2
dotnet build src/ReservePane.Installer/ReservePane.Installer.wixproj -c Release --no-restore -p:MsiVersion=0.0.1 -p:PayloadDir="$pwd/artifacts/ReservePane-msi-payload-0.0.1" -p:OutputPath="$pwd/artifacts/msi/0.0.1/"
dotnet build src/ReservePane.Installer/ReservePane.Installer.wixproj -c Release --no-restore -p:MsiVersion=0.0.2 -p:PayloadDir="$pwd/artifacts/ReservePane-msi-payload-0.0.2" -p:OutputPath="$pwd/artifacts/msi/0.0.2/"
./eng/installer/Test-MsiMetadata.ps1 -MsiPath artifacts/msi/0.0.1/ReservePane-v0.0.1-win-x64.msi -ExpectedVersion 0.0.1
./eng/installer/Test-MsiMetadata.ps1 -MsiPath artifacts/msi/0.0.2/ReservePane-v0.0.2-win-x64.msi -ExpectedVersion 0.0.2
```

Expected: metadata script succeeds and the payload contains only `ReservePane.exe`.

- [ ] **Step 7: Run the two-version lifecycle test**

```powershell
./eng/installer/Test-MsiLifecycle.ps1 `
    -BaseMsiPath artifacts/msi/0.0.1/ReservePane-v0.0.1-win-x64.msi `
    -BaseVersion 0.0.1 `
    -UpgradeMsiPath artifacts/msi/0.0.2/ReservePane-v0.0.2-win-x64.msi `
    -UpgradeVersion 0.0.2
```

Expected: fresh install, running-process upgrade, uninstall, data preservation, registry cleanup, shortcut cleanup, and no-reboot assertions pass for ReservePane `0.0.1` to `0.0.2`.

- [ ] **Step 8: Commit installer identity**

```powershell
git add src/ReservePane.Installer src/ReservePane.InstallerActions eng/installer tests/ReservePane.Tests/Platform/InstallerShutdownSignalTests.cs
git commit -m "feat(installer): establish reservepane product identity" -m "Created with Codex"
```

---

### Task 5: Rename build, publish, release, and active documentation surfaces

**Files:**
- Modify: `.github/workflows/build.yml`
- Modify: `.github/workflows/release-please.yml`
- Modify: `release-please-config.json`
- Modify: `README.md`
- Modify: `SECURITY.md`
- Modify: `AGENTS.md`
- Modify: `docs/release-process.md`
- Modify: `docs/manual-test-checklist.md`
- Modify: `docs/assets/overlay.png` only if visual inspection finds old branding

**Interfaces:**
- Consumes: renamed solution, projects, executable, MSI, asset paths, and installer checks
- Produces: ReservePane CI artifacts, Release Please component branch, active documentation, and public repository links

- [ ] **Step 1: Rename build workflow paths and inventories**

Use `ReservePane.slnx`, `src/ReservePane/ReservePane.csproj`, renamed publish profiles, `artifacts/ReservePane-win-x64`, and `ReservePane.exe`. Uploaded build artifacts and inventory assertions use ReservePane naming only.

- [ ] **Step 2: Rename the complete release contract**

Use:

```bash
release_branch='release-please--branches--master--components--ReservePane'
```

Every candidate, reconciliation, archive, checksum, MSI, upload-artifact, download-artifact, verification, and finalization path uses `ReservePane-${RELEASE_TAG}-win-x64` or `ReservePane-v$env:RELEASE_VERSION-win-x64.msi` as appropriate. ZIP validation requires exactly `ReservePane.exe`.

- [ ] **Step 3: Rename the Release Please package**

```json
"packages": {
  ".": {
    "package-name": "ReservePane"
  }
}
```

Do not modify `.release-please-manifest.json`, `version.txt`, or generated `CHANGELOG.md`.

- [ ] **Step 4: Update active documentation and contributor guidance**

Use the public description from the spec. Update commands, paths, filenames, screenshots, installer behavior, release examples, repository clone URL, and security advisory URL. Reset the manual checklist for ReservePane and remove completed QuotaGlass execution evidence. Preserve historically accurate generated changelog and historical `docs/superpowers` records.

- [ ] **Step 5: Inspect the documentation screenshot**

Open `docs/assets/overlay.png`. If it shows a QuotaGlass title, Q-shaped icon, or old executable identity, replace it with a sanitized ReservePane screenshot produced from the renamed build. Otherwise leave it unchanged and record the inspection result in the pull request.

- [ ] **Step 6: Run active-surface old-name searches**

```powershell
rg -n -i 'quotaglass' src tests assets .github eng README.md SECURITY.md AGENTS.md docs/release-process.md docs/manual-test-checklist.md release-please-config.json ReservePane.slnx
rg -n 'medokin/QuotaGlass' README.md SECURITY.md AGENTS.md docs/release-process.md docs/manual-test-checklist.md .github release-please-config.json
```

Expected: no match. Do not include `CHANGELOG.md` or historical `docs/superpowers` paths in this assertion.

- [ ] **Step 7: Commit automation and documentation**

```powershell
git add .github release-please-config.json README.md SECURITY.md AGENTS.md docs/release-process.md docs/manual-test-checklist.md docs/assets/overlay.png
git commit -m "docs(branding): update reservepane release surfaces" -m "Created with Codex"
```

---

### Task 6: Run full repository verification and prepare the pull request

**Files:**
- Verify: all changed files
- Create outside tracked source: temporary publish, MSI, lifecycle, and visual-inspection artifacts
- External: GitHub pull request for issue #34

**Interfaces:**
- Consumes: all implementation tasks
- Produces: verified branch, focused pull request, representative logo screenshots, and deployment-gate checklist

- [ ] **Step 1: Run the full Release restore, build, and test sequence**

```powershell
dotnet restore ReservePane.slnx
dotnet restore src/ReservePane.Installer/ReservePane.Installer.wixproj
dotnet build ReservePane.slnx -c Release --no-restore
dotnet test ReservePane.slnx -c Release --no-build
```

Expected: zero warnings, zero errors, zero failed tests, and 507 total tests unless additional behavior tests were justified during implementation.

- [ ] **Step 2: Verify portable publish inventory**

```powershell
dotnet publish src/ReservePane/ReservePane.csproj -c Release -p:PublishProfile=win-x64 -o artifacts/ReservePane-win-x64
$publishedFiles = @(Get-ChildItem artifacts/ReservePane-win-x64 -File -Recurse)
if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Name -ne 'ReservePane.exe') {
    throw "Expected only ReservePane.exe, found: $($publishedFiles.FullName -join ', ')"
}
```

- [ ] **Step 3: Rebuild and inspect assets and MSI**

Run the asset generator, MSI metadata check, and two-version lifecycle check again against fresh outputs. Confirm PNG/ICO visual inspection at all required sizes and preserve representative sanitized screenshots for the pull request.

- [ ] **Step 4: Run repository hygiene checks**

```powershell
git diff --check
git status --short
rg -n -i 'quotaglass' src tests assets .github eng README.md SECURITY.md AGENTS.md docs/release-process.md docs/manual-test-checklist.md release-please-config.json ReservePane.slnx
rg -n 'medokin/QuotaGlass' README.md SECURITY.md AGENTS.md docs/release-process.md docs/manual-test-checklist.md .github release-please-config.json
```

Expected: clean diff formatting and no active old-name matches. Review all fixtures, screenshots, logs, and evidence for credentials, account identifiers, live responses, or sensitive data.

- [ ] **Step 5: Review the complete branch diff**

```powershell
git diff origin/master...HEAD --stat
git diff origin/master...HEAD
git log --oneline origin/master..HEAD
```

Confirm every acceptance criterion from the spec maps to a concrete change or verification result. Confirm `.release-please-manifest.json`, `version.txt`, and generated `CHANGELOG.md` are untouched.

- [ ] **Step 6: Push and open the pull request**

Push `codex/rename-reservepane`. Create a pull request titled `feat(branding): rename quotaglass to reservepane`. Link `#34`, state the user-visible clean-break outcome, list exact verification commands and results, attach representative icon screenshots, state that the GitHub repository rename remains at the deployment gate, and include `Created with Codex`.

- [ ] **Step 7: Wait for required checks**

Wait for `Secret scan`, `Build, test, and publish`, and `Validate PR title`. Fix implementation failures on the branch and rerun verification before updating the pull request.

---

### Task 7: Execute the GitHub rename and release deployment gate

**Files:**
- External: GitHub repository settings, local `origin`, pull request, Release Please pull request, and protected release environment

**Interfaces:**
- Consumes: green implementation pull request and verified branch
- Produces: repository `medokin/ReservePane`, squash-merged rename, and first immutable ReservePane release

- [ ] **Step 1: Confirm the deployment gate**

Verify no workflow is running or waiting for approval and the implementation pull request has all required successful checks. Do not rename the repository before this gate passes.

- [ ] **Step 2: Rename and describe the repository**

Rename `medokin/QuotaGlass` to `medokin/ReservePane`. Set the description to the public description from the spec. Preserve useful technology topics and add `ai-usage`.

- [ ] **Step 3: Update and verify the local remote**

```powershell
git remote set-url origin git@github.com:medokin/ReservePane.git
git remote -v
gh repo view medokin/ReservePane
```

Verify the old repository URL redirects to the renamed repository.

- [ ] **Step 4: Revalidate protected repository settings**

Verify the `Protect master` ruleset still requires `Secret scan`, `Build, test, and publish`, and `Validate PR title`. Verify squash-only merge, Actions permissions, release immutability, the protected `release` environment, `master` deployment restriction, and `medokin` approval survived the rename.

- [ ] **Step 5: Re-run invalidated checks and squash merge**

If GitHub invalidated checks, rerun them and wait for success. Squash merge with title `feat(branding): rename quotaglass to reservepane` and a body containing `Created with Codex`.

- [ ] **Step 6: Validate Release Please and publish ReservePane**

Review the generated Release Please pull request. Confirm its source branch uses the ReservePane component name and its only files are `.release-please-manifest.json`, `CHANGELOG.md`, and `version.txt`. Merge after required checks, validate the exact release candidate, approve the protected release environment, and verify the immutable release contains exactly the ReservePane ZIP, ZIP checksum, MSI, and MSI checksum.

- [ ] **Step 7: Perform final product validation**

Validate portable launch, fresh install, ReservePane-to-ReservePane upgrade, uninstall, tray icon, popup title, overlay, settings, autostart, notifications, provider discovery, source link, clone URL, security advisory URL, and release links. Confirm no supported path reads or modifies QuotaGlass state.
