using System.Collections.Immutable;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using AiStatus.Model;

namespace AiStatus.Providers;

public sealed class OllamaProvider(HttpMessageHandler handler, TimeProvider? timeProvider = null) : IStatusProvider
{
    private static readonly Uri VersionUri = new("http://localhost:11434/api/version");
    private static readonly Uri ProcessUri = new("http://localhost:11434/api/ps");
    private readonly HttpMessageHandler _handler = handler;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public string Id => "ollama";

    public string Label => "Ollama";

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset fetchedAt = _timeProvider.GetUtcNow();

        try
        {
            using var client = new HttpClient(_handler, disposeHandler: false);
            using HttpResponseMessage versionResponse = await SendAsync(client, VersionUri, cancellationToken)
                .ConfigureAwait(false);
            versionResponse.EnsureSuccessStatusCode();
            using JsonDocument version = JsonDocument.Parse(
                await versionResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));

            using HttpResponseMessage processResponse = await SendAsync(client, ProcessUri, cancellationToken)
                .ConfigureAwait(false);
            processResponse.EnsureSuccessStatusCode();
            using JsonDocument processes = JsonDocument.Parse(
                await processResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));

            return new ProviderSnapshot(
                Id,
                Label,
                HealthState.Ok,
                null,
                ImmutableArray<UsageWindow>.Empty,
                [
                    new InfoLine("Version", version.RootElement.GetProperty("version").GetString()!),
                    new InfoLine("Loaded models", processes.RootElement.GetProperty("models").GetArrayLength().ToString())
                ],
                null,
                fetchedAt,
                0);
        }
        catch (HttpRequestException)
        {
            return new ProviderSnapshot(
                Id,
                Label,
                HealthState.Unreachable,
                null,
                ImmutableArray<UsageWindow>.Empty,
                ImmutableArray<InfoLine>.Empty,
                null,
                fetchedAt,
                0);
        }
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
