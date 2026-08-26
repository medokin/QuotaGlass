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

## Commit & Pull Request Guidelines

Use Conventional Commits matching repository history, for example `fix(core): prevent overlapping polls` or `test(ui): cover overlay placement`. Keep commits focused. Pull requests should explain the user-visible outcome, link relevant issues, list verification commands, and include screenshots for UI changes. Ensure build, tests, publish validation, and the CI Gitleaks scan pass.

## Security & Configuration

Never commit credentials, tokens, account identifiers, live API responses, or sensitive logs. Sanitize all fixtures and screenshots. Runtime configuration and logs belong under `%APPDATA%\QuotaGlass\`; do not redirect them into the repository. Report vulnerabilities through the private process in `SECURITY.md`.
