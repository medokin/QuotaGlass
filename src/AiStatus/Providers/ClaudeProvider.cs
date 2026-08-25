using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using AiStatus.Model;

namespace AiStatus.Providers;

public sealed class ClaudeProvider : IStatusProvider
{
    private static readonly Uri UsageUri = new("https://api.anthropic.com/api/oauth/usage");
    private static readonly Uri ProfileUri = new("https://api.anthropic.com/api/oauth/profile");
    private readonly string _credentialPath;
    private readonly HttpMessageHandler _handler;
    private readonly Func<double?, Severity> _severityFromPercent;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, Stream> _openCredential;
    private DateTimeOffset _profileCachedAt;
    private string? _cachedPlanLabel;

    public ClaudeProvider(
        string credentialPath,
        HttpMessageHandler handler,
        Func<double?, Severity> severityFromPercent,
        TimeProvider? timeProvider = null)
        : this(credentialPath, handler, severityFromPercent, timeProvider, File.OpenRead)
    {
    }

    internal ClaudeProvider(
        string credentialPath,
        HttpMessageHandler handler,
        Func<double?, Severity> severityFromPercent,
        TimeProvider? timeProvider,
        Func<string, Stream> openCredential)
    {
        _credentialPath = credentialPath;
        _handler = handler;
        _severityFromPercent = severityFromPercent;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _openCredential = openCredential;
    }

    public string Id => "claude";

    public string Label => "Claude";

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset fetchedAt = _timeProvider.GetUtcNow();

        try
        {
            Credential credential = ReadCredential();
            if (credential.ExpiresAt <= fetchedAt)
            {
                return Snapshot(HealthState.AuthExpired, null, [], [], "re-auth: run claude login", fetchedAt);
            }

            using var client = new HttpClient(_handler, disposeHandler: false);
            using HttpResponseMessage usageResponse = await SendAsync(client, UsageUri, credential.AccessToken, cancellationToken);
            if (IsAuthExpired(usageResponse.StatusCode))
            {
                return Snapshot(HealthState.AuthExpired, null, [], [], "re-auth: run claude login", fetchedAt);
            }

            if (!usageResponse.IsSuccessStatusCode)
            {
                return Snapshot(HealthState.Degraded, null, [], [], "Claude usage request failed", fetchedAt);
            }

            if (!IsJson(usageResponse))
            {
                return Snapshot(HealthState.Degraded, null, [], [], "Claude usage response was not JSON", fetchedAt);
            }

            using JsonDocument usage = JsonDocument.Parse(await usageResponse.Content.ReadAsStreamAsync(cancellationToken));
            ProfileResult profile = await GetProfileAsync(client, credential.AccessToken, fetchedAt, cancellationToken);
            if (profile.AuthExpired)
            {
                return Snapshot(HealthState.AuthExpired, null, [], [], "re-auth: run claude login", fetchedAt);
            }

            if (profile.Error is not null)
            {
                return Snapshot(HealthState.Degraded, null, [], [], profile.Error, fetchedAt);
            }

            return Snapshot(
                HealthState.Ok,
                profile.PlanLabel,
                ReadWindows(usage.RootElement),
                ReadSpend(usage.RootElement),
                null,
                fetchedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Snapshot(HealthState.Degraded, null, [], [], "Claude status could not be read", fetchedAt);
        }
    }

    private Credential ReadCredential()
    {
        using Stream stream = _openCredential(_credentialPath);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
        int bytesInBuffer = 0;
        var state = new JsonReaderState();
        bool awaitsOauthObject = false;
        bool inOauthObject = false;
        int oauthObjectDepth = -1;
        CredentialField field = CredentialField.None;
        string? accessToken = null;
        long? expiresAtUnixMilliseconds = null;

        try
        {
            while (true)
            {
                if (bytesInBuffer == buffer.Length)
                {
                    byte[] expandedBuffer = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    Buffer.BlockCopy(buffer, 0, expandedBuffer, 0, bytesInBuffer);
                    CryptographicOperations.ZeroMemory(buffer);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = expandedBuffer;
                }

                int read = stream.Read(buffer, bytesInBuffer, buffer.Length - bytesInBuffer);
                bool isFinalBlock = read == 0;
                bytesInBuffer += read;
                var reader = new Utf8JsonReader(buffer.AsSpan(0, bytesInBuffer), isFinalBlock, state);

                while (reader.Read())
                {
                    if (awaitsOauthObject)
                    {
                        if (reader.TokenType != JsonTokenType.StartObject)
                        {
                            throw new InvalidDataException("Claude credentials are incomplete.");
                        }

                        awaitsOauthObject = false;
                        inOauthObject = true;
                        oauthObjectDepth = reader.CurrentDepth;
                        continue;
                    }

                    if (!inOauthObject)
                    {
                        if (reader.TokenType == JsonTokenType.PropertyName &&
                            reader.CurrentDepth == 1 &&
                            reader.ValueTextEquals("claudeAiOauth"))
                        {
                            awaitsOauthObject = true;
                        }

                        continue;
                    }

                    if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == oauthObjectDepth + 1)
                    {
                        field = reader.ValueTextEquals("accessToken")
                            ? CredentialField.AccessToken
                            : reader.ValueTextEquals("expiresAt")
                                ? CredentialField.ExpiresAt
                                : CredentialField.None;
                        continue;
                    }

                    if (field == CredentialField.AccessToken && reader.TokenType == JsonTokenType.String)
                    {
                        accessToken = reader.GetString();
                        field = CredentialField.None;
                        continue;
                    }

                    if (field == CredentialField.ExpiresAt && reader.TokenType == JsonTokenType.Number)
                    {
                        if (!reader.TryGetInt64(out long expiresAt))
                        {
                            throw new InvalidDataException("Claude credentials are incomplete.");
                        }

                        expiresAtUnixMilliseconds = expiresAt;
                        field = CredentialField.None;
                        continue;
                    }

                    if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == oauthObjectDepth)
                    {
                        if (string.IsNullOrWhiteSpace(accessToken) || expiresAtUnixMilliseconds is not long expiresAt)
                        {
                            throw new InvalidDataException("Claude credentials are incomplete.");
                        }

                        return new Credential(accessToken, DateTimeOffset.FromUnixTimeMilliseconds(expiresAt));
                    }
                }

                int consumed = checked((int)reader.BytesConsumed);
                state = reader.CurrentState;
                int unconsumed = bytesInBuffer - consumed;
                if (unconsumed > 0)
                {
                    Buffer.BlockCopy(buffer, consumed, buffer, 0, unconsumed);
                }

                bytesInBuffer = unconsumed;
                if (isFinalBlock)
                {
                    throw new InvalidDataException("Claude credentials are incomplete.");
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<ProfileResult> GetProfileAsync(
        HttpClient client,
        string accessToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_profileCachedAt.AddHours(1) > now)
        {
            return new ProfileResult(_cachedPlanLabel, false, null);
        }

        using HttpResponseMessage response = await SendAsync(client, ProfileUri, accessToken, cancellationToken);
        if (IsAuthExpired(response.StatusCode))
        {
            return new ProfileResult(null, true, null);
        }

        if (!response.IsSuccessStatusCode)
        {
            return new ProfileResult(null, false, "Claude profile request failed");
        }

        if (!IsJson(response))
        {
            return new ProfileResult(null, false, "Claude profile response was not JSON");
        }

        using JsonDocument profile = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        _cachedPlanLabel = TryGetObject(profile.RootElement, "organization") is JsonElement organization
            ? TryGetString(organization, "seat_tier")
            : null;
        _profileCachedAt = now;
        return new ProfileResult(_cachedPlanLabel, false, null);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Uri uri,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        return await client.SendAsync(request, cancellationToken);
    }

    private ImmutableArray<UsageWindow> ReadWindows(JsonElement root)
    {
        if (!TryGetProperty(root, "limits", out JsonElement limits) || limits.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var windows = ImmutableArray.CreateBuilder<UsageWindow>();
        foreach (JsonElement limit in limits.EnumerateArray())
        {
            if (limit.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            double? percent = TryGetDouble(limit, "percent");
            windows.Add(new UsageWindow(
                TryGetString(limit, "group") ?? TryGetString(limit, "kind") ?? "usage",
                percent,
                TryGetDateTimeOffset(limit, "resets_at"),
                TryGetSeverity(limit, "severity") ?? _severityFromPercent(percent)));
        }

        return windows.ToImmutable();
    }

    private static ImmutableArray<InfoLine> ReadSpend(JsonElement root)
    {
        if (TryGetObject(root, "spend") is not JsonElement spend ||
            TryGetObject(spend, "used") is not JsonElement used ||
            TryGetDecimal(used, "amount_minor") is not decimal amountMinor ||
            TryGetString(used, "currency") is not string currency ||
            TryGetInt32(used, "exponent") is not int exponent ||
            exponent is < 0 or > 28)
        {
            return [];
        }

        decimal divisor = 1;
        for (int index = 0; index < exponent; index++)
        {
            divisor *= 10;
        }

        string amount = (amountMinor / divisor).ToString($"F{exponent}", CultureInfo.InvariantCulture);
        bool hasCap = spend.TryGetProperty("limit", out JsonElement limit) && limit.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
        string value = hasCap
            ? $"{currency} {amount} this cycle"
            : $"{currency} {amount} this cycle (no cap set)";
        return [new InfoLine("Extra usage", value)];
    }

    private static bool IsAuthExpired(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private static bool IsJson(HttpResponseMessage response) =>
        string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out value);
    }

    private static JsonElement? TryGetObject(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? TryGetDouble(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.TryGetDouble(out double result)
            ? result
            : null;

    private static decimal? TryGetDecimal(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.TryGetDecimal(out decimal result)
            ? result
            : null;

    private static int? TryGetInt32(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.TryGetInt32(out int result)
            ? result
            : null;

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
    {
        string? value = TryGetString(element, propertyName);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset result)
            ? result
            : null;
    }

    private static Severity? TryGetSeverity(JsonElement element, string propertyName) =>
        TryGetString(element, propertyName)?.ToLowerInvariant() switch
        {
            "normal" => Severity.Normal,
            "warning" => Severity.Warning,
            "critical" => Severity.Critical,
            _ => null
        };

    private static ProviderSnapshot Snapshot(
        HealthState health,
        string? planLabel,
        ImmutableArray<UsageWindow> windows,
        ImmutableArray<InfoLine> info,
        string? error,
        DateTimeOffset fetchedAt) =>
        new("claude", "Claude", health, planLabel, windows, info, error, fetchedAt, 0);

    private sealed record Credential(string AccessToken, DateTimeOffset ExpiresAt);

    private sealed record ProfileResult(string? PlanLabel, bool AuthExpired, string? Error);

    private enum CredentialField { None, AccessToken, ExpiresAt }
}
