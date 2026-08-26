# Release Please Management Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an approved, retry-safe Release Please flow that turns squash-merged Conventional Commit pull requests into versioned QuotaGlass GitHub Releases with verified Windows artifacts.

**Architecture:** Release Please manages the version, generated changelog, release pull request, tag, and draft GitHub Release. A conditional Windows job builds and packages the tagged commit, then a protected finalization job verifies and attaches the immutable assets before publishing the draft. A separate metadata-only workflow validates pull request titles and prevents version overrides.

**Tech Stack:** GitHub Actions, `googleapis/release-please-action` v5, `amannn/action-semantic-pull-request` v6, PowerShell 7, .NET 10, WPF, GitHub CLI

**Spec:** `docs/superpowers/specs/2026-08-26-release-please-design.md`

## Global Constraints

- Work only on `codex/release-please`, based on `f296126828d463660a9ab24bbe53adae3ecacf37` from `origin/master`.
- Do not modify, close, merge, or depend on pull request #4 or `codex/release-management`.
- Do not create `CHANGELOG.md`; Release Please must create it in the generated release pull request.
- Tags use exact names `vMAJOR.MINOR.PATCH` without a component prefix.
- `fix` means patch, `feat` means minor, and breaking changes mean major except that breaking changes below `1.0.0` mean minor.
- Normal `docs`, `style`, `refactor`, `test`, `chore`, `perf`, `ci`, and `build` commits do not trigger releases.
- Do not implement a custom SemVer or changelog parser.
- Preserve the existing Windows x64, framework-dependent, single-file publish contract.
- Pin every third-party action to a full commit SHA.
- Every commit uses Conventional Commit format and includes `Created with Codex` in its body.
- Use no em dash characters in repository text.

---

### Task 1: Bootstrap Release Please Configuration

**Files:**
- Create: `release-please-config.json`
- Create: `.release-please-manifest.json`
- Create: `version.txt`

**Interfaces:**
- Consumes: default branch name `master` and bootstrap commit `f296126828d463660a9ab24bbe53adae3ecacf37`
- Produces: a root Release Please package named `QuotaGlass`, initial version `0.0.0`, `vX.Y.Z` tags, draft releases, and semantic release pull request titles

- [ ] **Step 1: Record the failing bootstrap assertions**

Run this PowerShell check before creating the files:

```powershell
$required = @(
    'release-please-config.json',
    '.release-please-manifest.json',
    'version.txt'
)
$missing = @($required | Where-Object { -not (Test-Path -LiteralPath $_) })
if ($missing.Count -gt 0) {
    throw "Missing Release Please bootstrap files: $($missing -join ', ')"
}
```

Expected: FAIL listing all three missing files.

- [ ] **Step 2: Add the manifest and version file**

Create `.release-please-manifest.json` with root version `0.0.0`. Create
`version.txt` containing exactly `0.0.0` and a final newline.

- [ ] **Step 3: Add the Release Please configuration**

Create `release-please-config.json` with the official schema URL, full
`bootstrap-sha`, `release-type: simple`, `include-component-in-tag: false`,
`include-v-in-tag: true`, `bump-minor-pre-major: true`,
`bump-patch-for-minor-pre-major: false`, `draft: true`,
`force-tag-creation: true`, and release PR title pattern
`chore(release): release ${version}`. Configure only `feat` and `fix` normal
changelog sections. Configure root package `.` with package name `QuotaGlass`.

- [ ] **Step 4: Validate the configuration**

Parse both JSON files, validate the config against the official Release Please
schema in a temporary directory, assert both initial versions are `0.0.0`, and
confirm `CHANGELOG.md` remains absent.

- [ ] **Step 5: Commit the bootstrap configuration**

```powershell
git add release-please-config.json .release-please-manifest.json version.txt
git commit -m "ci(release): configure release please" -m "Created with Codex"
```

### Task 2: Validate Pull Request Titles and Version Inputs

**Files:**
- Create: `.github/workflows/pr-title.yml`

**Interfaces:**
- Consumes: pull request title and body from trusted `pull_request_target` metadata
- Produces: required check `Validate PR title`; rejects non-Conventional titles and `Release-As:` overrides without checking out pull request code

- [ ] **Step 1: Define the expected structural assertions**

Before creation, confirm the workflow is absent. Record the required trigger
events `opened`, `edited`, `synchronize`, `reopened`, and `ready_for_review`,
read-only pull request permission, zero checkout steps, semantic action SHA
`48f256284bd46cdaab1048c3721360e808335d50`, exact allowed types, and the
forbidden `Release-As:` body trailer.

- [ ] **Step 2: Create the metadata-only title workflow**

Use `amannn/action-semantic-pull-request` pinned to
`48f256284bd46cdaab1048c3721360e808335d50` with a `# v6.1.1` comment. Pass the
default `GITHUB_TOKEN`, configure exactly the project types, and require a
lowercase subject without a trailing period to match `AGENTS.md`.

Add a separate shell step that receives the pull request body through an
environment variable and fails on a case-insensitive line beginning with
`Release-As:`. Do not interpolate the body directly into shell source.

- [ ] **Step 3: Validate the workflow**

Run `actionlint` and inspect the parsed event, permissions, action SHA, and lack
of checkout steps. Confirm that `fix: correct polling` and
`feat(ui)!: change overlay contract` are valid while `Feature: Add release` is
invalid.

- [ ] **Step 4: Commit pull request validation**

```powershell
git add .github/workflows/pr-title.yml
git commit -m "ci(pr): validate conventional titles" -m "Created with Codex"
```

### Task 3: Add the Draft Release and Windows Publication Workflow

**Files:**
- Create: `.github/workflows/release-please.yml`

**Interfaces:**
- Consumes: Release Please config and manifest, action outputs `release_created`, `tag_name`, `version`, and `sha`, or a manually supplied recovery tag
- Produces: a published GitHub Release containing exactly the versioned Windows ZIP and SHA256 checksum

- [ ] **Step 1: Define the failure cases before implementation**

The workflow must refuse finalization for a manual tag that differs from the
manifest, a non-draft recovery release, an unresolved tag, any .NET failure,
mismatched DLL or EXE metadata, unexpected publish or ZIP contents, an invalid
checksum, a differing existing release asset, or an unexpected final asset set.

- [ ] **Step 2: Implement the release-management job**

Create push-to-`master` and recovery `workflow_dispatch` triggers plus one
non-canceling concurrency group. On pushes, run
`googleapis/release-please-action` pinned to
`45996ed1f6d02564a971a2fa1b5860e934307cf7` (`v5.0.0`) with explicit target
branch `master`. Grant only `contents: write`, `pull-requests: write`, and
`issues: write` to the job.

On recovery, check out `master`, require the input tag to equal `v` plus the
root manifest version, require the GitHub Release to be a draft, and resolve the
tag commit. Finish with one normalization step that exposes job outputs named
`publish-required`, `tag-name`, `version`, and `sha`. Never access another job's
step outputs directly.

- [ ] **Step 3: Implement the Windows package job**

Run only when the normalized publish flag equals the string `true`. Give the job
`contents: read`, check out the exact SHA, and set up .NET 10 with the same
action SHAs and environment variables as `.github/workflows/build.yml`.

Restore, build, test, and publish with the version properties from the spec on
both compilations. Verify the built DLL with `AssemblyName.GetAssemblyName` and
`FileVersionInfo`, verify the published EXE with `FileVersionInfo`, and require
the recursive publish inventory to contain only `QuotaGlass.exe`.

Create `QuotaGlass-vX.Y.Z-win-x64.zip`, require its only entry to be
`QuotaGlass.exe`, and write `QuotaGlass-vX.Y.Z-win-x64.sha256` as lowercase
SHA256, two spaces, and the ZIP name in UTF-8 without a byte-order mark. Upload
both as one 14-day workflow artifact with the same pinned upload action already
used by `build.yml` and no extra compression.

- [ ] **Step 4: Implement protected finalization**

Add a dependent Ubuntu job with `contents: write` and `environment: release`.
Download the package with `actions/download-artifact` pinned to
`3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c` (`v8.0.1`).

For each expected file, upload it when absent. When present, download it to a
temporary directory and compare SHA256 with the local candidate. Fail on a
mismatch without clobbering. Require the remote asset names to equal the ZIP
and checksum names, verify the checksum again, then publish the draft release.

- [ ] **Step 5: Validate and rehearse the workflow locally**

Run `actionlint`. Rehearse the exact .NET version arguments with `0.1.0`, inspect
DLL and EXE metadata, inspect publish and ZIP inventories recursively, and
validate the checksum. Exercise local asset comparison with zero, one matching,
two matching, and one mismatching fixture files.

- [ ] **Step 6: Commit release automation**

```powershell
git add .github/workflows/release-please.yml
git commit -m "ci(release): publish verified windows artifacts" -m "Created with Codex"
```

### Task 4: Document Operations and Required Settings

**Files:**
- Create: `docs/release-process.md`
- Modify: `README.md`

**Interfaces:**
- Consumes: implemented workflow names, check names, environment name, and artifact format
- Produces: contributor instructions, maintainer steps, repository setup, acceptance procedure, and recovery guidance

- [ ] **Step 1: Write the release-process documentation**

Document Conventional Commit version rules, squash behavior, release pull
request approval, bot-created workflow approval behavior, Windows artifact and
checksum formats, manual acceptance before approving the protected release
environment named `release`, draft recovery, immutable releases, and all required
GitHub settings. State that
a PAT is unnecessary for normal publication and that a GitHub App or token is
optional only for unattended extra workflows. Explain independence from pull
request #4's older design.

- [ ] **Step 2: Link the document from README**

Add a short `## Releases` section after `## Build`. Preserve all existing build
commands and framework-dependent single-file wording.

- [ ] **Step 3: Validate documentation consistency**

Search for stale manual tag instructions, custom changelog editing, em dash
characters, placeholders, and mismatched workflow or check names. Run
`git diff --check`.

- [ ] **Step 4: Commit documentation**

```powershell
git add README.md docs/release-process.md
git commit -m "docs(release): document release please operations" -m "Created with Codex"
```

### Task 5: Verify, Review, and Publish the Implementation Pull Request

**Files:**
- Modify only files already listed if verification or review finds defects

**Interfaces:**
- Consumes: all changes since `f296126828d463660a9ab24bbe53adae3ecacf37`
- Produces: pushed branch `codex/release-please` and an independent draft GitHub pull request

- [ ] **Step 1: Run full verification from a clean state**

Run:

```powershell
dotnet restore QuotaGlass.slnx
dotnet build QuotaGlass.slnx -c Release --no-restore
dotnet test QuotaGlass.slnx -c Release --no-build
dotnet publish src/QuotaGlass/QuotaGlass.csproj -c Release -p:PublishProfile=win-x64
```

Also run JSON Schema validation, `actionlint`, the version-stamped publish
rehearsal, metadata checks, ZIP inventory, checksum validation,
`git diff --check`, and the prohibited-character scan.

- [ ] **Step 2: Review the final diff**

Compare `origin/master...HEAD` against every specification requirement. Confirm
that `CHANGELOG.md` is absent, no pull request #4 files were copied, and existing
build workflow behavior is unchanged.

- [ ] **Step 3: Request an independent code review**

Give the reviewer the exact base and head SHAs, specification, requirements,
verification evidence, and read-only instructions. Fix valid critical and
important findings and rerun affected verification.

- [ ] **Step 4: Push the branch**

```powershell
git push --set-upstream origin codex/release-please
```

- [ ] **Step 5: Run a read-only Release Please dry run**

Use the Release Please CLI version bundled by the pinned action, target the
pushed `codex/release-please` branch, and enable dry-run mode. Confirm the
manifest and configuration load without mutation. Explain that the default
branch will receive the implementation as one `feat(release): ...` squash
commit, yielding the expected initial `0.1.0` release.

- [ ] **Step 6: Open a separate draft pull request**

Use title `feat(release): automate releases with release please`. Include the
design, exact version rules, verification results, required settings, remaining
manual setup, independence from pull request #4, and `Created with Codex` in the
description. Target `master` and leave the pull request in draft state.
