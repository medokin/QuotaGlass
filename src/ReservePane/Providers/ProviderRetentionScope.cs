namespace QuotaGlass.Providers;

internal readonly record struct ProviderRetentionScope(bool IsKnown, string? Key)
{
    public static ProviderRetentionScope Unknown => new(false, null);

    public static ProviderRetentionScope Known(string? key) => new(true, key);
}

internal enum ProviderRetentionScopeRefreshOutcome
{
    Success,
    TransientFailure,
    InvalidResponse,
}

internal interface IRetentionScopedStatusProvider
{
    ProviderRetentionScope RetentionScope { get; }

    Task<ProviderRetentionScopeRefreshOutcome> RefreshRetentionScopeAsync(
        CancellationToken cancellationToken);
}
