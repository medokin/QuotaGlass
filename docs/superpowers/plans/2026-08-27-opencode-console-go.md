# OpenCode Console Go Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Populate OpenCode Go quota windows from an opt-in OpenCode Console account when no local `opencode-go` API key exists.

**Architecture:** Preserve the current API-key request as the first authentication path. When it is absent and Console discovery is enabled, run the read-only `opencode db` command to select only account IDs, access tokens, expiries, and active organization state, discover organizations through the Console account API, and call the exact private `GET /console/api/go/status` meter route. Keep all credentials and raw identifiers in memory, persist only an opaque SHA-256 selector, and treat the private endpoint as a capability that may disappear.

**Tech Stack:** .NET 10, C# 14, WPF, `System.Diagnostics.Process`, `System.Net.Http`, `System.Text.Json`, xUnit

**Spec:** [GitHub issue #16](https://github.com/medokin/QuotaGlass/issues/16)

## Global Constraints

- Keep the existing `opencode-go` API-key integration preferred and unchanged.
- Console discovery is opt-in.
- Never select the OpenCode refresh-token or email columns.
- Never persist or log access tokens, account IDs, organization IDs, emails, organization names, or raw response bodies.
- Never refresh, write, export, or delete OpenCode credentials.
- Persist only a deterministic opaque workspace selector.
- Missing OpenCode, missing accounts, inaccessible storage, and non-Go organizations remain quiet.
- Rejected or expired access tokens produce a sanitized authentication state.
- HTTP and process output reads are bounded and cancellation-aware.
- Only `https://opencode.ai/console` and its fixed API paths may receive the Console access token.
- Preserve the Release build, full xUnit suite, framework-dependent Windows x64 single-file publish, and Gitleaks validation.

---

### Task 1: Opt-in settings model

**Files:**
- Modify: `src/QuotaGlass/Core/AppSettings.cs`
- Modify: `src/QuotaGlass/Core/SettingsStore.cs`
- Test: `tests/QuotaGlass.Tests/Core/SettingsStoreTests.cs`

**Interfaces:**
- Produces: `OpenCodeConsoleSettings(bool Enabled, string? WorkspaceSelector)`
- Produces: `ProviderSettings.OpenCodeConsole`
- Consumes: existing provider settings serialization and normalization

- [ ] **Step 1: Write failing settings tests**

Add tests that enable Console discovery, round-trip a 64-character lowercase hexadecimal selector, reject malformed selectors, and verify default settings keep Console discovery disabled.

```csharp
ProviderSettings configured = new(true)
{
    OpenCodeConsole = new(true, new string('a', 64)),
};
```

- [ ] **Step 2: Run the focused tests and verify failure**

Run:

```powershell
dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~SettingsStoreTests
```

Expected: compile failure because `OpenCodeConsoleSettings` and `ProviderSettings.OpenCodeConsole` do not exist.

- [ ] **Step 3: Implement the settings types and validation**

Add:

```csharp
public sealed record OpenCodeConsoleSettings(bool Enabled, string? WorkspaceSelector);

public sealed record ProviderSettings(bool Enabled)
{
    public OpenCodeConsoleSettings? OpenCodeConsole { get; init; }
}
```

Normalize a non-null selector to lowercase and accept it only when it contains exactly 64 ASCII hexadecimal characters. Leave `OpenCodeConsole` null by default so existing files remain opt-in.

- [ ] **Step 4: Run focused tests and verify pass**

Run the same filtered command. Expected: all `SettingsStoreTests` pass.

- [ ] **Step 5: Commit the settings change**

```powershell
git add src/QuotaGlass/Core/AppSettings.cs src/QuotaGlass/Core/SettingsStore.cs tests/QuotaGlass.Tests/Core/SettingsStoreTests.cs docs/superpowers/plans/2026-08-27-opencode-console-go.md
git commit -m "feat(core): add opencode console discovery settings" -m "Created with Codex"
```

### Task 2: Read-only OpenCode account discovery

**Files:**
- Create: `src/QuotaGlass/Providers/OpenCodeConsoleAccountReader.cs`
- Create: `tests/QuotaGlass.Tests/Providers/OpenCodeConsoleAccountReaderTests.cs`

**Interfaces:**
- Produces: `OpenCodeConsoleAccount(string AccountId, string AccessToken, DateTimeOffset? ExpiresAt)`
- Produces: `IOpenCodeConsoleAccountReader.ReadAsync(CancellationToken)`
- Consumes: the installed `opencode db --format json` command

- [ ] **Step 1: Write failing reader tests**

Cover these behaviors:

```csharp
Assert.DoesNotContain("email", capturedQuery, StringComparison.OrdinalIgnoreCase);
Assert.DoesNotContain("refresh_token", capturedQuery, StringComparison.OrdinalIgnoreCase);
Assert.Equal("account_test", account.AccountId);
Assert.Equal("access-test", account.AccessToken);
```

Also cover missing command, non-zero exit, malformed JSON, excessive output, expired timestamp parsing, cancellation, maximum account count, and rejection of non-HTTPS or non-Console account URLs.

- [ ] **Step 2: Run the focused tests and verify failure**

Run:

```powershell
dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~OpenCodeConsoleAccountReaderTests
```

Expected: compile failure because the reader does not exist.

- [ ] **Step 3: Implement the bounded process reader**

Use `ProcessStartInfo` with `UseShellExecute = false`, redirected output, `CreateNoWindow = true`, and argument-list entries for `db`, `--format`, `json`, and one constant SQL query. The query selects only:

```sql
select a.id, a.url, a.access_token, a.token_expiry
from account a
order by a.id
limit 33;
```

Read stdout and stderr concurrently with a 1 MiB limit, kill the child process tree on cancellation or overflow, never include child output in an exception, and return an empty result for a missing executable or non-zero exit. Accept only `https://opencode.ai/console`, non-empty bounded tokens, and at most 32 accounts.

- [ ] **Step 4: Run focused tests and verify pass**

Run the same filtered command. Expected: all account-reader tests pass.

- [ ] **Step 5: Commit account discovery**

```powershell
git add src/QuotaGlass/Providers/OpenCodeConsoleAccountReader.cs tests/QuotaGlass.Tests/Providers/OpenCodeConsoleAccountReaderTests.cs
git commit -m "feat(providers): discover opencode console accounts" -m "Created with Codex"
```

### Task 3: Exact Console Go quota client

**Files:**
- Create: `src/QuotaGlass/Providers/OpenCodeConsoleGoClient.cs`
- Create: `tests/QuotaGlass.Tests/Providers/OpenCodeConsoleGoClientTests.cs`
- Create: `tests/QuotaGlass.Tests/Fixtures/opencode-console-go-status.json`
- Modify: `tests/QuotaGlass.Tests/Fixtures/FixtureSecurityTests.cs`

**Interfaces:**
- Produces: `OpenCodeConsoleGoClient.FindEligibleAsync(...)`
- Produces: `OpenCodeConsoleGoCandidate(string WorkspaceSelector, ImmutableArray<UsageWindow> Windows)`
- Consumes: Console access tokens from Task 2 and severity policy delegate

- [ ] **Step 1: Write failing client tests**

Cover fixed-host requests, bearer and `x-org-id` headers, `200 null` Seat exclusion, `access: null`, exact five-hour/week/month mapping, decimal-string microcent parsing, over-100 percent, nullable five-hour reset, monthly reset from `access.endsAt`, 401/403 classification, 404 capability absence, 429/503 retry behavior, bounded JSON, malformed schema, cancellation, single eligible organization, multiple eligible organizations, and deterministic opaque selectors.

Use a sanitized successful fixture without subscriber, account, organization, email, token, or payment-attempt fields:

```json
{
  "useBalance": false,
  "access": {
    "startsAt": "2026-08-01T00:00:00Z",
    "endsAt": "2026-09-01T00:00:00Z",
    "cancelAtPeriodEnd": false,
    "meters": {
      "fiveHour": { "startsAt": "2026-08-27T10:00:00Z", "resetsAt": "2026-08-27T15:00:00Z", "limitMicroCents": "1200000000", "usedMicroCents": "600000000" },
      "week": { "startsAt": "2026-08-24T00:00:00Z", "resetsAt": "2026-08-31T00:00:00Z", "limitMicroCents": "3000000000", "usedMicroCents": "1500000000" },
      "month": { "limitMicroCents": "6000000000", "usedMicroCents": "3000000000" }
    }
  }
}
```

- [ ] **Step 2: Run the focused tests and verify failure**

Run:

```powershell
dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~OpenCodeConsoleGoClientTests|FullyQualifiedName~FixtureSecurityTests"
```

Expected: compile failure because the client does not exist.

- [ ] **Step 3: Implement organization discovery and exact meter parsing**

For every usable account, call fixed `GET https://opencode.ai/console/api/orgs`, parse only organization IDs, then call fixed `GET https://opencode.ai/console/api/go/status` with the same bearer and an `x-org-id` header. Never call the subscription write routes. Compute selectors as lowercase SHA-256 over the account ID, a NUL separator, and organization ID. Compute percentages from checked `decimal` microcent values before converting the ratio to `double`.

Map meters as:

```text
fiveHour -> rolling, resetsAt from fiveHour.resetsAt
week     -> weekly,  resetsAt from week.resetsAt
month    -> monthly, resetsAt from access.endsAt
```

Return no candidate for a nullable response or nullable access. Keep authentication rejection, route absence, transient failure, and invalid schema distinct so the provider can preserve prior results.

- [ ] **Step 4: Run focused tests and verify pass**

Run the same filtered command. Expected: all client and fixture-security tests pass.

- [ ] **Step 5: Commit the Console client**

```powershell
git add src/QuotaGlass/Providers/OpenCodeConsoleGoClient.cs tests/QuotaGlass.Tests/Providers/OpenCodeConsoleGoClientTests.cs tests/QuotaGlass.Tests/Fixtures/opencode-console-go-status.json tests/QuotaGlass.Tests/Fixtures/FixtureSecurityTests.cs
git commit -m "feat(providers): fetch opencode console go quotas" -m "Created with Codex"
```

### Task 4: Provider precedence and selection

**Files:**
- Modify: `src/QuotaGlass/Providers/OpenCodeGoProvider.cs`
- Modify: `src/QuotaGlass/Providers/ProviderRegistry.cs`
- Modify: `tests/QuotaGlass.Tests/Providers/OpenCodeGoProviderTests.cs`
- Modify: `tests/QuotaGlass.Tests/Providers/ProviderRegistryTests.cs`
- Modify: `tests/QuotaGlass.Tests/Providers/ProviderPollerIntegrationTests.cs`

**Interfaces:**
- Consumes: `ProviderSettings.OpenCodeConsole` from Task 1
- Consumes: account reader and Console client from Tasks 2 and 3
- Produces: API-key-first `OpenCodeGoProvider.FetchAsync`

- [ ] **Step 1: Write failing provider tests**

Cover:

```text
API key present -> existing /zen/go/v1/usage request only
API key absent and Console disabled -> quiet NotConfigured
API key absent and Console enabled -> account reader and Console client run
one eligible organization -> automatic success
multiple eligible organizations without selector -> quiet selection-required snapshot
multiple eligible organizations with matching selector -> selected success
expired-only accounts -> AuthenticationRequired
401/403 -> AuthenticationRequired with sanitized text
404 or contract drift -> transient/invalid result that retains previous data
```

Assert that snapshots, errors, settings, and captured requests never contain raw test tokens or IDs.

- [ ] **Step 2: Run focused tests and verify failure**

Run:

```powershell
dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~OpenCodeGoProviderTests|FullyQualifiedName~ProviderRegistryTests|FullyQualifiedName~ProviderPollerIntegrationTests"
```

Expected: provider fallback tests fail because Console discovery is not wired.

- [ ] **Step 3: Implement precedence and deterministic selection**

Split the existing fetch method into an API-key path and a Console path. Read the current provider setting through a delegate so settings reloads do not recreate providers. If exactly one candidate exists, use it. If multiple exist, require a matching 64-character selector. Publish only the existing provider ID and label, three sanitized usage windows, and fixed error text.

- [ ] **Step 4: Run focused tests and verify pass**

Run the same filtered command. Expected: all focused provider and integration tests pass.

- [ ] **Step 5: Commit provider integration**

```powershell
git add src/QuotaGlass/Providers/OpenCodeGoProvider.cs src/QuotaGlass/Providers/ProviderRegistry.cs tests/QuotaGlass.Tests/Providers/OpenCodeGoProviderTests.cs tests/QuotaGlass.Tests/Providers/ProviderRegistryTests.cs tests/QuotaGlass.Tests/Providers/ProviderPollerIntegrationTests.cs
git commit -m "feat(providers): support opencode console go sessions" -m "Created with Codex"
```

### Task 5: Live, security, and release verification

**Files:**
- Modify only if verification exposes a defect

**Interfaces:**
- Consumes: completed implementation from Tasks 1 through 4
- Produces: release-ready branch and PR evidence

- [ ] **Step 1: Run the complete Release suite**

```powershell
dotnet restore QuotaGlass.slnx
dotnet build QuotaGlass.slnx -c Release --no-restore
dotnet test QuotaGlass.slnx -c Release --no-build
```

Expected: zero warnings, zero errors, all tests pass.

- [ ] **Step 2: Validate the Windows x64 publish**

```powershell
dotnet publish src/QuotaGlass/QuotaGlass.csproj -c Release -p:PublishProfile=win-x64
```

Expected: one framework-dependent `QuotaGlass.exe` application artifact and no unexpected SQLite native dependency.

- [ ] **Step 3: Run live Console capability validation**

Against the local OpenCode 1.18.21 account, verify that the production account reader succeeds without selecting email or refresh-token data, organization discovery succeeds, and `GET /console/api/go/status` returns `200 null` for the company Seat organization. Do not print or persist credential or identifier values.

- [ ] **Step 4: Run secret and diff checks**

```powershell
gitleaks git --no-banner
git diff --check origin/master...HEAD
git status --short
```

Expected: no leaks, no whitespace errors, and only intended changes.

- [ ] **Step 5: Request independent specification and code-quality reviews**

Dispatch one reviewer for issue acceptance/security and another for implementation quality. Resolve every valid finding and repeat the focused and full verification commands.

- [ ] **Step 6: Open the pull request**

Use a conventional title and include outcome, issue link, exact verification commands, the private-contract stability caveat, and `Created with Codex` in the PR body.
