# Provider Availability Discovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace provider enable flags with runtime local availability discovery and omit unavailable providers from polling and UI reports.

**Architecture:** Built-in providers implement `IProviderAvailability`, which checks only local files, tools, or services. `StatusPoller` evaluates availability before cooldown handling and remote quota fetching, then publishes only available providers.

**Tech Stack:** .NET 10, C# 14, WPF, xUnit, System.Text.Json

**Spec:** `docs/superpowers/specs/2026-08-27-provider-availability-discovery-design.md`

## Global Constraints

- Work only in the isolated `codex/issue-22-availability-discovery` worktree based on merge commit `088eb3c723291a0bceb2df3ee09d238aec3f40a9`.
- Do not log credentials, raw account identifiers, workspace identifiers, or credential contents.
- Do not call remote quota endpoints when local prerequisites are unavailable.
- Preserve OpenCode Console configuration independently from provider availability.
- Use four-space C# indentation and keep warnings as errors.
- Run the full Release build, test, and publish validation before opening the pull request.

---

### Task 1: Migrate provider settings

**Files:**
- Modify: `src/QuotaGlass/Core/AppSettings.cs`
- Modify: `src/QuotaGlass/Core/SettingsStore.cs`
- Modify: `tests/QuotaGlass.Tests/Core/SettingsStoreTests.cs`
- Modify: `tests/QuotaGlass.Tests/Core/ApplicationCompositionTests.cs`
- Modify: `tests/QuotaGlass.Tests/Providers/ProviderRegistryTests.cs`

**Interfaces:**
- Produces: `ProviderSettings` with `OpenCodeConsoleSettings? OpenCodeConsole { get; init; }` and no provider-level `Enabled` property.
- Preserves: `OpenCodeConsoleSettings(string? WorkspaceSelector)`.

- [ ] **Step 1: Write failing migration and default tests**

Add tests that deserialize legacy provider objects containing `Enabled`, assert all built-ins exist without consulting the old values, preserve `OpenCodeConsole`, save the loaded settings, and assert the saved JSON has no provider-level `Enabled` property.

- [ ] **Step 2: Run the focused settings tests and verify failure**

Run: `dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj -c Release --filter FullyQualifiedName~SettingsStoreTests`

Expected: FAIL because `ProviderSettings.Enabled` still exists and saved JSON still contains it.

- [ ] **Step 3: Remove the provider flag and normalize defaults**

Change the provider model to:

```csharp
public sealed record ProviderSettings
{
    public OpenCodeConsoleSettings? OpenCodeConsole { get; init; }
}
```

Create every default provider entry with `new ProviderSettings()`. Keep the existing OpenCode Console validation and unknown-provider preservation in `SettingsStore.TryNormalize`.

- [ ] **Step 4: Update affected construction tests and run the focused suite**

Replace provider-level boolean construction with `new ProviderSettings()` and keep Console-specific booleans unchanged.

Run: `dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj -c Release --filter "FullyQualifiedName~SettingsStoreTests|FullyQualifiedName~ApplicationCompositionTests|FullyQualifiedName~ProviderRegistryTests"`

Expected: PASS.

- [ ] **Step 5: Commit the migration**

```powershell
git add src/QuotaGlass/Core/AppSettings.cs src/QuotaGlass/Core/SettingsStore.cs tests/QuotaGlass.Tests/Core/SettingsStoreTests.cs tests/QuotaGlass.Tests/Core/ApplicationCompositionTests.cs tests/QuotaGlass.Tests/Providers/ProviderRegistryTests.cs
git commit -m "refactor(core): remove provider enable settings"
```

### Task 2: Add explicit built-in availability probes

**Files:**
- Create: `src/QuotaGlass/Providers/IProviderAvailability.cs`
- Create: `src/QuotaGlass/Providers/CommandAvailability.cs`
- Modify: `src/QuotaGlass/Providers/ClaudeProvider.cs`
- Modify: `src/QuotaGlass/Providers/CodexProvider.cs`
- Modify: `src/QuotaGlass/Providers/OpenCodeGoProvider.cs`
- Modify: `src/QuotaGlass/Providers/OpenCodeCompanySeatProvider.cs`
- Modify: `src/QuotaGlass/Providers/OllamaProvider.cs`
- Modify: corresponding provider tests under `tests/QuotaGlass.Tests/Providers/`

**Interfaces:**
- Produces: `Task<bool> IProviderAvailability.IsAvailableAsync(CancellationToken cancellationToken)`.
- Produces: `CommandAvailability.IsAvailable(string commandName)` for sanitized PATH and PATHEXT discovery.
- Consumes: existing credential paths, OpenCode Console settings, and provider HTTP handlers.

- [ ] **Step 1: Write failing availability tests for every built-in provider**

Cover credential file present and absent for Claude and Codex, API credential and Console command alternatives for OpenCode Go, command presence for OpenCode Company Seat, and local response versus connection failure for Ollama. Use injected delegates and fake handlers so tests never access real credentials or remote services.

- [ ] **Step 2: Run focused provider tests and verify failure**

Run: `dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj -c Release --filter "FullyQualifiedName~ClaudeProviderTests|FullyQualifiedName~CodexProviderTests|FullyQualifiedName~OpenCodeGoProviderTests|FullyQualifiedName~OpenCodeCompanySeatProviderTests|FullyQualifiedName~OllamaProviderTests"`

Expected: FAIL because the availability contract and methods do not exist.

- [ ] **Step 3: Implement the availability contract and command discovery**

Create:

```csharp
public interface IProviderAvailability
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);
}
```

Implement command discovery by scanning non-empty PATH entries for the command name and PATHEXT candidates. Do not execute a shell and do not include discovered paths in logs.

- [ ] **Step 4: Implement provider-specific probes**

Claude and Codex use injected `Func<string, bool>` file checks. OpenCode Go returns true for an API credential file or an available local `opencode` command. OpenCode Company Seat checks the same local command. Ollama sends a GET to its local version URI and returns false only for connection-level `HttpRequestException`; cancellation still propagates.

- [ ] **Step 5: Run focused provider tests**

Run the command from Step 2.

Expected: PASS.

- [ ] **Step 6: Commit availability probes**

```powershell
git add src/QuotaGlass/Providers tests/QuotaGlass.Tests/Providers
git commit -m "feat(providers): discover local provider availability"
```

### Task 3: Filter unavailable providers before polling

**Files:**
- Modify: `src/QuotaGlass/Core/StatusPoller.cs`
- Modify: `tests/QuotaGlass.Tests/Core/StatusPollerTests.cs`
- Modify: `tests/QuotaGlass.Tests/Providers/ProviderPollerIntegrationTests.cs`

**Interfaces:**
- Consumes: `IProviderAvailability.IsAvailableAsync(CancellationToken)`.
- Produces: `StatusReport` values containing only providers available during the current poll.

- [ ] **Step 1: Write failing poller tests**

Add an availability-aware fake provider with a mutable availability delegate and fetch count. Test unavailable omission, zero fetches while unavailable, unavailable to available to unavailable transitions, and an unavailable provider next to a normally fetched provider.

- [ ] **Step 2: Run focused poller tests and verify failure**

Run: `dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj -c Release --filter "FullyQualifiedName~StatusPollerTests|FullyQualifiedName~ProviderPollerIntegrationTests"`

Expected: FAIL because the poller still reads `ProviderSettings.Enabled` and publishes disabled snapshots.

- [ ] **Step 3: Evaluate availability before cooldown and fetch logic**

Remove `IsEnabled` and the disabled snapshot path. Add a timeout-bound availability check before cooldown handling. Treat timeout or discovery exceptions as unavailable for that cycle, log only the safe provider ID plus outcome and exception type, and clear cooldown and retention state when a provider is unavailable.

- [ ] **Step 4: Omit unavailable attempts from reports**

Filter unavailable attempts before `ApplyAttempt`, then publish the remaining snapshots in registry order. Providers that implement only `IStatusProvider` remain available by default.

- [ ] **Step 5: Run focused poller tests**

Run the command from Step 2.

Expected: PASS.

- [ ] **Step 6: Commit poller filtering**

```powershell
git add src/QuotaGlass/Core/StatusPoller.cs tests/QuotaGlass.Tests/Core/StatusPollerTests.cs tests/QuotaGlass.Tests/Providers/ProviderPollerIntegrationTests.cs
git commit -m "refactor(core): filter unavailable providers"
```

### Task 4: Verify UI report omission

**Files:**
- Modify: `tests/QuotaGlass.Tests/Ui/TrayIconHostTests.cs`

**Interfaces:**
- Consumes: filtered `StatusReport.Providers`.
- Verifies: popup and overlay receive the same filtered provider sequence.

- [ ] **Step 1: Add a UI delivery regression test**

Raise a report containing only an available provider after a prior report containing multiple providers. Assert both fake windows replace their collections and contain only the available provider.

- [ ] **Step 2: Run the UI test and verify behavior**

Run: `dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj -c Release --filter FullyQualifiedName~TrayIconHostTests`

Expected: PASS because the existing delivery path already replaces both collections. The test locks in the issue requirement without adding view-level filtering.

- [ ] **Step 3: Commit UI coverage**

```powershell
git add tests/QuotaGlass.Tests/Ui/TrayIconHostTests.cs
git commit -m "test(ui): cover unavailable provider omission"
```

### Task 5: Validate locally and prepare the pull request

**Files:**
- Modify only files required by review findings.

**Interfaces:**
- Verifies: local OpenCode command discovery and application composition on this machine.
- Verifies: complete Release build, test, and win-x64 publish output.

- [ ] **Step 1: Validate the local OpenCode prerequisite without exposing data**

Run `Get-Command opencode` and `opencode --version`. Run a count-only `opencode db` query that returns no credential or identifier columns. Confirm the new command discovery reports available through a focused integration invocation.

- [ ] **Step 2: Run subagent specification and code-quality reviews**

Provide reviewers with issue #22, the design, the implementation plan, and the exact base and head SHAs. Resolve every Critical and Important finding, then request scoped re-review of fixes.

- [ ] **Step 3: Run full verification**

```powershell
dotnet restore QuotaGlass.slnx
dotnet build QuotaGlass.slnx -c Release --no-restore
dotnet test QuotaGlass.slnx -c Release --no-build
dotnet publish src/QuotaGlass/QuotaGlass.csproj -c Release -p:PublishProfile=win-x64
```

Expected: 0 build warnings, 0 test failures, and one framework-dependent win-x64 `QuotaGlass.exe` in the publish output.

- [ ] **Step 4: Push and open the pull request**

Push `codex/issue-22-availability-discovery`. Open a pull request against `master` titled `refactor(core): replace provider enable flags with availability discovery`. Include outcome, `Closes #22`, exact verification commands, local OpenCode validation, and `Created with Codex`.
