using System.Net;
using QuotaGlass.Providers;
using QuotaGlass.Tests.Support;

namespace QuotaGlass.Tests.Providers;

public sealed class ProviderFetchResultTests
{
    [Theory]
    [InlineData(ProviderFetchOutcome.Success)]
    [InlineData(ProviderFetchOutcome.PartialSuccess)]
    [InlineData(ProviderFetchOutcome.NotConfigured)]
    [InlineData(ProviderFetchOutcome.AuthenticationRequired)]
    public void Constructor_PublishableOutcomeRequiresSnapshot(ProviderFetchOutcome outcome)
    {
        Assert.Throws<ArgumentException>(() => new ProviderFetchResult(outcome));
    }

    [Theory]
    [InlineData(ProviderFetchOutcome.TransientFailure)]
    [InlineData(ProviderFetchOutcome.RateLimited)]
    [InlineData(ProviderFetchOutcome.InvalidResponse)]
    public void Constructor_FailureOutcomeRejectsSnapshot(ProviderFetchOutcome outcome)
    {
        Assert.Throws<ArgumentException>(() => new ProviderFetchResult(
            outcome,
            FakeStatusProvider.Snapshot("codex")));
    }

    [Fact]
    public void Constructor_RateLimitedRequiresRetryAfter()
    {
        Assert.Throws<ArgumentException>(() => new ProviderFetchResult(
            ProviderFetchOutcome.RateLimited,
            statusCode: HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public void Constructor_NonRateLimitedRejectsRetryAfter()
    {
        Assert.Throws<ArgumentException>(() => new ProviderFetchResult(
            ProviderFetchOutcome.TransientFailure,
            retryAfter: TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Constructor_RateLimitedRejectsCooldownAboveOneHour()
    {
        // Break caught: an out-of-contract cooldown later fails diagnostics and aborts the poll transition.
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderFetchResult(
            ProviderFetchOutcome.RateLimited,
            statusCode: HttpStatusCode.TooManyRequests,
            retryAfter: TimeSpan.FromHours(2)));
    }
}
