using QuotaGlass.Model;

namespace QuotaGlass.Providers;

public interface IStatusProvider
{
    string Id { get; }

    string Label { get; }

    Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken);
}
