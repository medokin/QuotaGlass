using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ReservePane.Model;
using ReservePane.Providers;
using ReservePane.Tests.Support;

namespace ReservePane.Tests.Providers;

public sealed class OpenCodeConsoleGoClientTests
{
    private static readonly OpenCodeConsoleAccount Account = new(
        "account-test",
        "access-test",
        DateTimeOffset.Parse("2026-08-28T00:00:00Z"));

    [Fact]
    public async Task FetchAsync_UsesFixedEndpointsAndMapsGoMeters()
    {
        // Catches token routing, selector stability, or private-contract mapping regressions.
        var requests = new List<(Uri? Uri, string? Bearer, string? OrgId)>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add((
                request.RequestUri,
                request.Headers.Authorization?.Parameter,
                request.Headers.TryGetValues("x-org-id", out IEnumerable<string>? values)
                    ? Assert.Single(values)
                    : null));
            return request.RequestUri?.AbsolutePath.EndsWith("/orgs", StringComparison.Ordinal) == true
                ? JsonResponse("[{\"id\":\"org-test\"}]")
                : JsonResponse(ValidStatus);
        });
        var client = new OpenCodeConsoleGoClient(handler, SeverityFromPercent);

        OpenCodeConsoleFetchResult result = await client.FetchAsync([Account], CancellationToken.None);

        Assert.Equal(OpenCodeConsoleFetchOutcome.Success, result.Outcome);
        OpenCodeConsoleWorkspace workspace = Assert.Single(result.Workspaces);
        Assert.Equal(Selector("account-test", "org-test"), workspace.Selector);
        Assert.Collection(
            workspace.Windows,
            rolling => AssertWindow(rolling, "rolling", 25, "2026-08-27T13:00:00Z"),
            weekly => AssertWindow(weekly, "weekly", 50, "2026-09-01T00:00:00Z"),
            monthly => AssertWindow(monthly, "monthly", 75, "2026-09-27T00:00:00Z"));
        Assert.Collection(
            requests,
            request =>
            {
                Assert.Equal("https://opencode.ai/console/api/orgs", request.Uri?.ToString());
                Assert.Equal("access-test", request.Bearer);
                Assert.Null(request.OrgId);
            },
            request =>
            {
                Assert.Equal("https://opencode.ai/console/api/go/status", request.Uri?.ToString());
                Assert.Equal("access-test", request.Bearer);
                Assert.Equal("org-test", request.OrgId);
            });
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"access\":null}")]
    public async Task FetchAsync_NonGoWorkspaceIsSuccessfulButIneligible(string status)
    {
        var handler = Handler(JsonResponse(status));
        var client = new OpenCodeConsoleGoClient(handler, SeverityFromPercent);

        OpenCodeConsoleFetchResult result = await client.FetchAsync([Account], CancellationToken.None);

        Assert.Equal(OpenCodeConsoleFetchOutcome.Success, result.Outcome);
        Assert.Empty(result.Workspaces);
    }

    [Fact]
    public async Task FetchAsync_NullFiveHourResetKeepsAvailableWindow()
    {
        // Catches the nullable upstream rolling reset invalidating an otherwise active Go subscription.
        string status = ValidStatus.Replace(
            "\"resetsAt\": \"2026-08-27T13:00:00Z\"",
            "\"resetsAt\": null",
            StringComparison.Ordinal);
        var client = new OpenCodeConsoleGoClient(Handler(JsonResponse(status)), SeverityFromPercent);

        OpenCodeConsoleFetchResult result = await client.FetchAsync([Account], CancellationToken.None);

        OpenCodeConsoleWorkspace workspace = Assert.Single(result.Workspaces);
        UsageWindow rolling = Assert.Single(workspace.Windows, window => window.Label == "rolling");
        Assert.Null(rolling.ResetsAt);
    }

    [Fact]
    public async Task FetchAsync_ComputesPercentageBeforeFloatingPointConversion()
    {
        // Catches arbitrarily large micro-cent counters becoming infinity divided by infinity.
        string used = "1" + new string('0', 400);
        string limit = "2" + new string('0', 400);
        string status = ValidStatus
            .Replace("\"limitMicroCents\": \"400\"", $"\"limitMicroCents\": \"{limit}\"", StringComparison.Ordinal)
            .Replace("\"usedMicroCents\": \"100\"", $"\"usedMicroCents\": \"{used}\"", StringComparison.Ordinal);
        var client = new OpenCodeConsoleGoClient(Handler(JsonResponse(status)), SeverityFromPercent);

        OpenCodeConsoleFetchResult result = await client.FetchAsync([Account], CancellationToken.None);

        UsageWindow rolling = Assert.Single(
            Assert.Single(result.Workspaces).Windows,
            window => window.Label == "rolling");
        Assert.Equal(50, rolling.Percent);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task FetchAsync_RejectedConsoleTokenReturnsAuthenticationRequired(HttpStatusCode statusCode)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("sensitive response"),
        });
        var client = new OpenCodeConsoleGoClient(handler, SeverityFromPercent);

        OpenCodeConsoleFetchResult result = await client.FetchAsync([Account], CancellationToken.None);

        Assert.Equal(OpenCodeConsoleFetchOutcome.AuthenticationRequired, result.Outcome);
        Assert.Equal(statusCode, result.StatusCode);
        Assert.Empty(result.Workspaces);
    }

    [Fact]
    public async Task FetchAsync_StaleAccountDoesNotHideValidAccount()
    {
        // Catches one revoked account aborting discovery before another account is checked.
        var stale = new OpenCodeConsoleAccount("account-stale", "access-stale", null);
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.Headers.Authorization?.Parameter == "access-stale")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            return request.RequestUri?.AbsolutePath.EndsWith("/orgs", StringComparison.Ordinal) == true
                ? JsonResponse("[{\"id\":\"org-test\"}]")
                : JsonResponse(ValidStatus);
        });
        var client = new OpenCodeConsoleGoClient(handler, SeverityFromPercent);

        OpenCodeConsoleFetchResult result = await client.FetchAsync(
            [stale, Account],
            CancellationToken.None);

        Assert.Equal(OpenCodeConsoleFetchOutcome.Success, result.Outcome);
        Assert.Single(result.Workspaces);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_TransientAccountFailureBlocksAutomaticSelection()
    {
        // Catches incomplete discovery being mistaken for exactly one eligible workspace.
        var unavailable = new OpenCodeConsoleAccount("account-unavailable", "access-unavailable", null);
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.Headers.Authorization?.Parameter == "access-unavailable")
            {
                return new HttpResponseMessage(HttpStatusCode.BadGateway);
            }

            return request.RequestUri?.AbsolutePath.EndsWith("/orgs", StringComparison.Ordinal) == true
                ? JsonResponse("[{\"id\":\"org-test\"}]")
                : JsonResponse(ValidStatus);
        });
        var client = new OpenCodeConsoleGoClient(handler, SeverityFromPercent);

        OpenCodeConsoleFetchResult result = await client.FetchAsync(
            [unavailable, Account],
            CancellationToken.None);

        Assert.Equal(OpenCodeConsoleFetchOutcome.TransientFailure, result.Outcome);
        Assert.Equal(HttpStatusCode.BadGateway, result.StatusCode);
        Assert.Empty(result.Workspaces);
    }

    [Fact]
    public async Task FetchAsync_BoundsConcurrentDiscoveryRequests()
    {
        // Catches valid multi-account discovery becoming fully serial or unbounded.
        var handler = new ConcurrencyHandler();
        var accounts = Enumerable.Range(0, 8)
            .Select(index => new OpenCodeConsoleAccount($"account-{index}", $"access-{index}", null))
            .ToImmutableArray();
        var client = new OpenCodeConsoleGoClient(handler, SeverityFromPercent);

        OpenCodeConsoleFetchResult result = await client.FetchAsync(accounts, CancellationToken.None);

        Assert.Equal(OpenCodeConsoleFetchOutcome.Success, result.Outcome);
        Assert.Empty(result.Workspaces);
        Assert.InRange(handler.MaximumConcurrentRequests, 2, 4);
    }

    [Fact]
    public async Task FetchAsync_ConfiguredSelectorSkipsUnselectedStatusRequests()
    {
        // Catches explicit selection still probing every organization on every poll.
        string selected = Selector(Account.AccountId, "org-selected");
        string? requestedOrganization = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/orgs", StringComparison.Ordinal) == true)
            {
                return JsonResponse("[{\"id\":\"org-other\"},{\"id\":\"org-selected\"}]");
            }

            requestedOrganization = Assert.Single(request.Headers.GetValues("x-org-id"));
            return JsonResponse(ValidStatus);
        });
        var client = new OpenCodeConsoleGoClient(handler, SeverityFromPercent);

        OpenCodeConsoleFetchResult result = await client.FetchAsync(
            [Account],
            CancellationToken.None,
            selected);

        Assert.Single(result.Workspaces);
        Assert.Equal("org-selected", requestedOrganization);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_MissingPrivateRouteReturnsTransientFailure()
    {
        var client = new OpenCodeConsoleGoClient(
            Handler(new HttpResponseMessage(HttpStatusCode.NotFound)),
            SeverityFromPercent);

        OpenCodeConsoleFetchResult result = await client.FetchAsync([Account], CancellationToken.None);

        Assert.Equal(OpenCodeConsoleFetchOutcome.TransientFailure, result.Outcome);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
    }

    [Fact]
    public async Task FetchAsync_RateLimitReturnsBoundedRetryDelay()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", "120");
        var client = new OpenCodeConsoleGoClient(Handler(response), SeverityFromPercent);

        OpenCodeConsoleFetchResult result = await client.FetchAsync([Account], CancellationToken.None);

        Assert.Equal(OpenCodeConsoleFetchOutcome.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(2), result.RetryAfter);
    }

    [Theory]
    [InlineData("{\"access\":")]
    [InlineData("{\"access\":{\"startsAt\":\"2026-08-27T00:00:00Z\",\"endsAt\":\"2026-09-27T00:00:00Z\",\"meters\":{}}}")]
    public async Task FetchAsync_InvalidStatusReturnsSanitizedInvalidResponse(string status)
    {
        var client = new OpenCodeConsoleGoClient(Handler(JsonResponse(status)), SeverityFromPercent);

        OpenCodeConsoleFetchResult result = await client.FetchAsync([Account], CancellationToken.None);

        Assert.Equal(OpenCodeConsoleFetchOutcome.InvalidResponse, result.Outcome);
        Assert.Empty(result.Workspaces);
    }

    [Fact]
    public async Task FetchAsync_RejectsUnboundedOrganizationLists()
    {
        string orgs = "[" + string.Join(
            ',',
            Enumerable.Range(0, 33).Select(index => $"{{\"id\":\"org-{index}\"}}")) + "]";
        var handler = new StubHttpMessageHandler(_ => JsonResponse(orgs));
        var client = new OpenCodeConsoleGoClient(handler, SeverityFromPercent);

        OpenCodeConsoleFetchResult result = await client.FetchAsync([Account], CancellationToken.None);

        Assert.Equal(OpenCodeConsoleFetchOutcome.InvalidResponse, result.Outcome);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_CallerCancellationPropagates()
    {
        var handler = new BlockingHandler();
        var client = new OpenCodeConsoleGoClient(handler, SeverityFromPercent);
        using var cancellation = new CancellationTokenSource();

        Task<OpenCodeConsoleFetchResult> fetch = client.FetchAsync([Account], cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
    }

    private static StubHttpMessageHandler Handler(HttpResponseMessage statusResponse) => new(request =>
        request.RequestUri?.AbsolutePath.EndsWith("/orgs", StringComparison.Ordinal) == true
            ? JsonResponse("[{\"id\":\"org-test\"}]")
            : statusResponse);

    private static void AssertWindow(UsageWindow window, string label, double percent, string resetsAt)
    {
        Assert.Equal(label, window.Label);
        Assert.Equal(percent, window.Percent);
        Assert.Equal(DateTimeOffset.Parse(resetsAt), window.ResetsAt);
    }

    private static string Selector(string accountId, string orgId) => Convert
        .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId + "\0" + orgId)))
        .ToLowerInvariant();

    private static Severity SeverityFromPercent(double? percent) =>
        SeverityPolicy.FromPercent(percent, 80, 95);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private const string ValidStatus =
        """
        {
          "subscriberUserId": "subscriber-test",
          "useBalance": true,
          "access": {
            "startsAt": "2026-08-27T00:00:00Z",
            "endsAt": "2026-09-27T00:00:00Z",
            "cancelAtPeriodEnd": false,
            "meters": {
              "fiveHour": {
                "startsAt": "2026-08-27T08:00:00Z",
                "resetsAt": "2026-08-27T13:00:00Z",
                "limitMicroCents": "400",
                "usedMicroCents": "100"
              },
              "week": {
                "startsAt": "2026-08-25T00:00:00Z",
                "resetsAt": "2026-09-01T00:00:00Z",
                "limitMicroCents": "1000",
                "usedMicroCents": "500"
              },
              "month": {
                "limitMicroCents": "2000",
                "usedMicroCents": "1500"
              }
            }
          },
          "renewalPaymentAttemptId": null
        }
        """;

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new Xunit.Sdk.XunitException("Unreachable");
        }
    }

    private sealed class ConcurrencyHandler : HttpMessageHandler
    {
        private int _activeRequests;
        private int _maximumConcurrentRequests;

        public int MaximumConcurrentRequests => _maximumConcurrentRequests;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref _activeRequests);
            int observed;
            do
            {
                observed = _maximumConcurrentRequests;
            }
            while (active > observed &&
                Interlocked.CompareExchange(ref _maximumConcurrentRequests, active, observed) != observed);

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
                return JsonResponse("[]");
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
            }
        }
    }
}
