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
        CancellationToken cancellationToken,
        string? workspaceSelector = null);
}

internal sealed class OpenCodeConsoleGoClient : IOpenCodeConsoleGoClient
{
    private const int MaximumConcurrentRequests = 4;
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
        CancellationToken cancellationToken,
        string? workspaceSelector = null)
    {
        using var client = new HttpClient(_handler, disposeHandler: false);
        using var requestGate = new SemaphoreSlim(MaximumConcurrentRequests);
        OrganizationDiscovery[] discoveries = await Task.WhenAll(accounts.Select(account =>
            DiscoverOrganizationsAsync(client, requestGate, account, cancellationToken)))
            .ConfigureAwait(false);

        var targets = ImmutableArray.CreateBuilder<WorkspaceTarget>();
        OpenCodeConsoleFetchResult? authenticationFailure = null;
        OpenCodeConsoleFetchResult? blockingFailure = null;
        foreach (OrganizationDiscovery discovery in discoveries)
        {
            RememberFailure(discovery.Failure, ref authenticationFailure, ref blockingFailure);
            targets.AddRange(discovery.Targets);
        }

        ImmutableArray<WorkspaceTarget> selectedTargets = targets
            .OrderBy(target => target.Selector, StringComparer.Ordinal)
            .Where(target => workspaceSelector is null ||
                string.Equals(target.Selector, workspaceSelector, StringComparison.Ordinal))
            .ToImmutableArray();
        WorkspaceDiscovery[] workspaceDiscoveries = await Task.WhenAll(selectedTargets.Select(target =>
            DiscoverWorkspaceAsync(client, requestGate, target, cancellationToken)))
            .ConfigureAwait(false);

        var workspaces = ImmutableArray.CreateBuilder<OpenCodeConsoleWorkspace>();
        foreach (WorkspaceDiscovery discovery in workspaceDiscoveries)
        {
            RememberFailure(discovery.Failure, ref authenticationFailure, ref blockingFailure);
            if (discovery.Workspace is not null)
            {
                workspaces.Add(discovery.Workspace);
            }
        }

        if (workspaceSelector is not null && workspaces.Count > 0)
        {
            return Success(workspaces.ToImmutable());
        }

        if (blockingFailure is not null)
        {
            return blockingFailure;
        }

        return workspaces.Count > 0
            ? Success(workspaces.ToImmutable())
            : authenticationFailure ?? Success([]);
    }

    private async Task<OrganizationDiscovery> DiscoverOrganizationsAsync(
        HttpClient client,
        SemaphoreSlim requestGate,
        OpenCodeConsoleAccount account,
        CancellationToken cancellationToken)
    {
        HttpJsonResult organizations = await GetJsonAsync(
            client,
            requestGate,
            OrganizationsUri,
            account.AccessToken,
            organizationId: null,
            cancellationToken).ConfigureAwait(false);
        if (organizations.Failure is not null)
        {
            return new OrganizationDiscovery([], organizations.Failure);
        }

        using JsonDocument document = organizations.Document!;
        if (!TryReadOrganizationIds(document.RootElement, out ImmutableArray<string> organizationIds))
        {
            return new OrganizationDiscovery([], InvalidResponse());
        }

        ImmutableArray<WorkspaceTarget> targets = organizationIds
            .Select(organizationId => new WorkspaceTarget(
                account,
                organizationId,
                CreateSelector(account.AccountId, organizationId)))
            .ToImmutableArray();
        return new OrganizationDiscovery(targets, null);
    }

    private async Task<WorkspaceDiscovery> DiscoverWorkspaceAsync(
        HttpClient client,
        SemaphoreSlim requestGate,
        WorkspaceTarget target,
        CancellationToken cancellationToken)
    {
        HttpJsonResult status = await GetJsonAsync(
            client,
            requestGate,
            GoStatusUri,
            target.Account.AccessToken,
            target.OrganizationId,
            cancellationToken).ConfigureAwait(false);
        if (status.Failure is not null)
        {
            return new WorkspaceDiscovery(null, status.Failure);
        }

        using JsonDocument document = status.Document!;
        if (!TryReadWorkspace(document.RootElement, out ImmutableArray<UsageWindow> windows))
        {
            return new WorkspaceDiscovery(null, InvalidResponse());
        }

        OpenCodeConsoleWorkspace? workspace = windows.IsEmpty
            ? null
            : new OpenCodeConsoleWorkspace(target.Selector, windows);
        return new WorkspaceDiscovery(workspace, null);
    }

    private async Task<HttpJsonResult> GetJsonAsync(
        HttpClient client,
        SemaphoreSlim requestGate,
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

        await requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
        finally
        {
            requestGate.Release();
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
        if (!TryAddWindow(meters, "fiveHour", "rolling", "resetsAt", null, true, result) ||
            !TryAddWindow(meters, "week", "weekly", "resetsAt", null, false, result) ||
            !TryAddWindow(meters, "month", "monthly", null, monthReset, false, result))
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
        string? resetPropertyName,
        DateTimeOffset? fixedReset,
        bool allowNullReset,
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

        DateTimeOffset? reset;
        if (resetPropertyName is null)
        {
            reset = fixedReset;
        }
        else if (!TryReadOptionalTimestamp(meter, resetPropertyName, allowNullReset, out reset))
        {
            return false;
        }

        const int percentageScale = 1_000_000;
        BigInteger scaledPercent = used * (100 * percentageScale) / limit;
        double percent = (double)scaledPercent / percentageScale;
        if (!double.IsFinite(percent) || percent < 0)
        {
            return false;
        }

        windows.Add(new UsageWindow(label, percent, reset, _severityFromPercent(percent)));
        return true;
    }

    private static bool TryReadOptionalTimestamp(
        JsonElement parent,
        string propertyName,
        bool allowNull,
        out DateTimeOffset? timestamp)
    {
        timestamp = null;
        if (!parent.TryGetProperty(propertyName, out JsonElement element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return allowNull;
        }

        if (element.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(
                element.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed))
        {
            return false;
        }

        timestamp = parsed;
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

    private static OpenCodeConsoleFetchResult Success(
        ImmutableArray<OpenCodeConsoleWorkspace> workspaces) => new(
        OpenCodeConsoleFetchOutcome.Success,
        workspaces);

    private static void RememberFailure(
        OpenCodeConsoleFetchResult? failure,
        ref OpenCodeConsoleFetchResult? authenticationFailure,
        ref OpenCodeConsoleFetchResult? blockingFailure)
    {
        if (failure?.Outcome == OpenCodeConsoleFetchOutcome.AuthenticationRequired)
        {
            authenticationFailure ??= failure;
        }
        else if (failure is not null)
        {
            blockingFailure ??= failure;
        }
    }

    private sealed record WorkspaceTarget(
        OpenCodeConsoleAccount Account,
        string OrganizationId,
        string Selector);

    private sealed record OrganizationDiscovery(
        ImmutableArray<WorkspaceTarget> Targets,
        OpenCodeConsoleFetchResult? Failure);

    private sealed record WorkspaceDiscovery(
        OpenCodeConsoleWorkspace? Workspace,
        OpenCodeConsoleFetchResult? Failure);

    private sealed record HttpJsonResult(
        JsonDocument? Document,
        OpenCodeConsoleFetchResult? Failure)
    {
        public static HttpJsonResult FromDocument(JsonDocument document) => new(document, null);

        public static HttpJsonResult FromFailure(OpenCodeConsoleFetchResult failure) => new(null, failure);
    }
}
