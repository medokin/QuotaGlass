using System.Net;
using System.Text;
using ReservePane.Model;
using ReservePane.Providers;
using ReservePane.Tests.Support;

namespace ReservePane.Tests.Providers;

public sealed class OllamaProviderTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task IsAvailableAsync_AnyLocalHttpResponseIsAvailable(HttpStatusCode statusCode)
    {
        // Catches discovery treating HTTP status or response content as a missing local service.
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("not-json", Encoding.UTF8, "text/plain"),
        });

        IProviderAvailability availability = new OllamaProvider(handler);
        bool result = await availability.IsAvailableAsync(CancellationToken.None);

        Assert.True(result);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task IsAvailableAsync_ConnectionFailureIsUnavailable()
    {
        // Catches a refused local connection being reported as discovered.
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("refused"));

        IProviderAvailability availability = new OllamaProvider(handler);
        bool result = await availability.IsAvailableAsync(CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task IsAvailableAsync_CallerCancellationPropagates()
    {
        // Catches cancellation being reclassified as an unavailable local service.
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OllamaProvider(new CancellationAwareHttpMessageHandler())
                .IsAvailableAsync(cancellationSource.Token));
    }

    [Fact]
    public async Task FetchAsync_ProducesInfoWithoutUsageWindows()
    {
        // Catches a mapper that loses the local version or reports Ollama models as quota windows.
        ProviderFetchResult result = await CreateFixtureProvider().FetchAsync(CancellationToken.None);
        ProviderSnapshot snapshot = Assert.IsType<ProviderSnapshot>(result.Snapshot);

        Assert.Equal(ProviderFetchOutcome.Success, result.Outcome);
        Assert.Equal(HealthState.Ok, snapshot.Health);
        Assert.Empty(snapshot.Windows);
        Assert.Contains(new InfoLine("Version", "0.32.15"), snapshot.Info);
        Assert.Contains(new InfoLine("Loaded models", "0"), snapshot.Info);
    }

    [Fact]
    public async Task FetchAsync_ConnectionRefusedReturnsTransientFailure()
    {
        // Catches a local connection failure that is surfaced as an application error.
        var handler = new StubHttpMessageHandler(_ =>
            throw new HttpRequestException("refused", null, HttpStatusCode.ServiceUnavailable));

        ProviderFetchResult result = await new OllamaProvider(handler)
            .FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.TransientFailure, result.Outcome);
        Assert.Null(result.Snapshot);
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

        ProviderSnapshot snapshot = await new OllamaProvider(handler).FetchSnapshotAsync(CancellationToken.None);

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
            new OllamaProvider(handler).FetchSnapshotAsync(cancellationSource.Token));
    }

    [Fact]
    public async Task FetchAsync_InvalidJsonReturnsInvalidResponse()
    {
        // Break caught: malformed local JSON is treated as a successful empty status.
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"version\":", Encoding.UTF8, "application/json"),
        });

        ProviderFetchResult result = await new OllamaProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.InvalidResponse, result.Outcome);
        Assert.Null(result.Snapshot);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"version\":null}")]
    [InlineData("{\"version\":42}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    public async Task FetchAsync_InvalidVersionShapeReturnsInvalidResponse(string versionJson)
    {
        // Break caught: a valid JSON document with a missing or non-string version escapes as an exception.
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(versionJson, Encoding.UTF8, "application/json"),
        });

        ProviderFetchResult result = await new OllamaProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.InvalidResponse, result.Outcome);
        Assert.Null(result.Snapshot);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"models\":null}")]
    [InlineData("{\"models\":{}}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    public async Task FetchAsync_InvalidModelsShapeReturnsInvalidResponse(string processJson)
    {
        // Break caught: a valid process document with a missing or non-array models field is transiently reclassified.
        var handler = new StubHttpMessageHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                request.RequestUri!.AbsolutePath.EndsWith("version", StringComparison.Ordinal)
                    ? "{\"version\":\"0.32.15\"}"
                    : processJson,
                Encoding.UTF8,
                "application/json"),
        });

        ProviderFetchResult result = await new OllamaProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.InvalidResponse, result.Outcome);
        Assert.Null(result.Snapshot);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "30", 30)]
    [InlineData(HttpStatusCode.ServiceUnavailable, null, 300)]
    public async Task FetchAsync_RateLimitReturnsSafeCooldown(
        HttpStatusCode statusCode,
        string? retryAfter,
        int expectedSeconds)
    {
        // Break caught: an Ollama 429 or 503 resets consecutive failures.
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(statusCode);
            if (retryAfter is not null)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            }

            return response;
        });

        ProviderFetchResult result = await new OllamaProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), result.RetryAfter);
        Assert.Null(result.Snapshot);
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
            string candidate = Path.Combine(directory.FullName, "tests", "ReservePane.Tests", "fixtures");
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
