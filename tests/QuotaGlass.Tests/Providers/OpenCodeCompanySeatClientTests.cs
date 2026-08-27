using System.Net;
using System.Numerics;
using System.Text;
using QuotaGlass.Model;
using QuotaGlass.Providers;
using QuotaGlass.Tests.Support;

namespace QuotaGlass.Tests.Providers;

public sealed class OpenCodeCompanySeatClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly OpenCodeConsoleActiveWorkspace Workspace = new(
        "account-active",
        "access-active",
        "org-active",
        Now.AddHours(1));

    [Fact]
    public async Task FetchAsync_ResolvesCurrentMemberAndReadsEffectiveBudgetWithFixedAuthentication()
    {
        // Catches the provider querying an organization total, omitting x-org-id, or using another token.
        var requests = new List<(string Uri, string? Scheme, string? Token, string? Organization)>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add((
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                request.Headers.TryGetValues("x-org-id", out IEnumerable<string>? values)
                    ? Assert.Single(values)
                    : null));
            return request.RequestUri.AbsolutePath.EndsWith("/orgs/current", StringComparison.Ordinal)
                ? JsonResponse("{\"userId\":\"member-current\"}")
                : JsonResponse(
                    """
                    {"scope":"user","userId":"member-current","limitMicroCents":"1000000000","spentMicroCents":"250000000","exceeded":false,"resetsAt":"2026-09-27T00:00:00Z","source":"default"}
                    """);
        });
        var client = new OpenCodeCompanySeatClient(handler, new FixedTimeProvider(Now));

        OpenCodeCompanySeatFetchResult result = await client.FetchAsync(
            Workspace,
            CancellationToken.None);

        Assert.Equal(OpenCodeCompanySeatFetchOutcome.Success, result.Outcome);
        OpenCodeCompanySeatBudget budget = Assert.IsType<OpenCodeCompanySeatBudget>(result.Budget);
        Assert.Equal(new BigInteger(1_000_000_000), budget.LimitMicroCents);
        Assert.Equal(new BigInteger(250_000_000), budget.SpentMicroCents);
        Assert.False(budget.Exceeded);
        Assert.Equal(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero), budget.ResetsAt);
        Assert.Equal("default", budget.Source);
        Assert.Collection(
            requests,
            request => Assert.Equal(
                "https://opencode.ai/console/api/orgs/current",
                request.Uri),
            request => Assert.Equal(
                "https://opencode.ai/console/api/budgets/users/member-current",
                request.Uri));
        Assert.All(requests, request =>
        {
            Assert.Equal("Bearer", request.Scheme);
            Assert.Equal("access-active", request.Token);
            Assert.Equal("org-active", request.Organization);
        });
    }

    [Fact]
    public async Task FetchAsync_MissingOptionalResetDoesNotInventALocalCalendarBoundary()
    {
        // Catches a missing server reset being replaced by a locally inferred month boundary.
        int request = 0;
        var client = new OpenCodeCompanySeatClient(
            new StubHttpMessageHandler(_ => Interlocked.Increment(ref request) == 1
                ? JsonResponse("{\"userId\":\"member-current\"}")
                : JsonResponse(
                    """
                    {"scope":"user","userId":"member-current","limitMicroCents":"100000000","spentMicroCents":"25000000","exceeded":false,"source":"custom"}
                    """)),
            new FixedTimeProvider(Now));

        OpenCodeCompanySeatFetchResult result = await client.FetchAsync(
            Workspace,
            CancellationToken.None);

        Assert.Equal(OpenCodeCompanySeatFetchOutcome.Success, result.Outcome);
        Assert.Null(Assert.IsType<OpenCodeCompanySeatBudget>(result.Budget).ResetsAt);
    }

    [Theory]
    [InlineData("{\"scope\":\"org\",\"userId\":\"member-current\",\"spentMicroCents\":\"1\",\"exceeded\":false,\"resetsAt\":\"2026-09-27T00:00:00Z\"}")]
    [InlineData("{\"scope\":\"user\",\"userId\":\"member-other\",\"spentMicroCents\":\"1\",\"exceeded\":false,\"resetsAt\":\"2026-09-27T00:00:00Z\"}")]
    [InlineData("{\"userId\":\"member-current\",\"spentMicroCents\":\"1\",\"exceeded\":false,\"resetsAt\":\"2026-09-27T00:00:00Z\"}")]
    public async Task FetchAsync_NonMemberBudgetShapeIsRejected(string budgetJson)
    {
        // Catches organization totals or another member's values being presented as the active member budget.
        int request = 0;
        var client = new OpenCodeCompanySeatClient(
            new StubHttpMessageHandler(_ => Interlocked.Increment(ref request) == 1
                ? JsonResponse("{\"userId\":\"member-current\"}")
                : JsonResponse(budgetJson)),
            new FixedTimeProvider(Now));

        OpenCodeCompanySeatFetchResult result = await client.FetchAsync(
            Workspace,
            CancellationToken.None);

        Assert.Equal(OpenCodeCompanySeatFetchOutcome.InvalidResponse, result.Outcome);
        Assert.Null(result.Budget);
    }

    [Fact]
    public async Task FetchAsync_NullBudgetIsAValidUnconfiguredResult()
    {
        // Catches the private endpoint's 200 null sentinel being treated as malformed JSON.
        OpenCodeCompanySeatClient client = ClientWithBudget("null");

        OpenCodeCompanySeatFetchResult result = await client.FetchAsync(
            Workspace,
            CancellationToken.None);

        Assert.Equal(OpenCodeCompanySeatFetchOutcome.Success, result.Outcome);
        Assert.Null(result.Budget);
    }

    [Theory]
    [InlineData("custom")]
    [InlineData("default")]
    public async Task FetchAsync_AcceptsKnownBudgetSources(string source)
    {
        // Catches custom and inherited-default member budgets being confused with contract drift.
        OpenCodeCompanySeatClient client = ClientWithBudget(
            $$"""
            {"scope":"user","userId":"member-current","limitMicroCents":null,"spentMicroCents":"0","exceeded":false,"resetsAt":"2026-09-27T00:00:00Z","source":"{{source}}"}
            """);

        OpenCodeCompanySeatFetchResult result = await client.FetchAsync(
            Workspace,
            CancellationToken.None);

        Assert.Equal(OpenCodeCompanySeatFetchOutcome.Success, result.Outcome);
        OpenCodeCompanySeatBudget budget = Assert.IsType<OpenCodeCompanySeatBudget>(result.Budget);
        Assert.Null(budget.LimitMicroCents);
        Assert.Equal(source, budget.Source);
    }

    [Theory]
    [InlineData("{\"scope\":\"user\",\"userId\":\"member-current\",\"limitMicroCents\":\"-1\",\"spentMicroCents\":\"0\",\"exceeded\":false,\"resetsAt\":\"2026-09-27T00:00:00Z\"}")]
    [InlineData("{\"scope\":\"user\",\"userId\":\"member-current\",\"limitMicroCents\":1," +
        "\"spentMicroCents\":\"0\",\"exceeded\":false,\"resetsAt\":\"2026-09-27T00:00:00Z\"}")]
    [InlineData("{\"scope\":\"user\",\"userId\":\"member-current\",\"spentMicroCents\":\"-1\",\"exceeded\":false,\"resetsAt\":\"2026-09-27T00:00:00Z\"}")]
    [InlineData("{\"scope\":\"user\",\"userId\":\"member-current\",\"spentMicroCents\":\"0\",\"resetsAt\":\"2026-09-27T00:00:00Z\"}")]
    [InlineData("{\"scope\":\"user\",\"userId\":\"member-current\",\"spentMicroCents\":\"0\",\"exceeded\":false,\"resetsAt\":\"not-a-time\"}")]
    [InlineData("{\"scope\":\"user\",\"userId\":\"member-current\",\"spentMicroCents\":\"0\",\"exceeded\":false,\"resetsAt\":\"2026-08-27T11:59:59Z\"}")]
    [InlineData("{\"scope\":\"user\",\"userId\":\"member-current\",\"spentMicroCents\":\"0\",\"exceeded\":false,\"resetsAt\":\"2026-09-27T00:00:00Z\",\"source\":\"organization\"}")]
    public async Task FetchAsync_InvalidBudgetFieldsReturnSanitizedInvalidResponse(string budgetJson)
    {
        // Catches negative money, schema changes, and stale periods replacing last-good data.
        OpenCodeCompanySeatClient client = ClientWithBudget(budgetJson);

        OpenCodeCompanySeatFetchResult result = await client.FetchAsync(
            Workspace,
            CancellationToken.None);

        Assert.Equal(OpenCodeCompanySeatFetchOutcome.InvalidResponse, result.Outcome);
        Assert.Null(result.Budget);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "AuthenticationRequired")]
    [InlineData(HttpStatusCode.Forbidden, "AuthenticationRequired")]
    [InlineData(HttpStatusCode.NotFound, "TransientFailure")]
    [InlineData(HttpStatusCode.InternalServerError, "TransientFailure")]
    [InlineData(HttpStatusCode.Redirect, "TransientFailure")]
    public async Task FetchAsync_RelevantHttpStatusIsSanitized(
        HttpStatusCode statusCode,
        string expected)
    {
        // Catches authentication, route removal, server failure, or redirects escaping as raw content.
        var client = new OpenCodeCompanySeatClient(
            new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode)),
            new FixedTimeProvider(Now));

        OpenCodeCompanySeatFetchResult result = await client.FetchAsync(
            Workspace,
            CancellationToken.None);

        Assert.Equal(expected, result.Outcome.ToString());
        Assert.Equal(statusCode, result.StatusCode);
        Assert.Null(result.Budget);
    }

    [Fact]
    public async Task FetchAsync_RateLimitReturnsBoundedRetryDelay()
    {
        // Catches a 429 bypassing the poller's shared cooldown behavior.
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", "120");
        var client = new OpenCodeCompanySeatClient(
            new StubHttpMessageHandler(_ => response),
            new FixedTimeProvider(Now));

        OpenCodeCompanySeatFetchResult result = await client.FetchAsync(
            Workspace,
            CancellationToken.None);

        Assert.Equal(OpenCodeCompanySeatFetchOutcome.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(2), result.RetryAfter);
    }

    [Fact]
    public async Task FetchAsync_OversizedResponseIsRejected()
    {
        // Catches a private endpoint buffering an unbounded response into the tray process.
        var oversized = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                new string(' ', ProviderHttpSafety.MaximumJsonBytes + 1),
                Encoding.UTF8,
                "application/json"),
        };
        var client = new OpenCodeCompanySeatClient(
            new StubHttpMessageHandler(_ => oversized),
            new FixedTimeProvider(Now));

        OpenCodeCompanySeatFetchResult result = await client.FetchAsync(
            Workspace,
            CancellationToken.None);

        Assert.Equal(OpenCodeCompanySeatFetchOutcome.InvalidResponse, result.Outcome);
    }

    [Fact]
    public async Task FetchAsync_CancellationStopsTheActiveRequest()
    {
        // Catches OpenCode requests surviving provider cancellation or application shutdown.
        var client = new OpenCodeCompanySeatClient(
            new BlockingHandler(),
            new FixedTimeProvider(Now));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.FetchAsync(Workspace, cancellation.Token));
    }

    private static OpenCodeCompanySeatClient ClientWithBudget(string budgetJson)
    {
        int request = 0;
        return new OpenCodeCompanySeatClient(
            new StubHttpMessageHandler(_ => Interlocked.Increment(ref request) == 1
                ? JsonResponse("{\"userId\":\"member-current\"}")
                : JsonResponse(budgetJson)),
            new FixedTimeProvider(Now));
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocking request unexpectedly completed.");
        }
    }
}
