using System.Net;
using QuotaGlass.Model;

namespace QuotaGlass.Providers;

public enum ProviderFetchOutcome
{
    Success,
    PartialSuccess,
    NotConfigured,
    AuthenticationRequired,
    TransientFailure,
    RateLimited,
    InvalidResponse,
}

public sealed record ProviderFetchResult
{
    public ProviderFetchResult(
        ProviderFetchOutcome outcome,
        ProviderSnapshot? snapshot = null,
        HttpStatusCode? statusCode = null,
        TimeSpan? retryAfter = null)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        bool publishesSnapshot = outcome is
            ProviderFetchOutcome.Success or
            ProviderFetchOutcome.PartialSuccess or
            ProviderFetchOutcome.NotConfigured or
            ProviderFetchOutcome.AuthenticationRequired;
        if (publishesSnapshot != (snapshot is not null))
        {
            throw new ArgumentException(
                publishesSnapshot
                    ? "The fetch outcome requires a snapshot."
                    : "The fetch outcome cannot publish a snapshot.",
                nameof(snapshot));
        }

        if (outcome == ProviderFetchOutcome.RateLimited)
        {
            if (retryAfter is null || retryAfter <= TimeSpan.Zero)
            {
                throw new ArgumentException("A rate-limited result requires a positive retry delay.", nameof(retryAfter));
            }

            if (retryAfter > TimeSpan.FromHours(1))
            {
                throw new ArgumentOutOfRangeException(nameof(retryAfter), "A retry delay cannot exceed one hour.");
            }
        }
        else if (retryAfter is not null)
        {
            throw new ArgumentException("Only a rate-limited result can carry a retry delay.", nameof(retryAfter));
        }

        Outcome = outcome;
        Snapshot = snapshot;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public ProviderFetchOutcome Outcome { get; }

    public ProviderSnapshot? Snapshot { get; }

    public HttpStatusCode? StatusCode { get; }

    public TimeSpan? RetryAfter { get; }
}
