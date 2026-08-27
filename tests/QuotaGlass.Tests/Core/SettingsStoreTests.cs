using System.Collections.Immutable;
using System.Text.Json;
using QuotaGlass.Core;
using QuotaGlass.Tests.Support;

namespace QuotaGlass.Tests.Core;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();
    private readonly string _path;
    private readonly SettingsStore _store;

    public SettingsStoreTests()
    {
        _path = Path.Combine(_directory.Path, "settings.json");
        _store = new SettingsStore(_path);
    }

    [Fact]
    public async Task LoadAsync_MissingFileReturnsDocumentedDefaults()
    {
        // Break caught: returning an arbitrary or partially initialized settings object for a missing file.
        AppSettings settings = await _store.LoadAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(60), settings.PollInterval);
        Assert.Equal(TimeSpan.FromMinutes(5), settings.IdleInterval);
        Assert.False(settings.OverlayVisible);
        Assert.Equal(80, settings.WarningPercent);
        Assert.Equal(95, settings.CriticalPercent);
        Assert.Equal("Ctrl+Alt+A", settings.Hotkey);
        Assert.Null(settings.Providers["opencode-go"].OpenCodeConsole);
        Assert.Equal(
            ["claude", "codex", "ollama", "opencode-company-seat", "opencode-go"],
            settings.Providers.Keys.OrderBy(providerId => providerId));
    }

    [Fact]
    public async Task LoadAsync_LegacyProviderEnabledFlagsAreRemovedWhenSettingsAreSaved()
    {
        // Break caught: legacy provider flags either affect provider discovery or survive the next save.
        const string selector = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string legacySettings = """
            {
              "PollInterval": "00:01:00",
              "IdleInterval": "00:05:00",
              "Providers": {
                "claude": { "Enabled": false },
                "codex": { "Enabled": true },
                "opencode-go": {
                  "Enabled": false,
                  "OpenCodeConsole": {
                    "Enabled": true,
                    "WorkspaceSelector": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                  }
                },
                "future-provider": { "Enabled": false }
              },
              "OverlayVisible": true,
              "OverlayCorner": 3,
              "OverlayMonitorId": null,
              "OverlayPosition": null,
              "Hotkey": "Ctrl+Alt+A",
              "WarningPercent": 80,
              "CriticalPercent": 95,
              "Autostart": false
            }
            """;
        await File.WriteAllTextAsync(_path, legacySettings, CancellationToken.None);

        AppSettings loaded = await _store.LoadAsync(CancellationToken.None);
        await _store.SaveAsync(loaded, CancellationToken.None);

        Assert.Contains("claude", loaded.Providers.Keys);
        Assert.Contains("codex", loaded.Providers.Keys);
        Assert.Contains("opencode-go", loaded.Providers.Keys);
        Assert.Contains("opencode-company-seat", loaded.Providers.Keys);
        Assert.Contains("ollama", loaded.Providers.Keys);
        Assert.Equal(
            new OpenCodeConsoleSettings(selector),
            loaded.Providers["opencode-go"].OpenCodeConsole);

        using JsonDocument saved = JsonDocument.Parse(await File.ReadAllTextAsync(_path));
        JsonElement providers = saved.RootElement.GetProperty("Providers");
        Assert.All(
            providers.EnumerateObject(),
            provider => Assert.False(provider.Value.TryGetProperty("Enabled", out _)));
        JsonElement console = providers
            .GetProperty("opencode-go")
            .GetProperty("OpenCodeConsole");
        Assert.False(console.TryGetProperty("Enabled", out _));
    }

    [Fact]
    public async Task SaveAsync_OpenCodeConsoleSettingsRoundTripAndNormalizeSelector()
    {
        // Catches the opaque selector being discarded during settings persistence.
        string uppercaseSelector = new('A', 64);
        AppSettings configured = AppSettings.Default with
        {
            Providers = AppSettings.Default.Providers.SetItem(
                "opencode-go",
                new ProviderSettings()
                {
                    OpenCodeConsole = new OpenCodeConsoleSettings(uppercaseSelector),
                }),
        };

        await _store.SaveAsync(configured, CancellationToken.None);
        AppSettings loaded = await _store.LoadAsync(CancellationToken.None);

        OpenCodeConsoleSettings console = Assert.IsType<OpenCodeConsoleSettings>(
            loaded.Providers["opencode-go"].OpenCodeConsole);
        Assert.Equal(new string('a', 64), console.WorkspaceSelector);
        string persisted = await File.ReadAllTextAsync(_path);
        Assert.DoesNotContain(uppercaseSelector, persisted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public async Task LoadAsync_InvalidOpenCodeWorkspaceSelectorReturnsDefaults(string selector)
    {
        // Catches malformed selectors creating an ambiguous or unstable workspace choice.
        AppSettings invalid = AppSettings.Default with
        {
            Providers = AppSettings.Default.Providers.SetItem(
                "opencode-go",
                new ProviderSettings()
                {
                    OpenCodeConsole = new OpenCodeConsoleSettings(selector),
                }),
            OverlayVisible = true,
        };
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(invalid), CancellationToken.None);

        AppSettings loaded = await _store.LoadAsync(CancellationToken.None);

        Assert.Equal(AppSettings.Default, loaded);
    }

    [Fact]
    public async Task LoadAsync_OpenCodeConsoleSettingsOnOtherProviderReturnsDefaults()
    {
        AppSettings invalid = AppSettings.Default with
        {
            Providers = AppSettings.Default.Providers.SetItem(
                "claude",
                new ProviderSettings()
                {
                    OpenCodeConsole = new OpenCodeConsoleSettings(null),
                }),
        };
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(invalid), CancellationToken.None);

        AppSettings loaded = await _store.LoadAsync(CancellationToken.None);

        Assert.Equal(AppSettings.Default, loaded);
    }

    [Fact]
    public async Task LoadAsync_PreOpenCodeSettingsAddsOpenCodeGoProvider()
    {
        // Catches a settings schema addition that discards existing settings or leaves the provider absent.
        AppSettings previous = AppSettings.Default with
        {
            Providers = AppSettings.Default.Providers.Remove("opencode-go"),
            OverlayVisible = true,
            WarningPercent = 75,
        };
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(previous), CancellationToken.None);

        AppSettings loaded = await _store.LoadAsync(CancellationToken.None);

        Assert.Contains("opencode-go", loaded.Providers.Keys);
        Assert.True(loaded.OverlayVisible);
        Assert.Equal(75, loaded.WarningPercent);
    }

    [Fact]
    public async Task LoadAsync_PreCompanySeatSettingsAddsProvider()
    {
        // Catches a settings schema addition that leaves a compiled provider absent.
        AppSettings previous = AppSettings.Default with
        {
            Providers = AppSettings.Default.Providers.Remove("opencode-company-seat"),
            OverlayVisible = true,
        };
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(previous), CancellationToken.None);

        AppSettings loaded = await _store.LoadAsync(CancellationToken.None);

        Assert.Contains("opencode-company-seat", loaded.Providers.Keys);
        Assert.True(loaded.OverlayVisible);
    }

    [Fact]
    public async Task LoadAsync_MalformedFileFallsBackWithoutThrowing()
    {
        // Break caught: malformed persisted JSON escaping from the store instead of using defaults.
        await File.WriteAllTextAsync(_path, "{broken", CancellationToken.None);

        Assert.Equal(AppSettings.Default, await _store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsync_RoundTripsAndLeavesNoTemporaryFile()
    {
        // Break caught: save does not atomically replace the settings file or loses persisted values.
        AppSettings expected = AppSettings.Default with { OverlayVisible = true, WarningPercent = 75 };

        await _store.SaveAsync(expected, CancellationToken.None);
        AppSettings actual = await _store.LoadAsync(CancellationToken.None);

        Assert.Equal(expected.OverlayVisible, actual.OverlayVisible);
        Assert.Equal(expected.WarningPercent, actual.WarningPercent);
        Assert.Equal(expected.CriticalPercent, actual.CriticalPercent);
        Assert.Equal(expected.Providers.OrderBy(pair => pair.Key), actual.Providers.OrderBy(pair => pair.Key));
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public async Task UpdateAsync_AppliesNarrowChangeToLatestSettings()
    {
        // Break caught: overlay persistence replaces newer unrelated settings with a cached full document.
        AppSettings latest = AppSettings.Default with
        {
            Hotkey = "Ctrl+Shift+Q",
            OverlayVisible = true,
            WarningPercent = 72,
        };
        await _store.SaveAsync(latest, CancellationToken.None);

        await _store.UpdateAsync(
            settings => settings with
            {
                OverlayCorner = OverlayCorner.Custom,
                OverlayMonitorId = "SECONDARY",
                OverlayPosition = new OverlayPosition(2100, 90),
            },
            CancellationToken.None);

        AppSettings saved = await _store.LoadAsync(CancellationToken.None);
        Assert.Equal("Ctrl+Shift+Q", saved.Hotkey);
        Assert.True(saved.OverlayVisible);
        Assert.Equal(72, saved.WarningPercent);
        Assert.Equal(OverlayCorner.Custom, saved.OverlayCorner);
        Assert.Equal("SECONDARY", saved.OverlayMonitorId);
        Assert.Equal(new OverlayPosition(2100, 90), saved.OverlayPosition);
    }

    [Fact]
    public async Task UpdateAsync_RereadsValidExternalEditBeforeApplyingNarrowChange()
    {
        // Break caught: drag persistence overwrites a valid external edit before the debounced watcher reload runs.
        using var delayedWatcherStore = new SettingsStore(_path, TimeSpan.FromHours(1));
        await delayedWatcherStore.LoadAsync(CancellationToken.None);
        AppSettings external = AppSettings.Default with
        {
            Hotkey = "Ctrl+Shift+U",
            WarningPercent = 71,
            OverlayVisible = true,
        };
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(external), CancellationToken.None);

        await delayedWatcherStore.UpdateAsync(
            settings => settings with
            {
                OverlayCorner = OverlayCorner.Custom,
                OverlayMonitorId = "SECONDARY",
                OverlayPosition = new OverlayPosition(2200, 100),
            },
            CancellationToken.None);

        AppSettings saved = await delayedWatcherStore.LoadAsync(CancellationToken.None);
        Assert.Equal("Ctrl+Shift+U", saved.Hotkey);
        Assert.Equal(71, saved.WarningPercent);
        Assert.True(saved.OverlayVisible);
        Assert.Equal(external.Providers.OrderBy(pair => pair.Key), saved.Providers.OrderBy(pair => pair.Key));
        Assert.Equal(external.PollInterval, saved.PollInterval);
        Assert.Equal(external.CriticalPercent, saved.CriticalPercent);
        Assert.Equal(OverlayCorner.Custom, saved.OverlayCorner);
        Assert.Equal("SECONDARY", saved.OverlayMonitorId);
        Assert.Equal(new OverlayPosition(2200, 100), saved.OverlayPosition);
    }

    [Fact]
    public async Task UpdateAsync_InvalidExternalEditPreservesLastKnownGoodSettings()
    {
        // Break caught: malformed external JSON makes a narrow overlay update reset unrelated settings to defaults.
        using var delayedWatcherStore = new SettingsStore(_path, TimeSpan.FromHours(1));
        AppSettings lastKnownGood = AppSettings.Default with
        {
            PollInterval = TimeSpan.FromSeconds(45),
            IdleInterval = TimeSpan.FromMinutes(7),
            Providers = AppSettings.Default.Providers.SetItem("ollama", new ProviderSettings()),
            OverlayVisible = true,
            Hotkey = "Ctrl+Shift+L",
            WarningPercent = 68,
            CriticalPercent = 92,
            Autostart = true,
        };
        await delayedWatcherStore.SaveAsync(lastKnownGood, CancellationToken.None);
        await File.WriteAllTextAsync(_path, "{broken", CancellationToken.None);

        AppSettings updated = await delayedWatcherStore.UpdateAsync(
            settings => settings with
            {
                OverlayCorner = OverlayCorner.Custom,
                OverlayMonitorId = "SECONDARY",
                OverlayPosition = new OverlayPosition(2300, 110),
            },
            CancellationToken.None);

        Assert.Equal(lastKnownGood.PollInterval, updated.PollInterval);
        Assert.Equal(lastKnownGood.IdleInterval, updated.IdleInterval);
        Assert.Equal(lastKnownGood.Providers.OrderBy(pair => pair.Key), updated.Providers.OrderBy(pair => pair.Key));
        Assert.Equal(lastKnownGood.OverlayVisible, updated.OverlayVisible);
        Assert.Equal(lastKnownGood.Hotkey, updated.Hotkey);
        Assert.Equal(lastKnownGood.WarningPercent, updated.WarningPercent);
        Assert.Equal(lastKnownGood.CriticalPercent, updated.CriticalPercent);
        Assert.Equal(lastKnownGood.Autostart, updated.Autostart);
        Assert.Equal(OverlayCorner.Custom, updated.OverlayCorner);
        Assert.Equal("SECONDARY", updated.OverlayMonitorId);
        Assert.Equal(new OverlayPosition(2300, 110), updated.OverlayPosition);

        AppSettings saved = await delayedWatcherStore.LoadAsync(CancellationToken.None);
        Assert.Equal(updated.PollInterval, saved.PollInterval);
        Assert.Equal(updated.IdleInterval, saved.IdleInterval);
        Assert.Equal(updated.Providers.OrderBy(pair => pair.Key), saved.Providers.OrderBy(pair => pair.Key));
        Assert.Equal(updated.OverlayVisible, saved.OverlayVisible);
        Assert.Equal(updated.OverlayCorner, saved.OverlayCorner);
        Assert.Equal(updated.OverlayMonitorId, saved.OverlayMonitorId);
        Assert.Equal(updated.OverlayPosition, saved.OverlayPosition);
        Assert.Equal(updated.Hotkey, saved.Hotkey);
        Assert.Equal(updated.WarningPercent, saved.WarningPercent);
        Assert.Equal(updated.CriticalPercent, saved.CriticalPercent);
        Assert.Equal(updated.Autostart, saved.Autostart);
    }

    [Fact]
    public async Task UpdateAsync_SerializesConcurrentReadModifyWriteOperations()
    {
        // Break caught: concurrent narrow updates read the same state and lose completed updates.
        await _store.LoadAsync(CancellationToken.None);

        Task<AppSettings>[] updates = Enumerable.Range(0, 20)
            .Select(_ => _store.UpdateAsync(
                settings => settings with { Hotkey = settings.Hotkey + "x" },
                CancellationToken.None))
            .ToArray();
        await Task.WhenAll(updates);

        AppSettings saved = await _store.LoadAsync(CancellationToken.None);
        Assert.Equal(AppSettings.Default.Hotkey + new string('x', 20), saved.Hotkey);
    }

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public async Task LoadAsync_InvalidCompleteDocumentReturnsDefaults(AppSettings invalidSettings)
    {
        // Break caught: accepting one invalid field and exposing a partly invalid settings object.
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(invalidSettings), CancellationToken.None);

        Assert.Equal(AppSettings.Default, await _store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LoadAsync_OldProviderDictionaryGainsDefaultsAndPreservesUnknownEntries()
    {
        // Break caught: adding a compiled provider rejects an older valid file or deletes unknown entries.
        AppSettings old = AppSettings.Default with
        {
            Providers = ImmutableDictionary<string, ProviderSettings>.Empty
                .Add("claude", new ProviderSettings())
                .Add("codex", new ProviderSettings())
                .Add("future-provider", new ProviderSettings()),
        };
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(old), CancellationToken.None);

        AppSettings loaded = await _store.LoadAsync(CancellationToken.None);
        await _store.SaveAsync(loaded, CancellationToken.None);
        AppSettings saved = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(_path))!;

        Assert.Contains("ollama", loaded.Providers.Keys);
        Assert.Contains("future-provider", loaded.Providers.Keys);
        Assert.Equal(loaded.Providers.OrderBy(pair => pair.Key), saved.Providers.OrderBy(pair => pair.Key));
    }

    [Fact]
    public async Task Changed_MalformedReloadPreservesActiveSettingsAndLogsOnce()
    {
        // Break caught: a partially written watched file resets active settings or floods sensitive diagnostics.
        string logPath = Path.Combine(_directory.Path, "settings.log");
        AppSettings active = AppSettings.Default with
        {
            Hotkey = "Ctrl+Shift+L",
            WarningPercent = 70,
        };
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(active), CancellationToken.None);
        using var store = new SettingsStore(
            _path,
            TimeSpan.FromMilliseconds(30),
            new RollingFileLog(logPath));
        await store.LoadAsync(CancellationToken.None);
        int changes = 0;
        store.Changed += (_, _) => Interlocked.Increment(ref changes);

        await File.WriteAllTextAsync(_path, "{\"secret\":\"credential-value\"", CancellationToken.None);
        await Task.Delay(500);

        AppSettings updated = await store.UpdateAsync(
            settings => settings with { OverlayVisible = true },
            CancellationToken.None);
        string log = await File.ReadAllTextAsync(logPath);

        Assert.Equal("Ctrl+Shift+L", updated.Hotkey);
        Assert.Equal(70, updated.WarningPercent);
        Assert.Equal(0, Volatile.Read(ref changes));
        Assert.Equal(1, log.Split(" settings invalid", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("secret", log, StringComparison.Ordinal);
        Assert.DoesNotContain("credential-value", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changed_ExternalValidWriteRaisesOneDebouncedEvent()
    {
        // Break caught: watcher duplicate notifications expose repeated settings changes to consumers.
        await _store.LoadAsync(CancellationToken.None);
        AppSettings expected = AppSettings.Default with { OverlayVisible = true };
        var changes = new List<AppSettings>();
        var changeReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _store.Changed += OnChanged;

        try
        {
            await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(expected), CancellationToken.None);
            await changeReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(500);

            Assert.Collection(changes, changed =>
            {
                Assert.True(changed.OverlayVisible);
                Assert.Equal(expected.Providers.OrderBy(pair => pair.Key), changed.Providers.OrderBy(pair => pair.Key));
            });
        }
        finally
        {
            _store.Changed -= OnChanged;
        }

        void OnChanged(object? sender, AppSettings settings)
        {
            lock (changes)
            {
                changes.Add(settings);
            }

            changeReceived.TrySetResult();
        }
    }

    public static IEnumerable<object[]> InvalidSettings()
    {
        yield return [AppSettings.Default with { WarningPercent = -1 }];
        yield return [AppSettings.Default with { WarningPercent = 95, CriticalPercent = 95 }];
        yield return [AppSettings.Default with { CriticalPercent = 101 }];
        yield return [AppSettings.Default with { PollInterval = TimeSpan.Zero }];
        yield return [AppSettings.Default with { IdleInterval = TimeSpan.FromSeconds(-1) }];
    }

    public void Dispose()
    {
        _store.Dispose();
        _directory.Dispose();
    }
}
