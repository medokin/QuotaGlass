using System.Net;
using System.Text;
using System.Text.Json;
using AiStatus.Core;
using AiStatus.Model;
using AiStatus.Providers;
using AiStatus.Tests.Support;

namespace AiStatus.Tests.Providers;

public sealed class ProviderPollerIntegrationTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    [Fact]
    public async Task ClaudeOperationalFailuresRetainLastGoodDataUntilRecovery()
    {
        // Break caught: Claude converts transport failures into valid empty snapshots that reset poller failures.
        int request = 0;
        var handler = new StubHttpMessageHandler(message => Interlocked.Increment(ref request) switch
        {
            1 => JsonResponse(ReadFixture("claude-usage.json")),
            2 => JsonResponse(ReadFixture("claude-profile.json")),
            3 or 4 or 5 => throw new HttpRequestException("synthetic transport failure"),
            6 => JsonResponse(ReadFixture("claude-usage.json")),
            _ => throw new InvalidOperationException("Unexpected HTTP request."),
        });
        string credentialPath = _directory.WriteFile(
            "claude-credentials.json",
            JsonSerializer.Serialize(new
            {
                claudeAiOauth = new
                {
                    accessToken = "unit-test-access-token",
                    expiresAt = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds(),
                },
            }));
        var provider = new ClaudeProvider(credentialPath, handler, SeverityFromPercent);
        StatusPoller poller = CreatePoller(provider);

        ProviderSnapshot good = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot first = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot second = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot third = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot recovered = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);

        AssertRetained(good, first, HealthState.Ok, 1);
        AssertRetained(good, second, HealthState.Ok, 2);
        AssertRetained(good, third, HealthState.Degraded, 3);
        Assert.Equal(good.PlanLabel, recovered.PlanLabel);
        Assert.True(good.Windows.SequenceEqual(recovered.Windows));
        Assert.True(good.Info.SequenceEqual(recovered.Info));
        Assert.Equal(HealthState.Ok, recovered.Health);
        Assert.Equal(0, recovered.ConsecutiveFailures);
    }

    [Fact]
    public async Task CodexOperationalFailuresRetainLastGoodDataUntilRecovery()
    {
        // Break caught: Codex converts transport failures into valid empty snapshots that reset poller failures.
        int request = 0;
        var handler = new StubHttpMessageHandler(_ => Interlocked.Increment(ref request) switch
        {
            1 => JsonResponse(ReadFixture("codex-wham.json")),
            2 or 3 or 4 => throw new HttpRequestException("synthetic transport failure"),
            5 => JsonResponse(ReadFixture("codex-wham.json")),
            _ => throw new InvalidOperationException("Unexpected HTTP request."),
        });
        string credentialPath = _directory.WriteFile(
            "codex-auth.json",
            """
            {"tokens":{"access_token":"unit-test-access-token","account_id":"unit-test-account-id"}}
            """);
        var provider = new CodexProvider(credentialPath, handler, SeverityFromPercent);
        StatusPoller poller = CreatePoller(provider);

        ProviderSnapshot good = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot first = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot second = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot third = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot recovered = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);

        AssertRetained(good, first, HealthState.Ok, 1);
        AssertRetained(good, second, HealthState.Ok, 2);
        AssertRetained(good, third, HealthState.Degraded, 3);
        Assert.Equal(good.PlanLabel, recovered.PlanLabel);
        Assert.True(good.Windows.SequenceEqual(recovered.Windows));
        Assert.True(good.Info.SequenceEqual(recovered.Info));
        Assert.Equal(HealthState.Ok, recovered.Health);
        Assert.Equal(0, recovered.ConsecutiveFailures);
    }

    [Fact]
    public async Task TransientIncompleteClaudeCredentialUsesPollerRetentionBoundary()
    {
        // Break caught: a credential rotation window publishes an empty snapshot instead of retaining last-good data.
        var handler = new StubHttpMessageHandler(message => JsonResponse(
            message.RequestUri!.AbsolutePath.EndsWith("profile", StringComparison.Ordinal)
                ? ReadFixture("claude-profile.json")
                : ReadFixture("claude-usage.json")));
        string credentialPath = _directory.WriteFile(
            "rotating-claude-credentials.json",
            CreateClaudeCredential());
        var provider = new ClaudeProvider(credentialPath, handler, SeverityFromPercent);
        StatusPoller poller = CreatePoller(provider);
        ProviderSnapshot good = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);

        await File.WriteAllTextAsync(credentialPath, "{\"claudeAiOauth\":{");
        ProviderSnapshot retained = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        await File.WriteAllTextAsync(credentialPath, CreateClaudeCredential());
        ProviderSnapshot recovered = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);

        AssertRetained(good, retained, HealthState.Ok, 1);
        Assert.True(good.Windows.SequenceEqual(recovered.Windows));
        Assert.True(good.Info.SequenceEqual(recovered.Info));
        Assert.Equal(0, recovered.ConsecutiveFailures);
    }

    public void Dispose() => _directory.Dispose();

    private StatusPoller CreatePoller(IStatusProvider provider) => new(
        [provider],
        () => AppSettings.Default,
        new RollingFileLog(Path.Combine(_directory.Path, $"poller-{Guid.NewGuid():N}.log")));

    private static Severity SeverityFromPercent(double? percent) =>
        SeverityPolicy.FromPercent(percent, 80, 95);

    private static void AssertRetained(
        ProviderSnapshot expected,
        ProviderSnapshot actual,
        HealthState health,
        int failures)
    {
        Assert.Equal(expected.PlanLabel, actual.PlanLabel);
        Assert.Equal(expected.Windows, actual.Windows);
        Assert.Equal(expected.Info, actual.Info);
        Assert.Equal(expected.FetchedAt, actual.FetchedAt);
        Assert.Equal(health, actual.Health);
        Assert.Equal(failures, actual.ConsecutiveFailures);
    }

    private static string CreateClaudeCredential() => JsonSerializer.Serialize(new
    {
        claudeAiOauth = new
        {
            accessToken = "unit-test-access-token",
            expiresAt = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds(),
        },
    });

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
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
}
