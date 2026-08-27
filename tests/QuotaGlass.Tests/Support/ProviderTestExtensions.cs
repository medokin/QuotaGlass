using QuotaGlass.Model;
using QuotaGlass.Providers;

namespace QuotaGlass.Tests.Support;

internal static class ProviderTestExtensions
{
    public static async Task<ProviderSnapshot> FetchSnapshotAsync(
        this IStatusProvider provider,
        CancellationToken cancellationToken)
    {
        ProviderFetchResult result = await provider.FetchAsync(cancellationToken);
        return Assert.IsType<ProviderSnapshot>(result.Snapshot);
    }
}
