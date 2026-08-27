# Provider Availability Discovery Design

## Goal

Replace persisted provider enable flags with runtime discovery of each built-in provider's local prerequisites. Unavailable providers are not polled and do not appear in any UI report.

## Scope

- Remove `ProviderSettings.Enabled` while preserving `OpenCodeConsoleSettings` and unrelated application settings when old settings files are loaded and saved.
- Re-evaluate availability before every provider poll.
- Cover Claude, Codex, OpenCode Go, OpenCode Company Seat, and Ollama.
- Keep authentication failures and remote endpoint failures separate from local unavailability.
- Keep discovery output and logs free of credentials, account identifiers, workspace identifiers, and raw credential contents.

## Architecture

Built-in providers implement a small `IProviderAvailability` contract next to `IStatusProvider`. The contract checks only local prerequisites and never calls a remote quota endpoint. `StatusPoller` checks availability concurrently before cooldown handling or quota fetching. Providers that report unavailable are omitted from the next `StatusReport`, and their cooldown and retention state is cleared.

Providers used by tests or future extensions that implement only `IStatusProvider` remain available by default. This preserves the existing extension boundary while requiring every built-in provider registration to implement the new contract.

## Provider Checks

| Provider | Available when | Unavailable when |
| --- | --- | --- |
| Claude | The configured Claude credential file exists | The credential file is absent |
| Codex | The configured Codex authentication file exists | The authentication file is absent |
| OpenCode Go | The OpenCode API credential file exists, or OpenCode Console fallback is enabled and the local `opencode` command is discoverable | Neither local credential path is usable |
| OpenCode Company Seat | The local `opencode` command is discoverable | The command is absent |
| Ollama | The local Ollama version endpoint accepts a connection and returns an HTTP response | The local service cannot be reached |

Credential contents are not validated by availability discovery. Invalid, expired, or permission-denied credentials remain visible and flow through the existing provider fetch outcomes. An Ollama HTTP error or invalid payload also remains a provider fetch result because the local service was discovered.

## Settings Migration

`ProviderSettings` retains only OpenCode Console configuration. The JSON serializer ignores legacy `Enabled` properties when reading existing files. Default settings contain entries for every built-in provider, including OpenCode Company Seat. Normalization preserves OpenCode Console settings and unknown provider entries, and all subsequent writes omit `Enabled`.

The OpenCode Console `Enabled` option remains independent. It controls whether OpenCode Go may use Console credentials as a fallback and is not a provider visibility flag.

## Polling and State Transitions

Availability is checked on every `PollOnceAsync` call before any remote quota request. A transition from available to unavailable removes the provider from the published report immediately and clears provider-specific cooldown and retention state. A later transition back to available performs a normal fresh fetch and republishes the provider.

Availability timeouts or exceptions are treated as local unavailability for that cycle. Logging is limited to the safe built-in provider ID, outcome, and exception type.

## UI Behavior

The popup, overlay, and tray tooltip already consume the providers in `StatusReport`. Filtering at the poller boundary makes all UI surfaces omit unavailable providers without separate view-level rules. Available providers continue to use the current health, last-good-data, cooldown, and authentication behavior.

## Testing

- Settings tests load legacy true and false values, preserve OpenCode Console configuration, and verify new writes omit `Enabled`.
- Provider tests cover available and unavailable local prerequisites for all five built-ins.
- Poller tests cover skipped fetches, runtime availability changes, unavailable-provider omission, and sanitized discovery failures.
- UI report delivery tests verify the popup and overlay receive only the providers published by the poller.
- Local validation runs against the installed and running OpenCode instance without printing credentials or identifiers.
- Final validation uses the repository's Release restore, build, test, and publish sequence.
