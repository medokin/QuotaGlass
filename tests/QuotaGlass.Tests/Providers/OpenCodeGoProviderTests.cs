using System.Net;
using System.Net.Http.Headers;
using System.Text;
using QuotaGlass.Model;
using QuotaGlass.Providers;
using QuotaGlass.Tests.Support;

namespace QuotaGlass.Tests.Providers;

public sealed class OpenCodeGoProviderTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    [Fact]
    public async Task FetchAsync_MapsUsageWindowsAndConfiguredSeverity()
    {
        // Catches a provider that omits valid windows, loses reset times, or clamps policy input.
        var handler = new StubHttpMessageHandler(_ => JsonResponse(ReadFixture("opencode-go-usage.json")));

        ProviderSnapshot snapshot = await CreateProvider(handler)
            .FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal(HealthState.Ok, snapshot.Health);
        Assert.Collection(
            snapshot.Windows,
            rolling =>
            {
                Assert.Equal("rolling", rolling.Label);
                Assert.Equal(23, rolling.Percent);
                Assert.Equal(DateTimeOffset.Parse("2026-08-27T12:00:00Z"), rolling.ResetsAt);
                Assert.Equal(Severity.Normal, rolling.Severity);
            },
            weekly =>
            {
                Assert.Equal("weekly", weekly.Label);
                Assert.Equal(85, weekly.Percent);
                Assert.Equal(DateTimeOffset.Parse("2026-08-31T00:00:00Z"), weekly.ResetsAt);
                Assert.Equal(Severity.Warning, weekly.Severity);
            },
            monthly =>
            {
                Assert.Equal("monthly", monthly.Label);
                Assert.Equal(110, monthly.Percent);
                Assert.Equal(DateTimeOffset.Parse("2026-09-15T00:00:00Z"), monthly.ResetsAt);
                Assert.Equal(Severity.Critical, monthly.Severity);
            });
    }

    [Fact]
    public async Task FetchAsync_SendsCredentialOnlyToFixedJsonEndpoint()
    {
        // Catches an endpoint or header regression that misroutes the credential or negotiates non-JSON content.
        Uri? requestUri = null;
        AuthenticationHeaderValue? authorization = null;
        string[] acceptedMediaTypes = [];
        var handler = new StubHttpMessageHandler(request =>
        {
            requestUri = request.RequestUri;
            authorization = request.Headers.Authorization;
            acceptedMediaTypes = request.Headers.Accept.Select(value => value.MediaType!).ToArray();
            return JsonResponse(ReadFixture("opencode-go-usage.json"));
        });

        ProviderSnapshot snapshot = await CreateProvider(handler).FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal(HealthState.Ok, snapshot.Health);
        Assert.Equal("https://opencode.ai/zen/go/v1/usage", requestUri?.ToString());
        Assert.Equal("Bearer", authorization?.Scheme);
        Assert.Equal("unit-test-api-key", authorization?.Parameter);
        Assert.Equal(["application/json"], acceptedMediaTypes);
    }

    [Fact]
    public async Task FetchAsync_MissingCredentialFileReturnsQuietNotConfiguredSnapshot()
    {
        // Catches a missing OpenCode installation becoming a repeated provider alert or HTTP request.
        var handler = new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run"));
        var provider = new OpenCodeGoProvider(
            Path.Combine(_directory.Path, "missing-auth.json"),
            handler,
            SeverityFromPercent);

        ProviderFetchResult result = await provider.FetchAsync(CancellationToken.None);
        ProviderSnapshot snapshot = Assert.IsType<ProviderSnapshot>(result.Snapshot);

        Assert.Equal(ProviderFetchOutcome.NotConfigured, result.Outcome);
        Assert.Equal(HealthState.Unreachable, snapshot.Health);
        Assert.Null(snapshot.Error);
        Assert.Empty(snapshot.Windows);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"opencode-go\":{\"type\":\"oauth\",\"key\":\"unit-test-api-key\"}}")]
    [InlineData("{\"opencode-go\":{\"type\":\"api\",\"key\":\"\"}}")]
    public async Task FetchAsync_MissingRequiredCredentialFieldsReturnsNotConfigured(string credential)
    {
        // Catches unrelated or incomplete OpenCode auth entries being used as bearer credentials.
        var handler = new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run"));

        ProviderFetchResult result = await CreateProvider(handler, credential).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.NotConfigured, result.Outcome);
        Assert.Equal(HealthState.Unreachable, Assert.IsType<ProviderSnapshot>(result.Snapshot).Health);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task FetchAsync_RejectedCredentialReturnsSanitizedAuthExpired(HttpStatusCode statusCode)
    {
        // Catches rejected credentials being reported as transient failures or leaking response content.
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("sensitive server response"),
        });

        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);
        ProviderSnapshot snapshot = Assert.IsType<ProviderSnapshot>(result.Snapshot);

        Assert.Equal(ProviderFetchOutcome.AuthenticationRequired, result.Outcome);
        Assert.Equal(statusCode, result.StatusCode);
        Assert.Equal(HealthState.AuthExpired, snapshot.Health);
        Assert.Equal("re-auth: run opencode auth login", snapshot.Error);
        Assert.DoesNotContain("sensitive", snapshot.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchAsync_MalformedOptionalWindowsDoNotDiscardValidWindow()
    {
        // Catches one omitted or malformed optional window invalidating all OpenCode Go usage.
        const string response = """
            {"usage":{"rolling":{"percent":42,"resetsAt":"2026-08-27T12:00:00Z"},"weekly":{"percent":"bad","resetsAt":"bad"}}}
            """;
        var handler = new StubHttpMessageHandler(_ => JsonResponse(response));

        ProviderSnapshot snapshot = await CreateProvider(handler).FetchSnapshotAsync(CancellationToken.None);

        UsageWindow rolling = Assert.Single(snapshot.Windows);
        Assert.Equal("rolling", rolling.Label);
        Assert.Equal(42, rolling.Percent);
    }

    [Theory]
    [InlineData("{\"usage\":", "application/json")]
    [InlineData("<html>login</html>", "text/html")]
    [InlineData("{\"usage\":{\"rolling\":{\"percent\":23}}}", "application/json")]
    public async Task FetchAsync_InvalidUsageResponseReturnsInvalidResponse(string body, string contentType)
    {
        // Catches malformed, non-JSON, or incomplete responses being published as empty successful snapshots.
        var handler = new StubHttpMessageHandler(_ => JsonResponse(body, contentType));

        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.InvalidResponse, result.Outcome);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task FetchAsync_ServerFailureReturnsTransientFailure()
    {
        // Catches an endpoint outage replacing retained quota data with an empty snapshot.
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.TransientFailure, result.Outcome);
        Assert.Equal(HttpStatusCode.BadGateway, result.StatusCode);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task FetchAsync_StopsReadingAfterRequiredCredentialFields()
    {
        // Catches a credential reader that buffers unrelated provider credentials after the OpenCode Go key.
        var handler = new StubHttpMessageHandler(_ => JsonResponse(ReadFixture("opencode-go-usage.json")));
        var credential = new SingleByteCredentialStream(
            """
            {"opencode-go":{"type":"api","key":"unit-test-api-key","metadata":"protected-tail"}}
            """,
            "metadata");
        var provider = new OpenCodeGoProvider(
            "credential-stream.json",
            handler,
            SeverityFromPercent,
            null,
            _ => credential);

        ProviderSnapshot snapshot = await provider.FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal(HealthState.Ok, snapshot.Health);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_MalformedCredentialThrowsSanitizedFailure()
    {
        // Catches a credential parse failure whose exception exposes the API key or document body.
        var handler = new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run"));
        OpenCodeGoProvider provider = CreateProvider(
            handler,
            "{\"opencode-go\":{\"type\":\"api\",\"key\":\"unit-test-api-key\"");

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.FetchSnapshotAsync(CancellationToken.None));

        Assert.DoesNotContain("unit-test-api-key", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.RequestCount);
    }

    public void Dispose() => _directory.Dispose();

    private OpenCodeGoProvider CreateProvider(HttpMessageHandler handler) => CreateProvider(
        handler,
        """
        {"opencode-go":{"type":"api","key":"unit-test-api-key"}}
        """);

    private OpenCodeGoProvider CreateProvider(HttpMessageHandler handler, string credential)
    {
        string credentialPath = _directory.WriteFile(
            "opencode-auth.json",
            credential);
        return new OpenCodeGoProvider(
            credentialPath,
            handler,
            SeverityFromPercent);
    }

    private static Severity SeverityFromPercent(double? percent) =>
        SeverityPolicy.FromPercent(percent, 80, 95);

    private static HttpResponseMessage JsonResponse(string json, string contentType = "application/json") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, contentType),
    };

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

    private sealed class SingleByteCredentialStream : Stream
    {
        private readonly byte[] _content;
        private readonly byte[] _protectedTail;
        private int _position;

        public SingleByteCredentialStream(string credential, string protectedTail)
        {
            _content = Encoding.UTF8.GetBytes(credential);
            _protectedTail = Encoding.UTF8.GetBytes(protectedTail);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _content.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _content.Length)
            {
                return 0;
            }

            if (_position + _protectedTail.Length <= _content.Length &&
                _content.AsSpan(_position, _protectedTail.Length).SequenceEqual(_protectedTail))
            {
                throw new Xunit.Sdk.XunitException("Credential reader consumed unrelated credential material.");
            }

            buffer[offset] = _content[_position++];
            return 1;
        }

        public override int ReadByte()
        {
            byte[] buffer = new byte[1];
            return Read(buffer, 0, 1) == 0 ? -1 : buffer[0];
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
