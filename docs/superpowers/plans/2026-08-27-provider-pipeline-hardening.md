# Provider Pipeline Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make all providers report explicit fetch outcomes so polling failures retain last-good data, rate limits enforce bounded cooldowns, old settings remain compatible, and HTTP JSON reads are safe.

**Architecture:** Providers return a small `ProviderFetchResult` that separates the outcome from an optional fresh `ProviderSnapshot`. `StatusPoller` owns the single transition table, per-provider cooldown state, and one-slot refresh coalescing. A focused HTTP helper validates JSON content types, bounds decompressed reads to 1 MiB, and parses safe `Retry-After` values. `SettingsStore` normalizes missing compiled provider defaults while preserving existing and unknown entries.

**Tech Stack:** .NET 10, C# 14, WPF application core, `HttpClient`, `System.Text.Json`, xUnit

**Spec:** GitHub issue [#8](https://github.com/medokin/QuotaGlass/issues/8), `refactor(core): harden provider polling before adding integrations`

## Global Constraints

- Keep the existing `HealthState` values and user-visible quota model.
- Do not add a provider SDK, dynamic plugins, provider-specific cadence, exponential backoff, or a scheduler subsystem.
- Bound JSON response bodies to 1 MiB after decompression.
- Accept only `application/json` and `application/*+json` response media types.
- For HTTP 429 and 503, use a five-minute fallback and clamp server cooldowns to one hour.
- Never log raw bodies, headers, credentials, tokens, account identifiers, or exception messages.
- Keep all provider-specific status meaning, requests, credentials, and JSON mapping inside each provider.
- Use test-first red-green-refactor cycles for every behavior change.

---

### Task 1: Safe HTTP helpers

**Files:**
- Create: `src/QuotaGlass/Providers/ProviderHttpSafety.cs`
- Create: `tests/QuotaGlass.Tests/Providers/ProviderHttpSafetyTests.cs`

**Interfaces:**
- Produces: `ProviderHttpSafety.ReadJsonAsync(HttpResponseMessage, CancellationToken)` and `ProviderHttpSafety.GetRetryAfter(HttpResponseMessage, DateTimeOffset)`.

- [ ] **Step 1: Add failing tests for JSON media types and size enforcement**

Add table-driven xUnit tests proving normal JSON and `application/problem+json` parse, while HTML, a missing content type, malformed JSON, and a body larger than 1,048,576 bytes throw `InvalidDataException` or `JsonException` without including response content in exception text. Include a gzip-compressed response whose compressed bytes are below 1 MiB but whose decompressed content exceeds 1 MiB, and assert the limit applies to the decompressed stream bytes.

- [ ] **Step 2: Run the HTTP helper tests and verify the missing helper failure**

Run: `dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj -c Release --filter FullyQualifiedName~ProviderHttpSafetyTests`

Expected: build failure because `ProviderHttpSafety` does not exist.

- [ ] **Step 3: Implement the bounded JSON reader**

Implement a private bounded stream wrapper or pooled-copy routine that stops after exactly 1 MiB of decompressed content. Validate `Content-Type` before reading, then parse with `JsonDocument.ParseAsync`. Use sanitized exception messages containing no payload text.

- [ ] **Step 4: Run the JSON helper tests and verify they pass**

Run: `dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj -c Release --filter FullyQualifiedName~ProviderHttpSafetyTests`

Expected: all JSON helper cases pass.

- [ ] **Step 5: Add failing table-driven tests for `Retry-After`**

Cover 429 and 503 with delta and HTTP-date values, plus missing, invalid, negative, past, and values above one hour. Assert five minutes for missing/invalid/negative values, one hour for excessive values, and `null` for statuses other than 429/503.

- [ ] **Step 6: Implement safe `Retry-After` parsing**

Use `response.Headers.RetryAfter` where parseable, calculate HTTP dates relative to the supplied `now`, reject non-positive values to the fallback, and clamp to `TimeSpan.FromHours(1)`.

- [ ] **Step 7: Run the complete suite to confirm the standalone helper is buildable**

Run: `dotnet test QuotaGlass.slnx -c Release`

Expected: all existing tests plus the new helper tests pass.

- [ ] **Step 8: Commit the HTTP helpers**

```powershell
git add src/QuotaGlass/Providers/ProviderHttpSafety.cs tests/QuotaGlass.Tests/Providers/ProviderHttpSafetyTests.cs
git commit -m "feat(providers): add safe HTTP response helpers" -m "Created with Codex"
```

### Task 2: Explicit outcomes across providers and poller

**Files:**
- Create: `src/QuotaGlass/Providers/ProviderFetchResult.cs`
- Modify: `src/QuotaGlass/Providers/IStatusProvider.cs`
- Modify: `src/QuotaGlass/Providers/ClaudeProvider.cs`
- Modify: `src/QuotaGlass/Providers/CodexProvider.cs`
- Modify: `src/QuotaGlass/Providers/OllamaProvider.cs`
- Modify: `src/QuotaGlass/Core/StatusPoller.cs`
- Modify: `tests/QuotaGlass.Tests/Support/FakeStatusProvider.cs`
- Modify: `tests/QuotaGlass.Tests/Providers/ClaudeProviderTests.cs`
- Modify: `tests/QuotaGlass.Tests/Providers/CodexProviderTests.cs`
- Modify: `tests/QuotaGlass.Tests/Providers/OllamaProviderTests.cs`
- Modify: `tests/QuotaGlass.Tests/Core/StatusPollerTests.cs`
- Modify: `tests/QuotaGlass.Tests/Providers/ProviderPollerIntegrationTests.cs`

**Interfaces:**
- Consumes: `ProviderHttpSafety.ReadJsonAsync` and `ProviderHttpSafety.GetRetryAfter` from Task 1.
- Produces: `ProviderFetchOutcome` with `Success`, `PartialSuccess`, `NotConfigured`, `AuthenticationRequired`, `TransientFailure`, `RateLimited`, and `InvalidResponse`.
- Produces: `ProviderFetchResult(ProviderFetchOutcome Outcome, ProviderSnapshot? Snapshot = null, HttpStatusCode? StatusCode = null, TimeSpan? RetryAfter = null)` plus focused factories that enforce valid snapshot/outcome combinations.
- Changes: `IStatusProvider.FetchAsync(CancellationToken)` returns `Task<ProviderFetchResult>`.
- Produces: explicit provider-specific outcomes and one poller transition function that publishes or retains snapshots and updates failure counts.

- [ ] **Step 1: Add failing fetch-result, provider, and poller tests before changing the interface**

Add the wished-for `ProviderFetchResult` API to tests first. Claude cases cover missing credentials, expired/rejected credentials, success, partial success after optional profile failure, 429/503, and required usage HTML, missing content type, malformed JSON, and oversized JSON as `InvalidResponse` without a snapshot. Codex cases cover the same required-response and rate-limit boundaries. Ollama cases cover success, connection failure as `TransientFailure`, invalid JSON, and rate limiting. Poller cases use literal results for every transition, wrong provider ID, caller cancellation, last-good retention, third-failure degradation, and recovery.

- [ ] **Step 2: Run the focused tests and verify the missing result API failure**

Run: `dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj -c Release --filter "FullyQualifiedName~StatusPollerTests|FullyQualifiedName~ProviderPollerIntegrationTests|FullyQualifiedName~ClaudeProviderTests|FullyQualifiedName~CodexProviderTests|FullyQualifiedName~OllamaProviderTests"`

Expected: build failure because `ProviderFetchResult` and `ProviderFetchOutcome` do not exist.

- [ ] **Step 3: Restore a buildable repository with one atomic interface migration**

Create the result type, change `IStatusProvider`, migrate `FakeStatusProvider`, all three real providers, and `StatusPoller` in the same working-tree change. Use the simplest mappings needed to compile. Require snapshots for `Success`, `PartialSuccess`, `NotConfigured`, and `AuthenticationRequired`; prohibit snapshots for `TransientFailure`, `RateLimited`, and `InvalidResponse`. Preserve existing provider request/mapping logic and make the poller consume results through one transition method.

- [ ] **Step 4: Run the focused tests and verify remaining behavioral failures**

Run the Step 2 command again.

Expected: the project compiles; tests still fail where provider classification or poller transitions are incomplete.

- [ ] **Step 5: Complete Claude result mapping**

Keep credential parsing and request construction unchanged. Use the bounded reader for usage and profile JSON. Catch the helper's expected content and parse validation exceptions around required usage parsing and return `InvalidResponse` without a snapshot. Map optional profile failure to `PartialSuccess` without converting fresh usage into a transport failure. Return rate-limit results only for 429 and 503.

- [ ] **Step 6: Run Claude tests to green**

Run: `dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj -c Release --filter FullyQualifiedName~ClaudeProviderTests`

Expected: all Claude outcome and mapping tests pass.

- [ ] **Step 7: Complete Codex result mapping**

Keep credential scanning and quota mapping provider-specific. Use bounded JSON reads, convert expected response validation failures into `InvalidResponse` without a fresh snapshot, map 429/503 to `RateLimited`, and leave unexpected exceptions for the poller to catch.

- [ ] **Step 8: Run Codex tests to green**

Run: `dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj -c Release --filter FullyQualifiedName~CodexProviderTests`

Expected: all Codex outcome and mapping tests pass.

- [ ] **Step 9: Complete Ollama result mapping**

Use bounded JSON reads and remove the synthetic unreachable snapshot for `HttpRequestException`. Return `TransientFailure` for connection failures, `InvalidResponse` for expected response validation errors, and `RateLimited` for 429/503.

- [ ] **Step 10: Run Ollama tests to green**

Run: `dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj -c Release --filter FullyQualifiedName~OllamaProviderTests`

Expected: all Ollama outcome and mapping tests pass.

- [ ] **Step 11: Complete the poller transition table**

Keep the current provider timeout and exception catch. Treat timeout and unexpected exceptions as transient failures. Validate every fresh snapshot ID against `provider.Id`; otherwise treat it as invalid. Publish fresh data for success and partial success, the result's quiet empty snapshot for not configured, and the result's provider-specific auth-expired snapshot for authentication required, all with failure count zero. Retain last-good values for transient, rate-limited, and invalid results.

- [ ] **Step 12: Run provider, poller, and integration tests to green**

Run: `dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj -c Release --filter "FullyQualifiedName~StatusPollerTests|FullyQualifiedName~ProviderPollerIntegrationTests|FullyQualifiedName~ClaudeProviderTests|FullyQualifiedName~CodexProviderTests|FullyQualifiedName~OllamaProviderTests"`

Expected: all explicit outcome, transition, retention, recovery, Ollama failure-count, and Codex invalid-response retention cases pass.

- [ ] **Step 13: Commit the atomic fetch-boundary migration**

```powershell
git add src/QuotaGlass/Providers src/QuotaGlass/Core/StatusPoller.cs tests/QuotaGlass.Tests/Support/FakeStatusProvider.cs tests/QuotaGlass.Tests/Providers tests/QuotaGlass.Tests/Core/StatusPollerTests.cs
git commit -m "refactor(core): centralize provider fetch outcomes" -m "Created with Codex"
```

### Task 3: Provider cooldowns, coalesced refresh, and diagnostics

**Files:**
- Modify: `src/QuotaGlass/Core/StatusPoller.cs`
- Modify: `src/QuotaGlass/Core/RollingFileLog.cs`
- Modify: `tests/QuotaGlass.Tests/Core/StatusPollerTests.cs`
- Modify: `tests/QuotaGlass.Tests/Core/RollingFileLogTests.cs`
- Modify: `tests/QuotaGlass.Tests/Providers/ProviderPollerIntegrationTests.cs`

**Interfaces:**
- Consumes: explicit provider results and poller transition function from Task 2.
- Produces: per-provider cooldown deadlines enforced within the serialized poll operation.
- Produces: sanitized provider logging fields for provider ID, fetch outcome, HTTP status, cooldown seconds, and consecutive failure count.

- [ ] **Step 1: Add failing cooldown tests**

With the existing controllable `TimeProvider`, prove an active provider cooldown skips only that provider, manual refresh cannot bypass it, and the provider becomes eligible at the deadline. Assert other providers continue to run. Cover cooldown clearing for `Success`, `PartialSuccess`, `NotConfigured`, and `AuthenticationRequired` so each resumes normal cadence.

- [ ] **Step 2: Implement per-provider cooldown deadlines**

Store cooldown deadlines keyed by provider ID under a small lock or within the serialized poll operation. Do not create background timers. During each poll, synthesize retained state for ineligible providers and fetch all eligible providers normally. Replace the deadline only for `RateLimited`; clear any stored deadline for every other completed outcome.

- [ ] **Step 3: Add failing refresh-coalescing test**

Block the first provider call, issue many `RequestRefresh()` calls, release the provider, and assert the run loop performs at most one additional poll.

- [ ] **Step 4: Replace the unbounded channel with one-slot coalescing**

Use a bounded channel of capacity one with drop-write/full coalescing behavior. `RequestRefresh()` must be idempotent while a signal is already pending and must not throw because the slot is full.

- [ ] **Step 5: Extend sanitized logging and tests**

Add optional structured scalar fields to `RollingFileLog.Write` or a focused provider-log method. Validate allowed provider IDs/outcome tokens and render only sanitized numeric/token data. Test that exception messages, bodies, headers, tokens, and account IDs never appear.

- [ ] **Step 6: Run poller and integration tests**

Run: `dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj -c Release --filter "FullyQualifiedName~StatusPollerTests|FullyQualifiedName~RollingFileLogTests|FullyQualifiedName~ProviderPollerIntegrationTests"`

Expected: all transition, cooldown, retention, recovery, Ollama failure-count, Codex invalid-response retention, and coalescing tests pass.

- [ ] **Step 7: Commit poller hardening**

```powershell
git add src/QuotaGlass/Core tests/QuotaGlass.Tests/Core tests/QuotaGlass.Tests/Providers/ProviderPollerIntegrationTests.cs
git commit -m "refactor(core): centralize provider polling transitions" -m "Created with Codex"
```

### Task 4: Additive settings normalization and reload diagnostics

**Files:**
- Modify: `src/QuotaGlass/Core/SettingsStore.cs`
- Modify: `src/QuotaGlass/App.xaml.cs`
- Modify: `src/QuotaGlass/Core/ApplicationComposition.cs`
- Modify: `tests/QuotaGlass.Tests/Core/SettingsStoreTests.cs`
- Modify: `tests/QuotaGlass.Tests/Core/ApplicationCompositionTests.cs`

**Interfaces:**
- Consumes: `AppSettings.Default.Providers` as the compiled provider defaults.
- Produces: normalized settings that preserve every persisted provider entry and add only missing compiled defaults.
- Produces: one sanitized invalid-settings log event per failed watched reload.

- [ ] **Step 1: Add failing load and save compatibility tests**

Write an older settings file that omits one compiled provider and includes an unknown provider entry. Assert load adds the missing default, preserves all known values, preserves the unknown entry, and a subsequent save keeps it.

- [ ] **Step 2: Run settings tests and verify red failure**

Run: `dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj -c Release --filter FullyQualifiedName~SettingsStoreTests`

Expected: failure because validation currently rejects missing required provider entries.

- [ ] **Step 3: Implement additive provider normalization**

After deserialization, validate scalar settings and every present provider value, then merge only missing keys from `AppSettings.Default.Providers`. Remove `RequiredProviderIds`; completeness comes from normalization, not rejection. Apply normalization consistently in load, update, watcher reload, and save validation without deleting unknown keys.

- [ ] **Step 4: Add failing watcher reload test**

Load a valid file, subscribe to `Changed`, write malformed JSON, wait past the debounce, and assert active settings remain unchanged, no change event fires, and exactly one sanitized invalid-settings event is logged for that reload attempt.

- [ ] **Step 5: Add logging dependency and implement invalid reload logging**

Allow `SettingsStore` to receive the application `RollingFileLog`, with a compatible internal/test constructor if needed. In `App.xaml.cs`, construct the log before the settings store and pass the existing log instance into the store. Log `LogArea.Settings` plus `LogOutcome.Invalid` once after a failed debounced reload. Do not log the path, file contents, or exception message.

- [ ] **Step 6: Wire the application composition and run settings tests**

Update the composition root to pass the existing log instance. Run the Step 2 command and require all settings tests to pass.

- [ ] **Step 7: Run composition tests**

Run: `dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj -c Release --filter FullyQualifiedName~ApplicationCompositionTests`

Expected: all composition tests pass with the updated constructor wiring.

- [ ] **Step 8: Commit settings compatibility**

```powershell
git add src/QuotaGlass/Core src/QuotaGlass/App.xaml.cs tests/QuotaGlass.Tests/Core
git commit -m "fix(settings): normalize missing provider defaults" -m "Created with Codex"
```

### Task 5: Full regression verification and documentation check

**Files:**
- Modify only files required by failures found during verification.

**Interfaces:**
- Produces: a Release-clean branch satisfying every issue #8 acceptance criterion.

- [ ] **Step 1: Run targeted acceptance tests**

Run: `dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj -c Release --filter "FullyQualifiedName~StatusPollerTests|FullyQualifiedName~SettingsStoreTests|FullyQualifiedName~ProviderHttpSafetyTests|FullyQualifiedName~ClaudeProviderTests|FullyQualifiedName~CodexProviderTests|FullyQualifiedName~OllamaProviderTests|FullyQualifiedName~ProviderPollerIntegrationTests"`

Expected: all targeted tests pass with zero warnings or failures.

- [ ] **Step 2: Run the complete Release sequence**

```powershell
dotnet restore QuotaGlass.slnx
dotnet build QuotaGlass.slnx -c Release --no-restore
dotnet test QuotaGlass.slnx -c Release --no-build
dotnet publish src/QuotaGlass/QuotaGlass.csproj -c Release -p:PublishProfile=win-x64 --no-build
```

Expected: restore succeeds, build has zero warnings/errors, all tests pass, and publish produces one framework-dependent Windows x64 executable.

- [ ] **Step 3: Audit the issue acceptance criteria and diff**

Check each required outcome, cancellation, cooldown, content-type, size, retention, recovery, settings, refresh, and logging case against a named test. Run `git diff origin/master...HEAD --check` and inspect `git status --short` for accidental artifacts or sensitive data.

- [ ] **Step 4: Commit verification-only corrections if required**

```powershell
$correctedFiles = git diff --name-only
git add -- $correctedFiles
git commit -m "test(core): complete provider hardening coverage" -m "Created with Codex"
```

Skip this commit when no corrections are needed.
