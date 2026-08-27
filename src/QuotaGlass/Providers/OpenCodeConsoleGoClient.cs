using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuotaGlass.Model;

namespace QuotaGlass.Providers;

internal enum OpenCodeConsoleFetchOutcome
{
    Success,
    AuthenticationRequired,
    TransientFailure,
    RateLimited,
    InvalidResponse,
}

internal sealed record OpenCodeConsoleWorkspace(
    string Selector,
    ImmutableArray<UsageWindow> Windows);

internal sealed record OpenCodeConsoleFetchResult(
    OpenCodeConsoleFetchOutcome Outcome,
    ImmutableArray<OpenCodeConsoleWorkspace> Workspaces,
    HttpStatusCode? StatusCode = null,
    TimeSpan? RetryAfter = null);

internal interface IOpenCodeConsoleGoClient
{
    Task<OpenCodeConsoleFetchResult> FetchAsync(
        ImmutableArray<OpenCodeConsoleAccount> accounts,
        CancellationToken cancellationToken);
}

internal sealed class OpenCodeConsoleGoClient : IOpenCodeConsoleGoClient
{
    private const int MaximumOrganizationsPerAccount = 32;
    private static readonly Uri OrganizationsUri = new("https://opencode.ai/console/api/orgs");
    private static readonly Uri GoStatusUri = new("https://opencode.ai/console/api/go/status");

    private readonly HttpMessageHandler _handler;
    private readonly Func<double?, Severity> _severityFromPercent;
    private readonly TimeProvider _timeProvider;

    public OpenCodeConsoleGoClient(
        HttpMessageHandler handler,
        Func<double?, Severity> severityFromPercent,
        TimeProvider? timeProvider = null)
    {
        _handler = handler;
        _severityFromPercent = severityFromPercent;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<OpenCodeConsoleFetchResult> FetchAsync(
        ImmutableArray<OpenCodeConsoleAccount> accounts,
        CancellationToken cancellationToken)
    {
        var workspaces = ImmutableArray.CreateBuilder<OpenCodeConsoleWorkspace>();
        using var client = new HttpClient(_handler, disposeHandler: false);

        foreach (OpenCodeConsoleAccount account in accounts)
        {
            HttpJsonResult organizations = await GetJsonAsync(
                client,
                OrganizationsUri,
                account.AccessToken,
                organizationId: null,
                cancellationToken).ConfigureAwait(false);
            if (organizations.Failure is not null)
            {
                return organizations.Failure;
            }

            using JsonDocument organizationDocument = organizations.Document!;
            if (!TryReadOrganizationIds(
                organizationDocument.RootElement,
                out ImmutableArray<string> organizationIds))
            {
                return InvalidResponse();
            }

            foreach (string organizationId in organizationIds)
            {
                HttpJsonResult status = await GetJsonAsync(
                    client,
                    GoStatusUri,
                    account.AccessToken,
                    organizationId,
                    cancellationToken).ConfigureAwait(false);
                if (status.Failure is not null)
                {
                    return status.Failure;
                }

                using JsonDocument statusDocument = status.Document!;
                if (!TryReadWorkspace(statusDocument.RootElement, out ImmutableArray<UsageWindow> windows))
                {
                    return InvalidResponse();
                }

                if (!windows.IsEmpty)
                {
                    workspaces.Add(new OpenCodeConsoleWorkspace(
                        CreateSelector(account.AccountId, organizationId),
                        windows));
                }
            }
        }

        return new OpenCodeConsoleFetchResult(
            OpenCodeConsoleFetchOutcome.Success,
            workspaces.ToImmutable());
    }

    private async Task<HttpJsonResult> GetJsonAsync(
        HttpClient client,
        Uri uri,
        string accessToken,
        string? organizationId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        if (organizationId is not null)
        {
            request.Headers.TryAddWithoutValidation("x-org-id", organizationId);
        }

        using HttpResponseMessage response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return HttpJsonResult.FromFailure(new OpenCodeConsoleFetchResult(
                OpenCodeConsoleFetchOutcome.AuthenticationRequired,
                [],
                response.StatusCode));
        }

        TimeSpan? retryAfter = ProviderHttpSafety.GetRetryAfter(
            response,
            _timeProvider.GetUtcNow());
        if (retryAfter is not null)
        {
            return HttpJsonResult.FromFailure(new OpenCodeConsoleFetchResult(
                OpenCodeConsoleFetchOutcome.RateLimited,
                [],
                response.StatusCode,
                retryAfter));
        }

        if (!response.IsSuccessStatusCode)
        {
            return HttpJsonResult.FromFailure(new OpenCodeConsoleFetchResult(
                OpenCodeConsoleFetchOutcome.TransientFailure,
                [],
                response.StatusCode));
        }

        try
        {
            JsonDocument document = await ProviderHttpSafety
                .ReadJsonAsync(response, cancellationToken)
                .ConfigureAwait(false);
            return HttpJsonResult.FromDocument(document);
        }
        catch (InvalidDataException)
        {
            return HttpJsonResult.FromFailure(InvalidResponse(response.StatusCode));
        }
    }

    private bool TryReadWorkspace(
        JsonElement root,
        out ImmutableArray<UsageWindow> windows)
    {
        windows = [];
        if (root.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("access", out JsonElement access))
        {
            return false;
        }

        if (access.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (access.ValueKind != JsonValueKind.Object ||
            !TryReadTimestamp(access, "endsAt", out DateTimeOffset monthReset) ||
            !access.TryGetProperty("meters", out JsonElement meters) ||
            meters.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var result = ImmutableArray.CreateBuilder<UsageWindow>(3);
        if (!TryAddWindow(meters, "fiveHour", "rolling", null, result) ||
            !TryAddWindow(meters, "week", "weekly", null, result) ||
            !TryAddWindow(meters, "month", "monthly", monthReset, result))
        {
            return false;
        }

        windows = result.ToImmutable();
        return true;
    }

    private bool TryAddWindow(
        JsonElement meters,
        string propertyName,
        string label,
        DateTimeOffset? fixedReset,
        ImmutableArray<UsageWindow>.Builder windows)
    {
        if (!meters.TryGetProperty(propertyName, out JsonElement meter) ||
            meter.ValueKind != JsonValueKind.Object ||
            !TryReadAmount(meter, "limitMicroCents", out BigInteger limit) ||
            !TryReadAmount(meter, "usedMicroCents", out BigInteger used) ||
            limit <= BigInteger.Zero)
        {
            return false;
        }

        DateTimeOffset reset;
        if (fixedReset is DateTimeOffset value)
        {
            reset = value;
        }
        else if (!TryReadTimestamp(meter, "resetsAt", out reset))
        {
            return false;
        }

        double percent = 100d * (double)used / (double)limit;
        if (!double.IsFinite(percent) || percent < 0)
        {
            return false;
        }

        windows.Add(new UsageWindow(label, percent, reset, _severityFromPercent(percent)));
        return true;
    }

    private static bool TryReadOrganizationIds(
        JsonElement root,
        out ImmutableArray<string> organizationIds)
    {
        organizationIds = [];
        if (root.ValueKind != JsonValueKind.Array ||
            root.GetArrayLength() > MaximumOrganizationsPerAccount)
        {
            return false;
        }

        var result = ImmutableArray.CreateBuilder<string>(root.GetArrayLength());
        foreach (JsonElement organization in root.EnumerateArray())
        {
            if (organization.ValueKind != JsonValueKind.Object ||
                !organization.TryGetProperty("id", out JsonElement id) ||
                id.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(id.GetString()))
            {
                return false;
            }

            result.Add(id.GetString()!);
        }

        organizationIds = result.ToImmutable();
        return true;
    }

    private static bool TryReadTimestamp(
        JsonElement parent,
        string propertyName,
        out DateTimeOffset timestamp)
    {
        timestamp = default;
        return parent.TryGetProperty(propertyName, out JsonElement element) &&
            element.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
            element.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp);
    }

    private static bool TryReadAmount(
        JsonElement parent,
        string propertyName,
        out BigInteger value)
    {
        value = default;
        return parent.TryGetProperty(propertyName, out JsonElement element) &&
            element.ValueKind == JsonValueKind.String &&
            BigInteger.TryParse(
            element.GetString(),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out value) &&
            value >= BigInteger.Zero;
    }

    private static string CreateSelector(string accountId, string organizationId) => Convert
        .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId + "\0" + organizationId)))
        .ToLowerInvariant();

    private static OpenCodeConsoleFetchResult InvalidResponse(HttpStatusCode? statusCode = null) => new(
        OpenCodeConsoleFetchOutcome.InvalidResponse,
        [],
        statusCode);

    private sealed record HttpJsonResult(
        JsonDocument? Document,
        OpenCodeConsoleFetchResult? Failure)
    {
        public static HttpJsonResult FromDocument(JsonDocument document) => new(document, null);

        public static HttpJsonResult FromFailure(OpenCodeConsoleFetchResult failure) => new(null, failure);
    }
}
