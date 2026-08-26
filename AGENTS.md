# Repository Guidelines

## Project Structure & Module Organization

QuotaGlass is a Windows x64 tray application built with .NET 10, C# 14, WPF, and limited WinForms tray APIs. Production code lives in `src/QuotaGlass/`: `Core/` handles polling, settings, and composition; `Providers/` integrates Claude, Codex, and Ollama; `Model/` contains shared domain types; `Platform/` wraps Windows services; and `Ui/` contains windows, controls, converters, and positioning logic. Tests mirror these areas under `tests/QuotaGlass.Tests/`. Keep sanitized provider samples in `tests/QuotaGlass.Tests/Fixtures/`, release checks in `docs/`, and CI changes in `.github/workflows/`.

## Build, Test, and Development Commands

Run commands from the repository root in PowerShell:

```powershell
dotnet restore QuotaGlass.slnx
dotnet build QuotaGlass.slnx -c Release --no-restore
dotnet test QuotaGlass.slnx -c Release --no-build
dotnet run --project src/QuotaGlass/QuotaGlass.csproj
dotnet publish src/QuotaGlass/QuotaGlass.csproj -c Release -p:PublishProfile=win-x64
```

Restore dependencies before the first build. Use the Release build and test sequence before submitting changes. Publishing must produce a single framework-dependent `QuotaGlass.exe` for Windows x64.

## Coding Style & Naming Conventions

Use four-space indentation in C# and preserve the existing XAML formatting. Nullable references, implicit usings, deterministic builds, and warnings-as-errors are enabled globally. Use PascalCase for types, members, and public properties; camelCase for parameters and locals; and `_camelCase` for private fields. Keep platform calls behind `Platform/` abstractions and provider-specific behavior inside `Providers/`. Prefer small, focused classes and cancellation-aware asynchronous methods.

## Testing Guidelines

Tests use xUnit. Name test classes after the subject, such as `StatusPollerTests`, and methods as `Member_ExpectedBehavior` or `Member_Condition_ExpectedBehavior`. Add regression coverage for behavior changes and fixture-based tests for provider parsing. Run the full Release suite; UI tests may require a Windows desktop/STA context. Follow `docs/manual-test-checklist.md` for release candidates.

## Commit, Pull Request & Release Guidelines

Use Conventional Commits for commits and pull request titles, for example
`fix(core): prevent overlapping polls` or `test(ui): cover overlay placement`.
Keep commits focused. Pull requests are squash merged, and the pull request
title becomes the Conventional Commit message consumed by Release Please. Keep
the title accurate through merge and use only the configured types: `feat`,
`fix`, `docs`, `style`, `refactor`, `test`, `chore`, `perf`, `ci`, and `build`.

Version effects are:

- `fix` increments the patch version.
- `feat` increments the minor version.
- `!` in the title or a `BREAKING CHANGE:` footer increments the major version.
- Below `1.0.0`, a breaking change increments the minor version instead.
- `docs`, `style`, `refactor`, `test`, `chore`, `perf`, `ci`, and `build` do not
  trigger a release by themselves.

Prefer `!` in the title for breaking changes. When using a `BREAKING CHANGE:`
footer, keep it in the pull request body because that body becomes the squash
commit body. Do not use `Release-As:` or `BEGIN_COMMIT_OVERRIDE` directives.

Pull requests should explain the user-visible outcome, link relevant issues,
list verification commands, and include screenshots for UI changes. Ensure
build, tests, publish validation, pull request title validation, and the CI
Gitleaks scan pass.

Release Please owns `.release-please-manifest.json`, `version.txt`, generated
`CHANGELOG.md`, version tags, and GitHub Releases during normal release work.
Do not manually create or edit those release outputs. Review and squash merge
the generated release pull request as the human approval gate. Follow
`docs/release-process.md` for the complete contributor, release, recovery, and
repository-settings flow.

## Security & Configuration

Never commit credentials, tokens, account identifiers, live API responses, or sensitive logs. Sanitize all fixtures and screenshots. Runtime configuration and logs belong under `%APPDATA%\QuotaGlass\`; do not redirect them into the repository. Report vulnerabilities through the private process in `SECURITY.md`.
