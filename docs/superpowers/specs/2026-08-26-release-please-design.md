# Release Please Management Design

## Goal

Add Release Please based release management for QuotaGlass without depending on
the custom release implementation in pull request #4. Conventional Commit
messages select versions, a generated release pull request provides the human
approval gate, and the same GitHub Actions workflow creates and publishes the
GitHub Release and its Windows artifacts.

## Starting Point

The implementation branch starts from the current `origin/master` commit
`f296126828d463660a9ab24bbe53adae3ecacf37`. This full commit ID is the Release
Please bootstrap boundary, so only commits after it contribute to the first
generated release.

The repository has no existing tags or GitHub Releases. Release Please starts
from version `0.0.0`. The implementation pull request will be squash merged with
a `feat(release): ...` title, so its expected first proposed release is `0.1.0`.

Pull request #4 and branch `codex/release-management` remain unchanged. They are
context for the older manual-tag design only.

## Version and Changelog Policy

Release Please parses the squash commit created from each pull request:

- `fix` increments the patch version.
- `feat` increments the minor version.
- `!` or a `BREAKING CHANGE:` footer increments the major version.
- Below `1.0.0`, breaking changes increment the minor version.
- Other allowed commit types do not create a release on their own.
- `Release-As:` overrides are forbidden because they bypass this mapping.

The allowed non-release types match the repository guidance: `docs`, `style`,
`refactor`, `test`, `chore`, `perf`, `ci`, and `build`.

`release-please-config.json` uses the `simple` strategy for the repository root,
disables component prefixes in tags, includes the `v` prefix, enables
`bump-minor-pre-major`, and limits normal changelog sections to features and bug
fixes. Breaking entries continue to be represented by Release Please. The
release pull request title follows `chore(release): release X.Y.Z`.

`.release-please-manifest.json` and `version.txt` both begin at `0.0.0`.
Release Please updates both version records and creates `CHANGELOG.md` in its
generated release pull request. This implementation does not pre-create the
changelog.

## Pull Request Contract

`.github/workflows/pr-title.yml` uses a commit-SHA-pinned
`amannn/action-semantic-pull-request` v6 action on `pull_request_target` events.
It validates titles without checking out or executing pull request code. The
workflow also rejects `Release-As:` trailers in pull request descriptions.

The repository must allow squash merging only and use the pull request title as
the squash commit title. The pull request description is retained as the squash
commit body so a `BREAKING CHANGE:` footer remains available to Release Please.
Using `!` in the title is the preferred breaking-change notation because it is
also covered by title validation.

The Release Please pull request is never auto-merged. Protection on `master`
must require a pull request, at least one human approval, and the existing build
and new title checks. Direct pushes and bypasses must be prevented or limited to
explicit administrators.

## Release Workflow

`.github/workflows/release-please.yml` runs for pushes to `master`. It also has
a manual recovery entry point for a specific existing draft release tag. A
workflow-level concurrency group serializes the release lane and never cancels
an in-progress release.

### Release Management Job

An Ubuntu job runs the commit-SHA-pinned `googleapis/release-please-action` v5
with job-scoped `contents: write`, `pull-requests: write`, and `issues: write`
permissions. Normal pushes explicitly target `master`.

For feature and maintenance pushes, Release Please creates or updates the
release pull request. No publication jobs run.

After a maintainer merges the release pull request, Release Please creates an
immutable `vMAJOR.MINOR.PATCH` tag and a draft GitHub Release. Draft release
creation and forced tag creation ensure a failed build never exposes an
assetless public release and provide a stable recovery target.

The job normalizes the action outputs into explicit job outputs:

- whether packaging is required
- tag name
- version without `v`
- tagged commit SHA

A manual recovery run accepts a tag for an existing draft Release Please
release. It checks out `master`, confirms that the tag matches the root version
in `.release-please-manifest.json`, confirms that the GitHub Release is still a
draft, and resolves the immutable tagged commit. It does not ask Release Please
to create another release.

### Windows Package Job

A dependent `windows-latest` job runs only for a newly created release or a
validated recovery. It checks out the exact tagged commit and uses .NET 10.

The job restores, builds, tests, and publishes the existing framework-dependent,
single-file Windows x64 application. The Release Please version output is passed
to every compilation that contributes to the publish result:

- `Version=X.Y.Z`
- `AssemblyVersion=X.0.0.0`
- `FileVersion=X.Y.Z.0`
- `InformationalVersion=X.Y.Z`
- `IncludeSourceRevisionInInformationalVersion=false`

The job verifies assembly and artifact metadata before packaging:

- `QuotaGlass.dll` has the expected assembly, file, product, and informational
  versions.
- The published `QuotaGlass.exe` has the expected Windows file and product
  versions.
- The recursive publish inventory contains only `QuotaGlass.exe`.
- The ZIP inventory contains only `QuotaGlass.exe`.
- The checksum hashes the ZIP, not the executable.

The resulting files are:

- `QuotaGlass-vMAJOR.MINOR.PATCH-win-x64.zip`
- `QuotaGlass-vMAJOR.MINOR.PATCH-win-x64.sha256`

The checksum file uses lowercase SHA256 followed by two spaces and the ZIP file
name. Both files are uploaded together as an immutable workflow artifact.

### Acceptance and Finalization Job

The final job uses a protected GitHub `release` environment. Its required
reviewer is the manual acceptance gate for the exact packaged candidate. Before
approving the environment, a maintainer downloads the workflow artifact,
records the applicable results in `docs/manual-test-checklist.md`, and confirms
repository release immutability is enabled.

After approval, the job downloads the exact workflow artifact and reconciles
the two release assets. Missing assets are uploaded individually. Existing
assets are downloaded and accepted only when their SHA256 matches the local
candidate. Assets are never overwritten. The job verifies that the draft
release contains exactly the expected ZIP and checksum, then publishes the
draft release.

If packaging, acceptance, or upload fails, the GitHub Release remains a draft.
A maintainer fixes the cause and starts the documented recovery dispatch for
that tag. The recovery path rebuilds the tagged commit and safely resumes after
zero, one, or two matching assets.

## Repository Settings

The following settings are prerequisites and are documented as manual setup:

1. Enable squash merging and disable merge commits and rebasing.
2. Set the default squash commit title to the pull request title and the default
   squash body to the pull request body.
3. Protect `master` with required pull requests, at least one approval, required
   build and title-validation checks, and controlled bypasses.
4. Allow GitHub Actions to create pull requests.
5. Keep default workflow permissions read-only. The workflows request narrower
   job-level write permissions where required.
6. Create a protected `release` environment with a required human reviewer.
7. Enable repository release immutability before the first publication.

Bot-created Release Please pull request workflow runs may require maintainer
approval under GitHub's current token policy. The documentation explains this
instead of requiring a personal access token. A GitHub App or fine-grained token
is optional only if maintainers later want unattended execution of additional
workflows on bot-created pull requests.

## Security and Failure Handling

- Third-party actions are pinned to full commit SHAs.
- Pull request title validation never checks out untrusted code.
- The default `GITHUB_TOKEN` is used with job-scoped permissions.
- No credentials, responses, or runtime data enter release artifacts.
- A release remains a draft until the exact Windows candidate is verified.
- Published tags and releases are immutable. Corrections use a new patch
  release.
- Finalization binds the draft release and tag to the exact packaged commit SHA
  and verifies GitHub's immutable state after publication.
- Asset reconciliation verifies content and never uses a clobber option.
- Recovery only accepts the manifest's current version and an existing draft
  release.

## Verification

Implementation verification covers:

- JSON parsing and validation against the official Release Please schema.
- YAML parsing and `actionlint` checks for both workflows.
- Inspection of the pinned action revisions and their documented outputs.
- Conventional Commit configuration checks for fix, feat, breaking, and
  non-release types without adding a custom SemVer parser.
- Release build and all xUnit tests.
- A version-stamped local publish rehearsal.
- DLL and EXE metadata checks.
- ZIP inventory and SHA256 verification.
- Recovery reconciliation tests for missing and already-present matching assets.
- A final diff review against this design and the user requirements.

## Documentation

`docs/release-process.md` describes the contributor flow, release pull request
approval, manual candidate acceptance, recovery procedure, immutable release
policy, artifact format, and required GitHub settings. `README.md` links to the
release documentation without replacing the existing build instructions.
