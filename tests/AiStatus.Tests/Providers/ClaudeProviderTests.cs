using System.Net;
using System.Text;
using System.Text.Json;
using AiStatus.Model;
using AiStatus.Providers;
using AiStatus.Tests.Support;

namespace AiStatus.Tests.Providers;

public sealed class ClaudeProviderTests : IDisposable
{
    private readonly List<TemporaryDirectory> _directories = [];
    [Fact]
    public async Task FetchAsync_MapsLimitsAndUncappedSpend()
    {
        // Catches a provider that maps obsolete top-level windows or treats uncapped spend as a quota.
        ProviderSnapshot snapshot = await CreateProviderWithFixtures().FetchAsync(CancellationToken.None);

        Assert.Equal(HealthState.Ok, snapshot.Health);
        Assert.Equal("team_standard", snapshot.PlanLabel);
        Assert.Collection(snapshot.Windows,
            session =>
            {
                Assert.Equal("session", session.Label);
                Assert.Equal(2d, session.Percent);
                Assert.Equal(Severity.Normal, session.Severity);
            },
            weekly =>
            {
                Assert.Equal("weekly", weekly.Label);
                Assert.Equal(95d, weekly.Percent);
                Assert.Equal(Severity.Critical, weekly.Severity);
            });
        Assert.Contains(snapshot.Info, line =>
            line.Label == "Extra usage" && line.Value == "EUR 322.52 this cycle (no cap set)");
    }

    [Fact]
    public async Task FetchAsync_VendorSeverityOverridesDerivedSeverity()
    {
        // Catches a provider that discards the vendor severity in favor of configurable thresholds.
        ProviderSnapshot snapshot = await CreateProviderWithFixtures(percent =>
                SeverityPolicy.FromPercent(percent, 50, 60))
            .FetchAsync(CancellationToken.None);

        Assert.Equal(Severity.Normal, snapshot.Windows[0].Severity);
    }

    [Fact]
    public async Task FetchAsync_ExpiredCredentialSkipsHttpAndReturnsAuthExpired()
    {
        // Catches a provider that sends an expired credential over HTTP.
        var handler = new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run"));
        ClaudeProvider provider = CreateProvider(handler, expiresAtUnixMilliseconds: 0);

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(HealthState.AuthExpired, snapshot.Health);
        Assert.Equal("re-auth: run claude login", snapshot.Error);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_MalformedCredentialDoesNotExposeItsToken()
    {
        // Catches a provider that lets a credential parse exception escape with authentication material.
        var handler = new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run"));
        ClaudeProvider provider = CreateProviderWithCredential(
            handler,
            "{\"claudeAiOauth\":{\"accessToken\":\"unit-test-access-token\"");

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(HealthState.Degraded, snapshot.Health);
        Assert.DoesNotContain("unit-test-access-token", snapshot.Error ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_MissingOptionalUsageFieldsReturnsEmptySafeSnapshot()
    {
        // Catches a provider that dereferences optional vendor fields and aborts an otherwise valid response.
        const string usage = """
            {"five_hour":null,"seven_day":null,"seven_day_opus":null,"extra_usage":null,"limits":[{"group":"session"}],"spend":null}
            """;
        var handler = new StubHttpMessageHandler(request => JsonResponse(
            request.RequestUri!.AbsolutePath.EndsWith("usage", StringComparison.Ordinal)
                ? usage
                : "{\"organization\":{}}"));

        ProviderSnapshot snapshot = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(HealthState.Ok, snapshot.Health);
        Assert.Null(snapshot.PlanLabel);
        UsageWindow window = Assert.Single(snapshot.Windows);
        Assert.Equal("session", window.Label);
        Assert.Null(window.Percent);
        Assert.Null(window.ResetsAt);
        Assert.Equal(Severity.Normal, window.Severity);
        Assert.Empty(snapshot.Info);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task FetchAsync_UnauthorizedResponseReturnsAuthExpired(HttpStatusCode statusCode)
    {
        // Catches a provider that reports an expired or rejected token as a generic transport failure.
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode));

        ProviderSnapshot snapshot = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(HealthState.AuthExpired, snapshot.Health);
        Assert.Equal("re-auth: run claude login", snapshot.Error);
    }

    [Fact]
    public async Task FetchAsync_CachesProfileForOneHour()
    {
        // Catches a provider that loads the immutable plan profile on every usage poll.
        int profileRequests = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("profile", StringComparison.Ordinal))
            {
                profileRequests++;
                return JsonResponse(ReadFixture("claude-profile.json"));
            }

            return JsonResponse(ReadFixture("claude-usage.json"));
        });
        ClaudeProvider provider = CreateProvider(handler);

        ProviderSnapshot first = await provider.FetchAsync(CancellationToken.None);
        ProviderSnapshot second = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal("team_standard", first.PlanLabel);
        Assert.Equal("team_standard", second.PlanLabel);
        Assert.Equal(1, profileRequests);
    }

    [Fact]
    public async Task FetchAsync_RequiresJsonContentType()
    {
        // Catches a provider that attempts to parse an HTML success page as a usage response.
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>not JSON</html>", Encoding.UTF8, "text/html")
        });

        ProviderSnapshot snapshot = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(HealthState.Degraded, snapshot.Health);
        Assert.DoesNotContain("unit-test-access-token", snapshot.Error ?? string.Empty, StringComparison.Ordinal);
    }

    private ClaudeProvider CreateProviderWithFixtures(Func<double?, Severity>? severityFromPercent = null) =>
        CreateProvider(new StubHttpMessageHandler(request => JsonResponse(
            request.RequestUri!.AbsolutePath.EndsWith("usage", StringComparison.Ordinal)
                ? ReadFixture("claude-usage.json")
                : ReadFixture("claude-profile.json"))), severityFromPercent: severityFromPercent);

    private ClaudeProvider CreateProvider(
        HttpMessageHandler handler,
        long? expiresAtUnixMilliseconds = null,
        Func<double?, Severity>? severityFromPercent = null)
    {
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        string credentialPath = directory.WriteFile(
            "credentials.json",
            JsonSerializer.Serialize(new
            {
                claudeAiOauth = new
                {
                    accessToken = "unit-test-access-token",
                    expiresAt = expiresAtUnixMilliseconds ?? DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds()
                }
            }));

        return new ClaudeProvider(
            credentialPath,
            handler,
            severityFromPercent ?? (percent => SeverityPolicy.FromPercent(percent, 80, 95)));
    }

    private ClaudeProvider CreateProviderWithCredential(HttpMessageHandler handler, string credentialJson)
    {
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        return new ClaudeProvider(
            directory.WriteFile("credentials.json", credentialJson),
            handler,
            percent => SeverityPolicy.FromPercent(percent, 80, 95));
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
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
}
