using ReservePane.Model;
using ReservePane.Providers;

namespace ReservePane.Tests.Support;

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
