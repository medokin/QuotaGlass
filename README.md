# QuotaGlass

QuotaGlass is a Windows tray application that shows Claude, Codex, and Ollama
status at a glance. It displays current usage windows in a tray popup, can keep
an optional overlay above other windows, and raises notifications when usage
crosses configured thresholds.

## Features

- Claude and Codex subscription usage windows
- Local Ollama version and running-model status
- Tray status based on the most urgent provider state
- Optional movable, always-on-top overlay
- Configurable warning and critical thresholds
- Global `Ctrl+Alt+A` overlay shortcut
- Optional start with Windows

## Requirements

- Windows 10 version 2004 or newer on x64
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- Claude Code and/or Codex CLI already authenticated for their respective cards
- Ollama running locally for the Ollama card

QuotaGlass reads the credential files created by Claude Code and Codex CLI. It
does not write those files, refresh tokens, or log credentials and API response
bodies. Providers can be disabled in the local settings file.

## Build

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0),
then run:

```powershell
dotnet restore QuotaGlass.slnx
dotnet build QuotaGlass.slnx -c Release --no-restore
dotnet test QuotaGlass.slnx -c Release --no-build
dotnet publish src/QuotaGlass/QuotaGlass.csproj -c Release -p:PublishProfile=win-x64
```

The publish profile produces a framework-dependent, single-file
`QuotaGlass.exe` for Windows x64.

## Releases

QuotaGlass uses Semantic Versioning and immutable annotated tags named
`vMAJOR.MINOR.PATCH`. The tag is the source of truth for the binary version,
artifact names, changelog entry, and GitHub Release.

To prepare a release:

1. Move the relevant entries in [`CHANGELOG.md`](CHANGELOG.md) from
   `Unreleased` to `## [MAJOR.MINOR.PATCH] - YYYY-MM-DD`. Use the UTC date on
   which the release workflow will first run, then recreate the empty
   `Unreleased` section and update the comparison links.
2. Complete [`docs/manual-test-checklist.md`](docs/manual-test-checklist.md)
   against the release candidate.
3. Merge the release commit to `master`.
4. Run the `Release` workflow manually with the planned tag to validate the
   version, changelog, build, tests, package, and checksum without creating a
   tag or GitHub Release.
5. Create and push the annotated tag from the validated commit:

   ```powershell
   git tag -a v0.1.0 -m "release: v0.1.0"
   git push origin v0.1.0
   ```

The tag-triggered workflow repeats all validation and publishes the matching
GitHub Release. It uses that version's changelog section as the release notes
and attaches the versioned Windows x64 ZIP and SHA-256 manifest.

Never move or reuse a published release tag. Release corrections under a new
patch version.

## Configuration

QuotaGlass creates `%APPDATA%\QuotaGlass\settings.json` on first launch. The
defaults poll once per minute while active, use 80 and 95 percent warning
thresholds, keep the overlay hidden, and leave autostart disabled.

Runtime logs are written to `%APPDATA%\QuotaGlass\log.txt`. Logs contain status
categories only, not exception messages, headers, bodies, tokens, account IDs,
or email addresses.

## Security

Do not include credentials, account identifiers, or live API responses in bug
reports. See [SECURITY.md](SECURITY.md) for private vulnerability reporting.

## License

QuotaGlass is available under the [MIT License](LICENSE).
