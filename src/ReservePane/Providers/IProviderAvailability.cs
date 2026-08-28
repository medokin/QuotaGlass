namespace QuotaGlass.Providers;

public interface IProviderAvailability
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);
}
