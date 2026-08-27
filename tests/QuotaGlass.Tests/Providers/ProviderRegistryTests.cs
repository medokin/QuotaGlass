using System.Net;
using QuotaGlass.Core;
using QuotaGlass.Model;
using QuotaGlass.Providers;
using QuotaGlass.Tests.Support;

namespace QuotaGlass.Tests.Providers;

public sealed class ProviderRegistryTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    [Fact]
    public void Create_ReturnsTheFourCompiledProvidersInStableOrder()
    {
        AppSettings settings = AppSettings.Default;

        using ProviderRegistry registry = ProviderRegistry.Create(
            () => settings,
            CreatePaths());

        Assert.Collection(
            registry.Providers,
            provider => Assert.Equal("claude", provider.Id),
            provider => Assert.Equal("codex", provider.Id),
            provider => Assert.Equal("opencode-go", provider.Id),
            provider => Assert.Equal("ollama", provider.Id));

        IStatusProvider[] originalProviders = registry.Providers.ToArray();
        settings = settings with
        {
            Providers = settings.Providers.SetItem("codex", new ProviderSettings(false)),
        };

        Assert.Equal(originalProviders, registry.Providers);
    }

    [Fact]
    public void Create_OwnsExactlyOneConfiguredSocketsHandlerPerHost()
    {
        using ProviderRegistry registry = ProviderRegistry.Create(
            () => AppSettings.Default,
            CreatePaths());

        Assert.Equal(4, registry.Handlers.Count);
        Assert.Equal(4, registry.Handlers.Distinct().Count());
        Assert.All(registry.Handlers, handler =>
        {
            Assert.Equal(
                DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                handler.AutomaticDecompression);
            Assert.Equal(TimeSpan.FromMinutes(15), handler.PooledConnectionLifetime);
        });
    }

    [Fact]
    public void SeverityDelegate_ReadsLatestThresholdsWithoutRecreatingProviders()
    {
        AppSettings settings = AppSettings.Default;
        using ProviderRegistry registry = ProviderRegistry.Create(() => settings, CreatePaths());
        IStatusProvider[] providers = registry.Providers.ToArray();

        Assert.Equal(Severity.Warning, registry.SeverityFromPercent(85));

        settings = settings with { WarningPercent = 90 };

        Assert.Equal(Severity.Normal, registry.SeverityFromPercent(85));
        Assert.Equal(providers, registry.Providers);
    }

    [Fact]
    public async Task Dispose_DisposesEveryOwnedHandlerAndIsIdempotent()
    {
        ProviderRegistry registry = ProviderRegistry.Create(
            () => AppSettings.Default,
            CreatePaths());
        HttpClient[] clients = registry.Handlers
            .Select(handler => new HttpClient(handler, disposeHandler: false))
            .ToArray();

        registry.Dispose();
        registry.Dispose();

        foreach (HttpClient client in clients)
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => client.GetAsync("http://127.0.0.1:1", CancellationToken.None));
            client.Dispose();
        }
    }

    public void Dispose() => _directory.Dispose();

    private AppPaths CreatePaths() => new(
        Path.Combine(_directory.Path, "claude.json"),
        Path.Combine(_directory.Path, "codex.json"),
        Path.Combine(_directory.Path, "opencode.json"),
        Path.Combine(_directory.Path, "settings.json"),
        Path.Combine(_directory.Path, "log.txt"));
}
