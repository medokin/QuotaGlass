using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace QuotaGlass.Providers;

internal static class ProviderHttpSafety
{
    internal const int MaximumJsonBytes = 1_048_576;
    internal static readonly TimeSpan RetryAfterFallback = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromHours(1);

    public static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!IsJsonMediaType(mediaType))
        {
            throw new InvalidDataException("Provider response content type was not JSON.");
        }

        await using Stream source = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var bounded = new MemoryStream();
        byte[] buffer = new byte[81920];
        int total = 0;

        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total = checked(total + read);
            if (total > MaximumJsonBytes)
            {
                throw new InvalidDataException("Provider JSON response exceeded the size limit.");
            }

            await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        bounded.Position = 0;
        try
        {
            return await JsonDocument.ParseAsync(bounded, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Provider response contained invalid JSON.");
        }
    }

    public static TimeSpan? GetRetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.StatusCode is not HttpStatusCode.TooManyRequests and not HttpStatusCode.ServiceUnavailable)
        {
            return null;
        }

        if (!response.Headers.TryGetValues("Retry-After", out IEnumerable<string>? values))
        {
            return RetryAfterFallback;
        }

        string value = values.FirstOrDefault() ?? string.Empty;
        TimeSpan parsed;
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds))
        {
            if (seconds <= 0)
            {
                return RetryAfterFallback;
            }

            parsed = seconds > (long)MaximumRetryAfter.TotalSeconds
                ? MaximumRetryAfter
                : TimeSpan.FromSeconds(seconds);
        }
        else if (DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset retryAt))
        {
            parsed = retryAt - now;
            if (parsed <= TimeSpan.Zero)
            {
                return RetryAfterFallback;
            }
        }
        else
        {
            return RetryAfterFallback;
        }

        return parsed > MaximumRetryAfter ? MaximumRetryAfter : parsed;
    }

    private static bool IsJsonMediaType(string? mediaType)
    {
        if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return mediaType is not null &&
            mediaType.StartsWith("application/", StringComparison.OrdinalIgnoreCase) &&
            mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }
}
