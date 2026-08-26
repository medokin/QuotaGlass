# QuotaGlass Release Acceptance Checklist

Use this checklist for every Windows x64 release candidate. Record `PASS`, `FAIL`, or `NOT EXERCISED` in every Status cell. Evidence must name the exact command, observation, or screenshot path. `NOT EXERCISED` requires a concrete environmental reason and should cite automated coverage when available. Never paste credentials, tokens, headers, response bodies, account identifiers, user identifiers, email addresses, or live usage data into this document or screenshots committed to the repository.

## Execution record

| Field | Value |
|---|---|
| Date | 2026-08-26 |
| Tester | Codex unattended verification |
| Build | Task 11 Release candidate from base `19c324a` |
| Windows session | Local interactive session; not RDP; no battery device detected |
| Display topology | Three 2560x1440 monitors at 100% DPI; bottom-edge taskbar on each working area |

## Acceptance results

| ID | Check | Status | Evidence | Notes |
|---|---|---|---|---|
| LIVE-01 | Initial Claude card shows the live plan label and every returned usage window. | NOT EXERCISED | Published process launched but exposed no targetable top-level window; supported follow-up UI automation was stopped when an unrelated Windows Security dialog blocked the desktop. | Both credential files existed. No credential contents or live account/usage values were read or recorded. |
| LIVE-02 | Initial Codex card shows the live plan label and every returned usage window. | NOT EXERCISED | Published process launched but exposed no targetable top-level window; supported follow-up UI automation was stopped when an unrelated Windows Security dialog blocked the desktop. | Both credential files existed. No credential contents or live account/usage values were read or recorded. |
| LIVE-03 | Initial Ollama card shows the daemon version and loaded-model count. | NOT EXERCISED | The card was not accessible. A read-only localhost probe observed version 0.32.15 and model count 0. | Endpoint evidence is partial and is not proof of card rendering. The daemon was not stopped or started. |
| TRAY-01 | Tray is green when every reachable provider is below the warning threshold. | NOT EXERCISED | No accessible tray surface or credential-free live-state injection. `TrayStatusPolicyTests.GetState_ReturnsGreenWhenHealthyProviderIsBelowWarningAlongsideUnreachableProvider` passed in the 231-test Release run. | Automated policy coverage is not proof of Windows shell rendering. |
| TRAY-02 | Tray is amber at the warning threshold. | NOT EXERCISED | No accessible tray surface. `TrayStatusPolicyTests.GetState_UsesInclusiveConfiguredThresholds` passed for 80 in the Release run. | Automated policy coverage is not proof of Windows shell rendering. |
| TRAY-03 | Tray is red at the critical threshold. | NOT EXERCISED | No accessible tray surface. `TrayStatusPolicyTests.GetState_UsesInclusiveConfiguredThresholds` passed for 95 in the Release run. | Automated policy coverage is not proof of Windows shell rendering. |
| TRAY-04 | Tray is grey when every provider is unreachable or disabled. | NOT EXERCISED | No accessible tray surface and stopping providers was prohibited. `TrayStatusPolicyTests.GetState_ReturnsGreyWhenEveryProviderIsUnavailable` passed in the Release run. | Automated policy coverage is not proof of Windows shell rendering. |
| ALERT-01 | A first-run value at 95 percent raises exactly one critical toast. | NOT EXERCISED | No credential-free real-app fixture runner and no accessible toast surface. `ThresholdWatcherTests.Evaluate_InitialCriticalPercentEmitsOnlyCritical` passed in the Release run. | A toast containing live usage data was not captured. |
| ALERT-02 | Refreshing an unchanged 95 percent cycle does not repeat the toast. | NOT EXERCISED | Real refresh UI was inaccessible. `ThresholdWatcherTests.Evaluate_FiresEachThresholdOncePerCycle` passed in the Release run. | Automated watcher coverage is not proof of toast delivery. |
| ALERT-03 | Moving the reset timestamp to a later fixture or clock-controlled cycle re-arms the alert. | NOT EXERCISED | No real-app fixture or clock injection surface. `ThresholdWatcherTests.Evaluate_NewResetTimestampRearmsThreshold` passed in the Release run. | No live vendor response was edited. |
| POPUP-01 | Popup remains fully visible beside a bottom-edge taskbar. | NOT EXERCISED | Three bottom-edge taskbars were detected, but the popup could not be opened before the desktop became blocked. `DisplayConverterTests.PopupPosition_StaysInsideWorkAreaForEveryTaskbarEdge` passed. | Automated geometry coverage is not proof of shell placement. |
| POPUP-02 | Popup remains fully visible beside a top-edge taskbar. | NOT EXERCISED | No top-edge taskbar was configured; all three detected taskbars were bottom-edge. `DisplayConverterTests.PopupPosition_StaysInsideWorkAreaForEveryTaskbarEdge` passed. | Changing taskbar configuration was outside the unattended check. |
| POPUP-03 | Popup remains fully visible beside a left-edge taskbar. | NOT EXERCISED | No left-edge taskbar was configured; all three detected taskbars were bottom-edge. `DisplayConverterTests.PopupPosition_StaysInsideWorkAreaForEveryTaskbarEdge` passed. | Changing taskbar configuration was outside the unattended check. |
| POPUP-04 | Popup remains fully visible beside a right-edge taskbar. | NOT EXERCISED | No right-edge taskbar was configured; all three detected taskbars were bottom-edge. `DisplayConverterTests.PopupPosition_StaysInsideWorkAreaForEveryTaskbarEdge` passed. | Changing taskbar configuration was outside the unattended check. |
| OVERLAY-01 | Overlay can be shown on each of two monitors with different DPI scaling. | NOT EXERCISED | Three monitors were detected, but all reported 96 DPI (100% scaling). | Mixed-DPI hardware/topology was unavailable. Automated DPI conversion tests passed. |
| OVERLAY-02 | Top-left corner placement stays inside the selected monitor working area. | NOT EXERCISED | Overlay surface was inaccessible after the blocked-desktop stop condition. `DisplayConverterTests.OverlayPosition_AppliesMarginAtEveryCorner` passed. | Default margin is 12 DIP. |
| OVERLAY-03 | Top-right corner placement stays inside the selected monitor working area. | NOT EXERCISED | Overlay surface was inaccessible after the blocked-desktop stop condition. `DisplayConverterTests.OverlayPosition_AppliesMarginAtEveryCorner` passed. | Default margin is 12 DIP. |
| OVERLAY-04 | Bottom-left corner placement stays inside the selected monitor working area. | NOT EXERCISED | Overlay surface was inaccessible after the blocked-desktop stop condition. `DisplayConverterTests.OverlayPosition_AppliesMarginAtEveryCorner` passed. | Default margin is 12 DIP. |
| OVERLAY-05 | Bottom-right corner placement stays inside the selected monitor working area. | NOT EXERCISED | Overlay surface was inaccessible after the blocked-desktop stop condition. `DisplayConverterTests.OverlayPosition_AppliesMarginAtEveryCorner` passed. | Default margin is 12 DIP. |
| OVERLAY-06 | A custom dragged position is saved and restored after restart. | NOT EXERCISED | Drag and restart could not be performed before the desktop became blocked. `DisplayConverterTests.OverlayPosition_ReclampsPersistedPhysicalCustomPositionAtTargetDpi` passed. | The pre-existing settings state was absent and was restored to absent. |
| OVERLAY-07 | While typing in another application, showing or updating the overlay does not move focus or interrupt input. | NOT EXERCISED | Focus probe could not continue after an unrelated Windows Security dialog blocked the desktop; safety guidance required stopping. | No focus result is claimed. |
| HOTKEY-01 | `Ctrl+Alt+A` toggles the overlay. | NOT EXERCISED | The supported key-input path could not continue after the desktop became blocked. `GlobalHotkeyTests.WindowMessage_ForRegisteredHotkeyRaisesPressed` passed. | No Windows-key shortcut was used. |
| HOTKEY-02 | If another process owns the hotkey, registration failure is graceful and the application remains usable. | NOT EXERCISED | No controlled competing hotkey owner was available before the desktop became blocked. `GlobalHotkeyTests.Constructor_RegistrationFailureIsLoggedWithoutThrowing` passed. | Automated coverage is not proof of real shell contention. |
| AUTOSTART-01 | Enabling Start with Windows adds only the `QuotaGlass` value with the quoted executable path. | PASS | Production `AutostartService` added an exact quoted `REG_SZ`; all other Run value names were unchanged. | The prior value was absent and was snapshotted before the check. |
| AUTOSTART-02 | An external change to the `QuotaGlass` Run value is reflected by the menu state. | NOT EXERCISED | Production `AutostartService.IsEnabled` immediately reflected an external value change and correction; the blocked desktop prevented opening the menu. | Backend live-read behavior passed; actual menu rendering remains unverified. |
| AUTOSTART-03 | Disabling Start with Windows removes only the `QuotaGlass` value. | PASS | Production `AutostartService` removed the named value; all other Run value names were unchanged; the prior absent state was restored. | No unrelated registry value was modified. |
| POLL-01 | Refresh now starts a new poll without waiting for the normal timer. | NOT EXERCISED | Tray command was inaccessible. `StatusPollerTests.RequestRefresh_WakesRunLoopBeforeTimerTick` and `RequestRefresh_PerformsOnePoll` passed. | No runtime timing evidence was available. |
| POLL-02 | Normal polling cadence is 60 seconds. | NOT EXERCISED | The real app run could not be observed for a complete cadence. `StatusPollerTests.SetReducedCadence_RecreatesTimerUsingCurrentSettings` passed with documented defaults. | Automated timer coverage is not a wall-clock shell observation. |
| POLL-03 | Session lock changes polling cadence to the five-minute backoff. | NOT EXERCISED | Locking the automation-controlled workstation was prohibited because it would sever control. `ActivityStateMonitorTests.IsReducedCadence_IsTrueWhileSessionIsLocked` passed. | No workstation lock was attempted. |
| POLL-04 | Unlock restores the normal 60-second cadence. | NOT EXERCISED | Unlock requires the prohibited workstation-lock cycle. `ActivityStateMonitorTests.LockAndUnlock_RaiseChangedOnlyOnStateTransitions` passed. | No workstation lock was attempted. |
| POLL-05 | Battery plus at least five minutes of idle time changes polling cadence to five minutes. | NOT EXERCISED | No battery device was detected. `ActivityStateMonitorTests.IsReducedCadence_RequiresOfflineBatteryAndAtLeastFiveMinutesIdle` passed. | Battery or power state was not altered. |
| POLL-06 | An RDP reconnect keeps the application responsive and polling. | NOT EXERCISED | The session was local, not RDP; disconnecting or reconnecting the control session was prohibited. | Residual shell-lifecycle risk. |
| AUTH-01 | An expired Claude token shows `re-auth: run claude login`, raises one toast, then remains silent until state changes. | NOT EXERCISED | The live credential was not edited or expired. Claude expiry and threshold deduplication tests passed in the Release run. | No authentication dialog or credential file was automated. |
| AUTH-02 | An expired Codex token shows `re-auth: run codex login`, raises one toast, then remains silent until state changes. | NOT EXERCISED | The live credential was not edited or expired. Codex unauthorized mapping and threshold deduplication tests passed in the Release run. | No authentication dialog or credential file was automated. |
| AUTH-03 | Claude and Codex credential files have identical before-and-after SHA-256 digests. | PASS | Both files existed; both before/after digest comparisons were equal. | Digest values and credential contents were not printed or recorded. |
| OLLAMA-01 | A stopped Ollama daemon appears silently unreachable with no toast. | NOT EXERCISED | The existing daemon was reachable and was not stopped. `OllamaProviderTests.FetchAsync_ConnectionRefusedIsQuietlyUnreachable` and `ThresholdWatcherTests.Evaluate_UnreachableOllamaNeverEmitsAlert` passed. | Existing daemon lifecycle was left unchanged. |
| OLLAMA-02 | Ollama recovers after its daemon starts. | NOT EXERCISED | The existing daemon was already running and was not stopped or started. | No safe daemon lifecycle transition was available. |
| LOG-01 | `log.txt` rotates once at 1,048,576 bytes and both retained files stay within the cap. | PASS | Focused Release run of `RollingFileLogTests`: 6 passed, 0 failed; includes `Write_WhenLogExceedsOneMiB_RotatesOnceWithBoundedFiles`. | Generated non-sensitive records were used by the test. |
| LOG-02 | Logs contain no request or response headers. | PASS | A same-user published smoke run created one runtime log; the header-category match count was 0. | The prior absent log state was restored after scanning. |
| LOG-03 | Logs contain no request or response bodies. | PASS | A same-user published smoke run created one runtime log; the body-category match count was 0. | The prior absent log state was restored after scanning. |
| LOG-04 | Logs contain no access tokens or refresh tokens. | PASS | A same-user published smoke run created one runtime log; bearer, JWT, access-token, and refresh-token counts were all 0. | No matched text was printed; the prior absent log state was restored. |
| LOG-05 | Logs contain no account or user identifiers. | PASS | A same-user published smoke run created one runtime log; account-ID and user-ID counts were both 0. | No matched text was printed; the prior absent log state was restored. |
| LOG-06 | Logs contain no email addresses. | PASS | A same-user published smoke run created one runtime log; the conservative email-marker count was 0. | No matched text was printed; the prior absent log state was restored. |
| SETTINGS-01 | `%APPDATA%\ai-status\settings.json` contains no access token, refresh token, account identifier, email address, or response body. | PASS | Production `SettingsStore` saved defaults; safe scan counts were 0 for access token, refresh token, account ID, user ID, email, and response body. | The prior absent settings state was restored after the scan. |

## Release verification

| Check | Status | Evidence | Notes |
|---|---|---|---|
| `dotnet test AiStatus.slnx -c Release` | PASS | 257 passed, 0 failed, 0 skipped. | Release configuration. |
| `dotnet publish src/AiStatus/AiStatus.csproj -p:PublishProfile=win-x64` | PASS | Publish exited 0. | Framework-dependent single-file profile. |
| Published artifact inventory | PASS | `QuotaGlass.exe`, 25,922,423 bytes; no other files. | Inventory taken from the profile publish directory. |
| Fixture security test | PASS | Focused Release run: 1 passed, 0 failed; all 6 fixture files scanned. | No matched fixture content was printed. |
| `dotnet build AiStatus.slnx -c Release` | PASS | Build succeeded with 0 warnings and 0 errors. | Release configuration. |
| `git diff --check` | PASS | Exit 0 with no output. | Run before commit. |

## Sign-off

- Release decision: Automated release verification passed; full manual Windows shell sign-off remains incomplete.
- Residual risks: real tray, popup, overlay, focus, hotkey contention, toast delivery, cadence, lock/unlock, mixed-DPI, alternate taskbar edges, battery, RDP reconnect, expired-token UI, and Ollama lifecycle remain manually unverified for the reasons recorded above.
