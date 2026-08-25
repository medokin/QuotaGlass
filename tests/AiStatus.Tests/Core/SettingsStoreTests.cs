using System.Collections.Immutable;
using System.Text.Json;
using AiStatus.Core;
using AiStatus.Tests.Support;

namespace AiStatus.Tests.Core;

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
        Assert.All(settings.Providers.Values, provider => Assert.True(provider.Enabled));
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

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public async Task LoadAsync_InvalidCompleteDocumentReturnsDefaults(AppSettings invalidSettings)
    {
        // Break caught: accepting one invalid field and exposing a partly invalid settings object.
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(invalidSettings), CancellationToken.None);

        Assert.Equal(AppSettings.Default, await _store.LoadAsync(CancellationToken.None));
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
        yield return [AppSettings.Default with
        {
            Providers = ImmutableDictionary<string, ProviderSettings>.Empty
                .Add("claude", new(true))
                .Add("codex", new(true)),
        }];
    }

    public void Dispose()
    {
        _store.Dispose();
        _directory.Dispose();
    }
}
