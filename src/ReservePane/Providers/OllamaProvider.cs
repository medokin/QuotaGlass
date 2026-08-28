using System.Collections.Immutable;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using ReservePane.Model;

namespace ReservePane.Providers;

public sealed class OllamaProvider(HttpMessageHandler handler, TimeProvider? timeProvider = null)
    : IStatusProvider, IProviderAvailability
{
    private static readonly Uri VersionUri = new("http://localhost:11434/api/version");
    private static readonly Uri ProcessUri = new("http://localhost:11434/api/ps");
    private readonly HttpMessageHandler _handler = handler;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public string Id => "ollama";

    public string Label => "Ollama";

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient(_handler, disposeHandler: false);
            using HttpResponseMessage response = await SendAsync(client, VersionUri, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public async Task<ProviderFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset fetchedAt = _timeProvider.GetUtcNow();

        try
        {
            using var client = new HttpClient(_handler, disposeHandler: false);
            using HttpResponseMessage versionResponse = await SendAsync(client, VersionUri, cancellationToken)
                .ConfigureAwait(false);
            ProviderFetchResult? versionFailure = FailureFor(versionResponse, fetchedAt);
            if (versionFailure is not null)
            {
                return versionFailure;
            }

            using JsonDocument version = await ProviderHttpSafety
                .ReadJsonAsync(versionResponse, cancellationToken)
                .ConfigureAwait(false);
            if (version.RootElement.ValueKind != JsonValueKind.Object ||
                !version.RootElement.TryGetProperty("version", out JsonElement versionValue) ||
                versionValue.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(versionValue.GetString()))
            {
                return new ProviderFetchResult(
                    ProviderFetchOutcome.InvalidResponse,
                    statusCode: versionResponse.StatusCode);
            }

            using HttpResponseMessage processResponse = await SendAsync(client, ProcessUri, cancellationToken)
                .ConfigureAwait(false);
            ProviderFetchResult? processFailure = FailureFor(processResponse, fetchedAt);
            if (processFailure is not null)
            {
                return processFailure;
            }

            using JsonDocument processes = await ProviderHttpSafety
                .ReadJsonAsync(processResponse, cancellationToken)
                .ConfigureAwait(false);

            if (processes.RootElement.ValueKind != JsonValueKind.Object ||
                !processes.RootElement.TryGetProperty("models", out JsonElement models) ||
                models.ValueKind != JsonValueKind.Array)
            {
                return new ProviderFetchResult(
                    ProviderFetchOutcome.InvalidResponse,
                    statusCode: processResponse.StatusCode);
            }

            return new ProviderFetchResult(
                ProviderFetchOutcome.Success,
                new ProviderSnapshot(
                    Id,
                    Label,
                    HealthState.Ok,
                    null,
                    ImmutableArray<UsageWindow>.Empty,
                    [
                        new InfoLine("Version", versionValue.GetString()!),
                        new InfoLine("Loaded models", models.GetArrayLength().ToString())
                    ],
                    null,
                    fetchedAt,
                    0));
        }
        catch (HttpRequestException)
        {
            return new ProviderFetchResult(ProviderFetchOutcome.TransientFailure);
        }
        catch (InvalidDataException)
        {
            return new ProviderFetchResult(ProviderFetchOutcome.InvalidResponse);
        }
    }

    private static ProviderFetchResult? FailureFor(HttpResponseMessage response, DateTimeOffset now)
    {
        TimeSpan? retryAfter = ProviderHttpSafety.GetRetryAfter(response, now);
        if (retryAfter is not null)
        {
            return new ProviderFetchResult(
                ProviderFetchOutcome.RateLimited,
                statusCode: response.StatusCode,
                retryAfter: retryAfter);
        }

        return response.IsSuccessStatusCode
            ? null
            : new ProviderFetchResult(
                ProviderFetchOutcome.TransientFailure,
                statusCode: response.StatusCode);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
