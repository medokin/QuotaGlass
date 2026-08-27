# Release Process

QuotaGlass uses Release Please to derive versions and changelog entries from
squash-merged Conventional Commit pull requests. The generated release pull
request is the human approval gate for the version and release notes. The same
workflow creates the GitHub Release and publishes the verified Windows assets,
so a personal access token is not required for normal releases.

This process replaces the older manual-tag proposal in issue #2 and pull
request #4. It does not use files or commits from pull request #4.

## Version rules

The pull request title becomes the squash commit title and selects the next
version:

| Squash commit | Version effect |
|---|---|
| `fix: ...` | Patch |
| `feat: ...` | Minor |
| A title containing `!` | Major |
| A body containing a `BREAKING CHANGE:` footer | Major |
| `docs`, `style`, `refactor`, `test`, `chore`, `perf`, `ci`, or `build` | No release by itself |

While the current version is below `1.0.0`, a breaking change increments the
minor version instead of the major version. A `feat` remains a minor change
below `1.0.0`.

Use `!` in the pull request title for a breaking change when practical:

```text
feat(settings)!: replace the configuration format
```

Alternatively, add a Conventional Commit footer to the pull request body:

```text
BREAKING CHANGE: existing settings files must be recreated
```

The repository retains the pull request body as the squash commit body so this
footer reaches Release Please. `Release-As:`, `BEGIN_COMMIT_OVERRIDE`, and
`END_COMMIT_OVERRIDE` are forbidden anywhere in pull request bodies, including
quoted text and examples. Release Please scans the complete body for these raw
control markers, which can bypass version policy or replace the commit message.

## Contributor flow

1. Open a pull request with a Conventional Commit title.
2. Update the title when the change type or compatibility impact changes.
3. Merge with squash merge after required reviews and checks pass.
4. Confirm that the squash commit title is the pull request title before
   completing the merge.

The `Pull request title` workflow validates pull request metadata with
`amannn/action-semantic-pull-request`. It does not check out or execute pull
request code.

## Release flow

1. A push to `master` runs the `Release Please` workflow.
2. Release Please reads commits after the last release and creates or updates a
   release pull request titled `chore(release): release X.Y.Z`.
3. The release pull request updates `.release-please-manifest.json`,
   `version.txt`, and the generated `CHANGELOG.md`.
4. A maintainer reviews the proposed version and user-facing changelog, then
   deliberately squash merges the release pull request. That merge is the
   version and changelog approval gate in the current solo-maintainer setup.
5. The merge reruns `Release Please`. It creates a `vX.Y.Z` tag and a draft
   GitHub Release.
6. The workflow checks out the tagged commit on `windows-latest`, restores,
   builds, tests, and publishes QuotaGlass with the release version stamped into
   the assembly and executable metadata.
7. The workflow packages and uploads a release-candidate artifact containing:

   - `QuotaGlass-vX.Y.Z-win-x64.zip`
   - `QuotaGlass-vX.Y.Z-win-x64.sha256`

8. Download the workflow artifact and perform the applicable checks from
   [manual-test-checklist.md](manual-test-checklist.md) against that exact
   candidate. Record results without credentials, account data, or live usage
   data.
9. Approve the protected `release` environment after acceptance. The final job
   attaches the verified assets and publishes the draft GitHub Release, at
   which point release immutability locks the tag and assets.

The ZIP contains only `QuotaGlass.exe`. The checksum file hashes the ZIP and
uses this format:

```text
<lowercase-sha256>  QuotaGlass-vX.Y.Z-win-x64.zip
```

Published tags, releases, and assets are immutable. Correct a published release
with a new patch release. Do not move tags, replace assets, or republish an
existing version. The protected environment reviewer confirms that repository
release immutability is enabled, and the workflow verifies the immutable state
after publication.

## Draft release recovery

Build, test, acceptance, or upload failures leave the GitHub Release in draft
state. Fix the underlying cause before recovery.

1. Open the `Release Please` workflow in GitHub Actions.
2. Select `Run workflow` from `master`.
3. Enter the existing draft tag, such as `v0.1.0`.
4. Start the run.
5. Repeat candidate acceptance and approve the `release` environment.

Recovery is intentionally narrow. The workflow requires the tag to match the
root version in `.release-please-manifest.json`, requires an existing draft
GitHub Release, and requires the tagged commit to be contained in `master`.

The finalization job safely handles a previous partial upload. It uploads a
missing asset, accepts an existing asset only when its SHA256 matches the new
candidate, and fails on a mismatch. It never overwrites an asset. The draft is
published only after its asset list contains exactly the expected ZIP and
checksum.

Do not merge another generated release pull request while a draft release is
awaiting recovery. Workflow concurrency serializes the automated release lane,
but maintainers must also preserve the manifest-to-draft relationship.

## Required GitHub repository settings

Configure the merge and Actions settings before merging the implementation pull
request. Configure the protected release environment and add the new title
check to branch protection immediately after the implementation merges and
before merging the first generated release pull request.

### Pull request merges

Under **Settings > General > Pull Requests**:

- Enable squash merging.
- Disable merge commits.
- Disable rebase merging.
- Set the default squash commit title to the pull request title.
- Set the default squash commit message to the pull request body.

These settings ensure the validated title and any `BREAKING CHANGE:` footer are
the Conventional Commit consumed by Release Please.

### Actions permissions

Under **Settings > Actions > General > Workflow permissions**:

- Keep the default workflow permission read-only.
- Enable **Allow GitHub Actions to create and approve pull requests**.

The workflows request job-level write permissions only where needed. Release
Please receives `actions: write` and `checks: write` for generated pull request
checks, plus `contents: write`, `pull-requests: write`, and `issues: write` for
release management. Artifact finalization receives `contents: write`. Build and
title-validation jobs remain read-only.

For a generated release pull request, the trusted Release Please workflow
verifies the expected title, body, source, target, and three generated files. It
then creates the required title check on the release pull request commit and
uses `workflow_dispatch` to run the normal build workflow on that commit.
The `workflow_dispatch` event is an allowed `GITHUB_TOKEN` trigger. The trusted
workflow waits for the returned run ID and verifies the exact workflow, branch,
commit, event, bounded jobs, and successful conclusions. GitHub omits these
dispatched job checks from the pull request rollup, so the trusted workflow
creates required check-run attestations on the release pull request commit and
links each attestation to its original job. Required checks therefore run
without a personal access token. The normal pull request trigger ignores changes
limited to the three Release Please generated files, preventing a duplicate
approval-required run. Pull requests that change any other file still run the
normal build workflow.

If the trusted check bridge times out or an API call fails, rerun the failed
`Release Please` workflow. The push path rediscovers the existing open generated
pull request, obtains a new build run ID directly from GitHub, and safely
recreates checks only for that pull request's current verified head SHA.

A GitHub App token or fine-grained personal access token is optional only if
maintainers later require every automatic pull request event to run unattended.
It is not required to create the release, run the required release pull request
checks, or publish the artifacts.

### Branch protection

Protect `master` with a branch protection rule or repository ruleset that:

- Requires a pull request before merging.
- Requires the `Secret scan`, `Build, test, and publish`, and
  `Validate PR title` checks. Add `Validate PR title` after that job has run at
  least once on the default branch.
- Requires conversations to be resolved.
- Disallows force pushes and deletion.
- Prevents direct pushes and has no routine bypass actors.

QuotaGlass currently has one maintainer, so the `Protect master` ruleset uses
zero required pull request approvals. A maintainer still has to deliberately
merge the generated release pull request, which is the human version and
changelog gate. If a second trusted maintainer is added, require at least one
approval and require approval of the latest reviewable push.

The Release Please workflow dispatches and verifies the required build jobs,
then records their checks and the trusted title check on generated release pull
requests. The generated-file path filter prevents a duplicate automatic run
without weakening checks on any other pull request change.

### Release immutability

Enable **Release immutability** in the repository settings before approving the
first release. This GitHub setting locks the tag and release assets after
publication. GitHub does not allow the workflow's `GITHUB_TOKEN` to read this
administrative setting, so the protected environment reviewer must confirm it
before approval. The publication job verifies that the resulting release is
immutable.

### Release environment

Create an environment named `release` under **Settings > Environments**:

- Add `medokin` as the required reviewer while the repository has one
  maintainer.
- Allow self-review for the solo-maintainer setup.
- Limit deployment branches to `master`.

If a second trusted maintainer is added, add that maintainer as a reviewer and
prevent self-review.

The reviewer approves publication only after testing the exact workflow
artifact and confirming release immutability is enabled. The environment
approval is separate from the release pull request approval because the final
Windows binary is built from the merged, tagged commit.

## Initial release

Release Please uses `0.0.1` as its bootstrap baseline and ignores commits at or
before the configured bootstrap commit. The baseline avoids an upstream
Release Please bug where `0.0.0` ignores pre-major bump options and proposes
`1.0.0`. Squash merging the implementation pull request with its
`feat(release): ...` title should produce the first release pull request for
`0.1.0`. `CHANGELOG.md` first appears in that generated pull request.
