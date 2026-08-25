using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AiStatus.Model;
using AiStatus.Providers;
using AiStatus.Tests.Support;

namespace AiStatus.Tests.Providers;

public sealed class CodexProviderTests : IDisposable
{
    private readonly List<TemporaryDirectory> _directories = [];

    [Fact]
    public async Task FetchAsync_MapsUnixResetAndAdditionalWindows()
    {
        // Catches a provider that treats reset_at as an ISO value or omits additional limits.
        ProviderSnapshot snapshot = await CreateProvider("codex-wham.json", "application/json")
            .FetchAsync(CancellationToken.None);

        Assert.Equal(HealthState.Ok, snapshot.Health);
        Assert.Equal("prolite", snapshot.PlanLabel);
        Assert.Equal("7d", snapshot.Windows[0].Label);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788272097), snapshot.Windows[0].ResetsAt);
        Assert.Contains(snapshot.Windows, w => w.Label == "GPT-5.3-Codex-Spark 5h");
        Assert.Contains(snapshot.Windows, w => w.Label == "GPT-5.3-Codex-Spark 7d");
    }

    [Fact]
    public async Task FetchAsync_AddsRequiredAuthenticationHeaders()
    {
        // Catches a request that omits the account header or uses an incorrect bearer credential.
        AuthenticationHeaderValue? authorization = null;
        string? accountId = null;
        Uri? requestUri = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            authorization = request.Headers.Authorization;
            accountId = request.Headers.GetValues("chatgpt-account-id").Single();
            requestUri = request.RequestUri;
            return JsonResponse(ReadFixture("codex-wham.json"), "application/json");
        });

        ProviderSnapshot snapshot = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(HealthState.Ok, snapshot.Health);
        Assert.Equal("Bearer", authorization?.Scheme);
        Assert.Equal("unit-test-access-token", authorization?.Parameter);
        Assert.Equal("unit-test-account-id", accountId);
        Assert.Equal("https://chatgpt.com/backend-api/wham/usage", requestUri?.ToString());
    }

    [Fact]
    public async Task FetchAsync_HtmlUnder200ReturnsDegradedSnapshot()
    {
        // Catches a provider that attempts to deserialize a successful HTML response.
        ProviderSnapshot snapshot = await CreateProvider("codex-html-200.html", "text/html")
            .FetchAsync(CancellationToken.None);

        Assert.Equal(HealthState.Degraded, snapshot.Health);
        Assert.Equal("Codex usage endpoint returned non-JSON content", snapshot.Error);
        Assert.Empty(snapshot.Windows);
    }

    [Fact]
    public async Task FetchAsync_AddsNoStoreToUsageRequest()
    {
        // Catches pooled clients that permit credential-bound usage responses to be cached.
        bool noStore = false;
        var handler = new StubHttpMessageHandler(request =>
        {
            noStore = request.Headers.CacheControl?.NoStore == true;
            return JsonResponse(ReadFixture("codex-wham.json"), "application/json");
        });

        ProviderSnapshot snapshot = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(HealthState.Ok, snapshot.Health);
        Assert.True(noStore);
    }

    [Fact]
    public async Task FetchAsync_UnauthorizedResponseReturnsAuthExpired()
    {
        // Catches an expired Codex credential reported as a generic transport failure.
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        ProviderSnapshot snapshot = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(HealthState.AuthExpired, snapshot.Health);
        Assert.Equal("re-auth: run codex login", snapshot.Error);
    }

    [Fact]
    public async Task FetchAsync_IgnoresNullSecondaryWindow()
    {
        // Catches a mapper that creates a phantom window for a null secondary_window.
        ProviderSnapshot snapshot = await CreateProvider("codex-wham.json", "application/json")
            .FetchAsync(CancellationToken.None);

        Assert.Equal(3, snapshot.Windows.Length);
        Assert.DoesNotContain(snapshot.Windows, window => window.Label == "0s");
    }

    [Theory]
    [InlineData(18_000, "5h")]
    [InlineData(604_800, "7d")]
    [InlineData(172_800, "2d")]
    [InlineData(7_200, "2h")]
    [InlineData(300, "300s")]
    public async Task FetchAsync_FormatsWindowDurationInvariantly(int seconds, string expectedLabel)
    {
        // Catches a duration mapper that loses the documented compact label format.
        string response = $$"""
            {"plan_type":"prolite","rate_limit":{"allowed":true,"limit_reached":false,"primary_window":{"used_percent":10,"limit_window_seconds":{{seconds}},"reset_at":1788272097},"secondary_window":null},"additional_rate_limits":[]}
            """;
        var handler = new StubHttpMessageHandler(_ => JsonResponse(response, "application/json"));

        ProviderSnapshot snapshot = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(expectedLabel, Assert.Single(snapshot.Windows).Label);
    }

    [Fact]
    public async Task FetchAsync_LimitReachedOverridesConfiguredSeverity()
    {
        // Catches a provider that downgrades a server-reported exhausted window using percent thresholds.
        const string response = """
            {"plan_type":"prolite","rate_limit":{"allowed":false,"limit_reached":true,"primary_window":{"used_percent":1,"limit_window_seconds":604800,"reset_at":1788272097},"secondary_window":null},"additional_rate_limits":[]}
            """;
        var handler = new StubHttpMessageHandler(_ => JsonResponse(response, "application/json"));

        ProviderSnapshot snapshot = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(Severity.Critical, Assert.Single(snapshot.Windows).Severity);
    }

    [Fact]
    public async Task FetchAsync_StopsReadingAfterDirectCredentialFields()
    {
        // Catches a credential reader that buffers or reads refresh-token material after both direct token fields.
        var handler = new StubHttpMessageHandler(_ => JsonResponse(ReadFixture("codex-wham.json"), "application/json"));
        var credential = new SingleByteCredentialStream(
            """
            {"tokens":{"access_token":"unit-test-access-token","account_id":"unit-test-account-id","refresh_token":"sentinel-refresh-token"}}
            """,
            "refresh_token");
        var provider = new CodexProvider(
            "credential-stream.json",
            handler,
            percent => SeverityPolicy.FromPercent(percent, 80, 95),
            null,
            _ => credential);

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(HealthState.Ok, snapshot.Health);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_RejectsCredentialFieldsOutsideTokensObject()
    {
        // Catches a scanner that accepts access credentials from an object other than /tokens.
        var handler = new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run"));
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        var provider = new CodexProvider(
            directory.WriteFile(
                "auth.json",
                """
                {"other":{"access_token":"unit-test-access-token","account_id":"unit-test-account-id"}}
                """),
            handler,
            percent => SeverityPolicy.FromPercent(percent, 80, 95));

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(HealthState.Degraded, snapshot.Health);
        Assert.Equal(0, handler.RequestCount);
    }

    private CodexProvider CreateProvider(string fixtureName, string contentType) =>
        CreateProvider(new StubHttpMessageHandler(_ => JsonResponse(ReadFixture(fixtureName), contentType)));

    private CodexProvider CreateProvider(HttpMessageHandler handler)
    {
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        string credentialPath = directory.WriteFile(
            "auth.json",
            """
            {"tokens":{"access_token":"unit-test-access-token","account_id":"unit-test-account-id"}}
            """);

        return new CodexProvider(
            credentialPath,
            handler,
            percent => SeverityPolicy.FromPercent(percent, 80, 95));
    }

    private static HttpResponseMessage JsonResponse(string body, string contentType) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, contentType)
    };

    private static string ReadFixture(string fileName) =>
        File.ReadAllText(Path.Combine(FindFixtureDirectory(), fileName));

    private static string FindFixtureDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "tests", "AiStatus.Tests", "fixtures");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("The source fixture directory was not found.");
    }

    public void Dispose()
    {
        foreach (TemporaryDirectory directory in _directories)
        {
            directory.Dispose();
        }
    }

    private sealed class SingleByteCredentialStream : Stream
    {
        private readonly byte[] _credential;
        private readonly int _protectedTailOffset;
        private int _position;

        public SingleByteCredentialStream(string credential, string protectedTail)
        {
            _credential = Encoding.UTF8.GetBytes(credential);
            _protectedTailOffset = credential.IndexOf(protectedTail, StringComparison.Ordinal);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count != 1)
            {
                throw new Xunit.Sdk.XunitException("credential reader must not request a bulk buffer");
            }

            if (_position >= _protectedTailOffset)
            {
                throw new Xunit.Sdk.XunitException("refresh-token tail must not be read");
            }

            buffer[offset] = _credential[_position++];
            return 1;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
