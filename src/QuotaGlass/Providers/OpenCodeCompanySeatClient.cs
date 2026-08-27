using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text.Json;

namespace QuotaGlass.Providers;

internal enum OpenCodeCompanySeatFetchOutcome
{
    Success,
    AuthenticationRequired,
    TransientFailure,
    RateLimited,
    InvalidResponse,
}

internal sealed record OpenCodeCompanySeatBudget(
    BigInteger? LimitMicroCents,
    BigInteger SpentMicroCents,
    bool Exceeded,
    DateTimeOffset? ResetsAt,
    string? Source);

internal sealed record OpenCodeCompanySeatFetchResult(
    OpenCodeCompanySeatFetchOutcome Outcome,
    OpenCodeCompanySeatBudget? Budget = null,
    HttpStatusCode? StatusCode = null,
    TimeSpan? RetryAfter = null);

internal interface IOpenCodeCompanySeatClient
{
    Task<OpenCodeCompanySeatFetchResult> FetchAsync(
        OpenCodeConsoleActiveWorkspace workspace,
        CancellationToken cancellationToken);
}

internal sealed class OpenCodeCompanySeatClient : IOpenCodeCompanySeatClient
{
    private static readonly Uri CurrentOrganizationUri =
        new("https://opencode.ai/console/api/orgs/current");
    private static readonly Uri MemberBudgetsBaseUri =
        new("https://opencode.ai/console/api/budgets/users/");

    private readonly HttpMessageHandler _handler;
    private readonly TimeProvider _timeProvider;

    public OpenCodeCompanySeatClient(
        HttpMessageHandler handler,
        TimeProvider? timeProvider = null)
    {
        _handler = handler;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<OpenCodeCompanySeatFetchResult> FetchAsync(
        OpenCodeConsoleActiveWorkspace workspace,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient(_handler, disposeHandler: false);
        HttpJsonResult organization = await GetJsonAsync(
            client,
            CurrentOrganizationUri,
            workspace,
            cancellationToken).ConfigureAwait(false);
        if (organization.Failure is not null)
        {
            return organization.Failure;
        }

        string? memberId;
        using (JsonDocument document = organization.Document!)
        {
            memberId = TryReadCurrentMemberId(document.RootElement);
        }

        if (memberId is null)
        {
            return InvalidResponse(organization.StatusCode);
        }

        var budgetUri = new Uri(MemberBudgetsBaseUri, Uri.EscapeDataString(memberId));
        HttpJsonResult response = await GetJsonAsync(
            client,
            budgetUri,
            workspace,
            cancellationToken).ConfigureAwait(false);
        if (response.Failure is not null)
        {
            return response.Failure;
        }

        using JsonDocument budgetDocument = response.Document!;
        if (!TryReadBudget(
            budgetDocument.RootElement,
            memberId,
            _timeProvider.GetUtcNow(),
            out OpenCodeCompanySeatBudget? budget))
        {
            return InvalidResponse(response.StatusCode);
        }

        return new OpenCodeCompanySeatFetchResult(
            OpenCodeCompanySeatFetchOutcome.Success,
            budget,
            response.StatusCode);
    }

    private async Task<HttpJsonResult> GetJsonAsync(
        HttpClient client,
        Uri uri,
        OpenCodeConsoleActiveWorkspace workspace,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            workspace.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        request.Headers.TryAddWithoutValidation("x-org-id", workspace.OrganizationId);

        using HttpResponseMessage response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return HttpJsonResult.FromFailure(new OpenCodeCompanySeatFetchResult(
                OpenCodeCompanySeatFetchOutcome.AuthenticationRequired,
                StatusCode: response.StatusCode));
        }

        TimeSpan? retryAfter = ProviderHttpSafety.GetRetryAfter(
            response,
            _timeProvider.GetUtcNow());
        if (retryAfter is not null)
        {
            return HttpJsonResult.FromFailure(new OpenCodeCompanySeatFetchResult(
                OpenCodeCompanySeatFetchOutcome.RateLimited,
                StatusCode: response.StatusCode,
                RetryAfter: retryAfter));
        }

        if (!response.IsSuccessStatusCode)
        {
            return HttpJsonResult.FromFailure(new OpenCodeCompanySeatFetchResult(
                OpenCodeCompanySeatFetchOutcome.TransientFailure,
                StatusCode: response.StatusCode));
        }

        try
        {
            JsonDocument document = await ProviderHttpSafety
                .ReadJsonAsync(response, cancellationToken)
                .ConfigureAwait(false);
            return new HttpJsonResult(document, null, response.StatusCode);
        }
        catch (InvalidDataException)
        {
            return HttpJsonResult.FromFailure(InvalidResponse(response.StatusCode));
        }
    }

    private static string? TryReadCurrentMemberId(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("userId", out JsonElement userId) ||
            userId.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? value = userId.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool TryReadBudget(
        JsonElement root,
        string expectedMemberId,
        DateTimeOffset now,
        out OpenCodeCompanySeatBudget? budget)
    {
        budget = null;
        if (root.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !TryReadExactString(root, "scope", "user") ||
            !TryReadExactString(root, "userId", expectedMemberId) ||
            !TryReadOptionalAmount(root, "limitMicroCents", out BigInteger? limit) ||
            !TryReadRequiredAmount(root, "spentMicroCents", out BigInteger spent) ||
            !root.TryGetProperty("exceeded", out JsonElement exceeded) ||
            exceeded.ValueKind is not JsonValueKind.True and not JsonValueKind.False ||
            !TryReadOptionalReset(root, now, out DateTimeOffset? resetsAt) ||
            !TryReadSource(root, out string? source))
        {
            return false;
        }

        budget = new OpenCodeCompanySeatBudget(
            limit,
            spent,
            exceeded.GetBoolean(),
            resetsAt,
            source);
        return true;
    }

    private static bool TryReadExactString(
        JsonElement root,
        string propertyName,
        string expected) =>
        root.TryGetProperty(propertyName, out JsonElement element) &&
        element.ValueKind == JsonValueKind.String &&
        string.Equals(element.GetString(), expected, StringComparison.Ordinal);

    private static bool TryReadOptionalAmount(
        JsonElement root,
        string propertyName,
        out BigInteger? amount)
    {
        amount = null;
        if (!root.TryGetProperty(propertyName, out JsonElement element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (!TryReadAmount(element, out BigInteger value))
        {
            return false;
        }

        amount = value;
        return true;
    }

    private static bool TryReadRequiredAmount(
        JsonElement root,
        string propertyName,
        out BigInteger amount)
    {
        amount = default;
        return root.TryGetProperty(propertyName, out JsonElement element) &&
            TryReadAmount(element, out amount);
    }

    private static bool TryReadAmount(JsonElement element, out BigInteger amount)
    {
        amount = default;
        return element.ValueKind == JsonValueKind.String &&
            BigInteger.TryParse(
                element.GetString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out amount) &&
            amount >= BigInteger.Zero;
    }

    private static bool TryReadOptionalReset(
        JsonElement root,
        DateTimeOffset now,
        out DateTimeOffset? resetsAt)
    {
        resetsAt = null;
        if (!root.TryGetProperty("resetsAt", out JsonElement element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(
                element.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed) ||
            parsed <= now)
        {
            return false;
        }

        resetsAt = parsed;
        return true;
    }

    private static bool TryReadSource(JsonElement root, out string? source)
    {
        source = null;
        if (!root.TryGetProperty("source", out JsonElement element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        source = element.GetString();
        return source is "custom" or "default";
    }

    private static OpenCodeCompanySeatFetchResult InvalidResponse(
        HttpStatusCode? statusCode = null) => new(
        OpenCodeCompanySeatFetchOutcome.InvalidResponse,
        StatusCode: statusCode);

    private sealed record HttpJsonResult(
        JsonDocument? Document,
        OpenCodeCompanySeatFetchResult? Failure,
        HttpStatusCode? StatusCode = null)
    {
        public static HttpJsonResult FromFailure(OpenCodeCompanySeatFetchResult failure) =>
            new(null, failure, failure.StatusCode);
    }
}
