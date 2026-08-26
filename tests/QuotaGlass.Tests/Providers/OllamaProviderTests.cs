using System.Net;
using System.Text;
using QuotaGlass.Model;
using QuotaGlass.Providers;
using QuotaGlass.Tests.Support;

namespace QuotaGlass.Tests.Providers;

public sealed class OllamaProviderTests
{
    [Fact]
    public async Task FetchAsync_ProducesInfoWithoutUsageWindows()
    {
        // Catches a mapper that loses the local version or reports Ollama models as quota windows.
        ProviderSnapshot snapshot = await CreateFixtureProvider()
            .FetchAsync(CancellationToken.None);

        Assert.Equal(HealthState.Ok, snapshot.Health);
        Assert.Empty(snapshot.Windows);
        Assert.Contains(new InfoLine("Version", "0.32.15"), snapshot.Info);
        Assert.Contains(new InfoLine("Loaded models", "0"), snapshot.Info);
    }

    [Fact]
    public async Task FetchAsync_ConnectionRefusedIsQuietlyUnreachable()
    {
        // Catches a local connection failure that is surfaced as an application error.
        var handler = new StubHttpMessageHandler(_ =>
            throw new HttpRequestException("refused", null, HttpStatusCode.ServiceUnavailable));

        ProviderSnapshot snapshot = await new OllamaProvider(handler)
            .FetchAsync(CancellationToken.None);

        Assert.Equal(HealthState.Unreachable, snapshot.Health);
        Assert.Null(snapshot.Error);
        Assert.Empty(snapshot.Windows);
    }

    [Fact]
    public async Task FetchAsync_AddsNoStoreToEveryRequest()
    {
        // Catches localhost status responses being reused by an intermediary cache.
        var cacheDirectives = new List<bool>();
        var handler = new StubHttpMessageHandler(request =>
        {
            cacheDirectives.Add(request.Headers.CacheControl?.NoStore == true);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    request.RequestUri!.AbsolutePath.EndsWith("version", StringComparison.Ordinal)
                        ? ReadFixture("ollama-version.json")
                        : ReadFixture("ollama-ps.json"),
                    Encoding.UTF8,
                    "application/json"),
            };
        });

        ProviderSnapshot snapshot = await new OllamaProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(HealthState.Ok, snapshot.Health);
        Assert.Equal([true, true], cacheDirectives);
    }

    [Fact]
    public async Task FetchAsync_CallerCancellationPropagates()
    {
        // Catches an exception handler that converts caller-requested cancellation into an unreachable snapshot.
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var handler = new CancellationAwareHttpMessageHandler();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OllamaProvider(handler).FetchAsync(cancellationSource.Token));
    }

    private static OllamaProvider CreateFixtureProvider() => new(new StubHttpMessageHandler(request =>
    {
        string body = request.RequestUri?.AbsolutePath switch
        {
            "/api/version" => ReadFixture("ollama-version.json"),
            "/api/ps" => ReadFixture("ollama-ps.json"),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected request URI: {request.RequestUri}")
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }));

    private static string ReadFixture(string fileName) =>
        File.ReadAllText(Path.Combine(FindFixtureDirectory(), fileName));

    private static string FindFixtureDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "tests", "QuotaGlass.Tests", "fixtures");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("The source fixture directory was not found.");
    }

    private sealed class CancellationAwareHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromCanceled<HttpResponseMessage>(cancellationToken);
    }
}
