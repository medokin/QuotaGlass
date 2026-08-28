using System.Collections.Immutable;

namespace ReservePane.Model;

public sealed record StatusReport(
    DateTimeOffset FetchedAt,
    ImmutableArray<ProviderSnapshot> Providers)
{
    public static StatusReport Empty(DateTimeOffset now) =>
        new(now, ImmutableArray<ProviderSnapshot>.Empty);
}
