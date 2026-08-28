using ReservePane.Model;

namespace ReservePane.Providers;

public interface IStatusProvider
{
    string Id { get; }

    string Label { get; }

    Task<ProviderFetchResult> FetchAsync(CancellationToken cancellationToken);
}
