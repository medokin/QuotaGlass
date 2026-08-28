using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using ReservePane.Model;

namespace ReservePane.Providers;

public sealed class GrokProvider : IStatusProvider, IProviderAvailability
{
    private static readonly Uri UsageUri = new("https://cli-chat-proxy.grok.com/v1/billing?format=credits");
    private readonly string _credentialPath;
    private readonly HttpMessageHandler _handler;
    private readonly Func<double?, Severity> _severityFromPercent;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, Stream> _openCredential;
    private readonly Func<string, bool> _credentialProbe;

    public GrokProvider(
        string credentialPath,
        HttpMessageHandler handler,
        Func<double?, Severity> severityFromPercent,
        TimeProvider? timeProvider = null)
        : this(credentialPath, handler, severityFromPercent, timeProvider, OpenCredentialStream)
    {
    }

    internal GrokProvider(
        string credentialPath,
        HttpMessageHandler handler,
        Func<double?, Severity> severityFromPercent,
        TimeProvider? timeProvider,
        Func<string, Stream> openCredential)
        : this(
            credentialPath,
            handler,
            severityFromPercent,
            timeProvider,
            openCredential,
            CredentialFilePrerequisite.Probe)
    {
    }

    internal GrokProvider(
        string credentialPath,
        HttpMessageHandler handler,
        Func<double?, Severity> severityFromPercent,
        TimeProvider? timeProvider,
        Func<string, Stream> openCredential,
        Func<string, bool> credentialProbe)
    {
        _credentialPath = credentialPath;
        _handler = handler;
        _severityFromPercent = severityFromPercent;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _openCredential = openCredential;
        _credentialProbe = credentialProbe;
    }

    public string Id => "grok";

    public string Label => "Grok";

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
        CredentialFilePrerequisite.IsPresentOrIndeterminateAsync(
            _credentialPath,
            _credentialProbe,
            cancellationToken);

    internal static Stream OpenCredentialStream(string credentialPath) =>
        new FileStream(
            credentialPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1,
            FileOptions.SequentialScan);

    public async Task<ProviderFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset fetchedAt = _timeProvider.GetUtcNow();
        Credential credential;
        try
        {
            credential = ReadCredential();
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or
            DirectoryNotFoundException or
            InvalidDataException)
        {
            return new ProviderFetchResult(
                ProviderFetchOutcome.NotConfigured,
                Snapshot(HealthState.Unreachable, null, [], null, fetchedAt));
        }

        using var client = new HttpClient(_handler, disposeHandler: false);
        using HttpResponseMessage response = await SendAsync(client, credential, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new ProviderFetchResult(
                ProviderFetchOutcome.AuthenticationRequired,
                Snapshot(HealthState.AuthExpired, null, [], "re-auth: run grok login", fetchedAt),
                response.StatusCode);
        }

        TimeSpan? retryAfter = ProviderHttpSafety.GetRetryAfter(response, fetchedAt);
        if (retryAfter is not null)
        {
            return new ProviderFetchResult(
                ProviderFetchOutcome.RateLimited,
                statusCode: response.StatusCode,
                retryAfter: retryAfter);
        }

        if (!response.IsSuccessStatusCode)
        {
            return new ProviderFetchResult(
                ProviderFetchOutcome.TransientFailure,
                statusCode: response.StatusCode);
        }

        JsonDocument usage;
        try
        {
            usage = await ProviderHttpSafety.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return new ProviderFetchResult(
                ProviderFetchOutcome.InvalidResponse,
                statusCode: response.StatusCode);
        }

        using (usage)
        {
            return new ProviderFetchResult(
                ProviderFetchOutcome.Success,
                Snapshot(
                    HealthState.Ok,
                    ReadPlanLabel(usage.RootElement),
                    ReadWindows(usage.RootElement),
                    null,
                    fetchedAt));
        }
    }

    private Credential ReadCredential()
    {
        using Stream stream = _openCredential(_credentialPath);
        return new CredentialScanner(stream).Read();
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Credential credential,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        request.Headers.Add("x-xai-token-auth", "xai-grok-cli");
        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private ImmutableArray<UsageWindow> ReadWindows(JsonElement root)
    {
        if (TryGetObject(root, "config") is not JsonElement config)
        {
            return [];
        }

        var windows = ImmutableArray.CreateBuilder<UsageWindow>();
        DateTimeOffset? resetsAt = ReadReset(config);
        if (TryGetDouble(config, "creditUsagePercent") is double percent)
        {
            windows.Add(new UsageWindow(
                PeriodLabel(config),
                percent,
                resetsAt,
                _severityFromPercent(percent)));
        }
        else if (TryGetObject(config, "currentPeriod") is not null)
        {
            windows.Add(new UsageWindow(
                PeriodLabel(config),
                0,
                resetsAt,
                _severityFromPercent(0)));
        }

        if (TryGetProperty(config, "productUsage", out JsonElement products) &&
            products.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement product in products.EnumerateArray())
            {
                if (product.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? name = TryGetString(product, "product");
                if (name is null || TryGetDouble(product, "usagePercent") is not double productPercent)
                {
                    continue;
                }

                windows.Add(new UsageWindow(
                    name,
                    productPercent,
                    resetsAt,
                    _severityFromPercent(productPercent)));
            }
        }

        if (TryGetObject(config, "onDemandCap") is JsonElement cap &&
            TryGetDouble(cap, "val") is double capValue &&
            capValue > 0)
        {
            double used = TryGetObject(config, "onDemandUsed") is JsonElement usedObject &&
                TryGetDouble(usedObject, "val") is double usedValue
                    ? usedValue
                    : 0;
            double onDemandPercent = used / capValue * 100;
            windows.Add(new UsageWindow(
                "on-demand",
                onDemandPercent,
                null,
                _severityFromPercent(onDemandPercent)));
        }

        return windows.ToImmutable();
    }

    private static string? ReadPlanLabel(JsonElement root)
    {
        JsonElement? config = TryGetObject(root, "config");
        return (config is JsonElement value ? TryGetString(value, "subscriptionTierDisplay") : null) ??
            TryGetString(root, "subscription_tier") ??
            (config is JsonElement fallback ? TryGetString(fallback, "subscriptionTier") : null);
    }

    private static string PeriodLabel(JsonElement config) =>
        TryGetObject(config, "currentPeriod") is JsonElement period
            ? TryGetString(period, "type") switch
            {
                "USAGE_PERIOD_TYPE_WEEKLY" => "weekly",
                "USAGE_PERIOD_TYPE_MONTHLY" => "monthly",
                _ => "credits",
            }
            : "credits";

    private static DateTimeOffset? ReadReset(JsonElement config)
    {
        if (TryGetObject(config, "currentPeriod") is JsonElement period &&
            TryGetDateTimeOffset(period, "end") is DateTimeOffset periodEnd)
        {
            return periodEnd;
        }

        return TryGetDateTimeOffset(config, "billingPeriodEnd");
    }

    private static JsonElement? TryGetObject(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out value);
    }

    private static string? TryGetString(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? TryGetDouble(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out JsonElement value) && value.TryGetDouble(out double result)
            ? result
            : null;

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
    {
        string? value = TryGetString(element, propertyName);
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out DateTimeOffset result)
            ? result
            : null;
    }

    private static ProviderSnapshot Snapshot(
        HealthState health,
        string? planLabel,
        ImmutableArray<UsageWindow> windows,
        string? error,
        DateTimeOffset fetchedAt) =>
        new("grok", "Grok", health, planLabel, windows, [], error, fetchedAt, 0);

    private sealed record Credential(string AccessToken);

    private sealed class CredentialScanner(Stream stream)
    {
        private const int MaxUnknownContainerDepth = 64;
        private static readonly byte[] PreferredPrefix = "https://auth.x.ai"u8.ToArray();
        private static readonly byte[] LegacyScope = "https://accounts.x.ai/sign-in"u8.ToArray();
        private static readonly byte[] KeyProperty = "key"u8.ToArray();
        private static readonly byte[] AccessTokenProperty = "access_token"u8.ToArray();
        private readonly Stream _stream = stream;
        private int _lookahead = -1;

        public Credential Read()
        {
            Expect((byte)'{');
            string? legacy = null;
            while (true)
            {
                int next = ReadNonWhitespace();
                if (next == '}')
                {
                    return Finish(legacy);
                }

                Unread(next);
                CredentialScope scope = ReadTopLevelScope();
                Expect((byte)':');
                if (scope == CredentialScope.Other)
                {
                    SkipValue();
                }
                else
                {
                    string? token = ReadSessionObject(stopAfterToken: scope == CredentialScope.Preferred);
                    if (scope == CredentialScope.Preferred && token is not null)
                    {
                        return new Credential(token);
                    }

                    if (scope == CredentialScope.Legacy && token is not null)
                    {
                        if (legacy is not null)
                        {
                            throw Incomplete();
                        }

                        legacy = token;
                    }
                }

                int separator = ReadNonWhitespace();
                if (separator == '}')
                {
                    return Finish(legacy);
                }

                if (separator != ',')
                {
                    throw Incomplete();
                }
            }
        }

        private static Credential Finish(string? legacy) =>
            legacy is not null ? new Credential(legacy) : throw Incomplete();

        private CredentialScope ReadTopLevelScope()
        {
            Expect((byte)'"');
            int index = 0;
            bool preferredMatches = true;
            bool legacyMatches = true;
            while (true)
            {
                int value = ReadPropertyCharacter();
                if (value == -1)
                {
                    if (preferredMatches && index >= PreferredPrefix.Length)
                    {
                        return CredentialScope.Preferred;
                    }

                    return legacyMatches && index == LegacyScope.Length
                        ? CredentialScope.Legacy
                        : CredentialScope.Other;
                }

                if (index < PreferredPrefix.Length)
                {
                    if (value != PreferredPrefix[index])
                    {
                        preferredMatches = false;
                    }
                }
                else if (index == PreferredPrefix.Length && value is not ':' and not '/')
                {
                    preferredMatches = false;
                }

                if (index < LegacyScope.Length)
                {
                    if (value != LegacyScope[index])
                    {
                        legacyMatches = false;
                    }
                }
                else
                {
                    legacyMatches = false;
                }

                index++;
            }
        }

        private string? ReadSessionObject(bool stopAfterToken)
        {
            Expect((byte)'{');
            string? token = null;
            while (true)
            {
                int next = ReadNonWhitespace();
                if (next == '}')
                {
                    return token;
                }

                Unread(next);
                bool isToken = ReadSessionFieldIsToken();
                Expect((byte)':');
                if (isToken)
                {
                    if (ReadNonWhitespace() != '"')
                    {
                        throw Incomplete();
                    }

                    Unread('"');
                    string field = ReadExpectedString();
                    if (string.IsNullOrWhiteSpace(field) || token is not null)
                    {
                        throw Incomplete();
                    }

                    token = field;
                    if (stopAfterToken)
                    {
                        return token;
                    }
                }
                else
                {
                    SkipValue();
                }

                int separator = ReadNonWhitespace();
                if (separator == '}')
                {
                    return token;
                }

                if (separator != ',')
                {
                    throw Incomplete();
                }
            }
        }

        private bool ReadSessionFieldIsToken()
        {
            Expect((byte)'"');
            int length = 0;
            bool keyMatches = true;
            bool accessTokenMatches = true;
            while (true)
            {
                int value = ReadPropertyCharacter();
                if (value == -1)
                {
                    return (keyMatches && length == KeyProperty.Length) ||
                        (accessTokenMatches && length == AccessTokenProperty.Length);
                }

                if (value < 0 || length >= KeyProperty.Length || value != KeyProperty[length])
                {
                    keyMatches = false;
                }

                if (value < 0 || length >= AccessTokenProperty.Length || value != AccessTokenProperty[length])
                {
                    accessTokenMatches = false;
                }

                length++;
            }
        }

        private string ReadExpectedString()
        {
            byte[] encoded = ArrayPool<byte>.Shared.Rent(128);
            int length = 0;
            bool escaped = false;
            try
            {
                Expect((byte)'"');
                Append((byte)'"');
                while (true)
                {
                    int value = ReadByte();
                    if (value < 0)
                    {
                        throw Incomplete();
                    }

                    Append((byte)value);
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }

                    if (value == '\\')
                    {
                        escaped = true;
                        continue;
                    }

                    if (value == '"')
                    {
                        var reader = new Utf8JsonReader(encoded.AsSpan(0, length), isFinalBlock: true, state: default);
                        if (!reader.Read() || reader.TokenType != JsonTokenType.String || reader.GetString() is not string token)
                        {
                            throw Incomplete();
                        }

                        return token;
                    }

                    if (value < 0x20)
                    {
                        throw Incomplete();
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encoded);
                ArrayPool<byte>.Shared.Return(encoded);
            }

            void Append(byte value)
            {
                if (length == encoded.Length)
                {
                    byte[] expanded = ArrayPool<byte>.Shared.Rent(encoded.Length * 2);
                    Buffer.BlockCopy(encoded, 0, expanded, 0, length);
                    CryptographicOperations.ZeroMemory(encoded);
                    ArrayPool<byte>.Shared.Return(encoded);
                    encoded = expanded;
                }

                encoded[length++] = value;
            }
        }

        private void SkipValue(int depth = 0)
        {
            int first = ReadNonWhitespace();
            switch (first)
            {
                case '"':
                    SkipStringBody();
                    return;
                case '{':
                    if (depth >= MaxUnknownContainerDepth)
                    {
                        throw Incomplete();
                    }

                    SkipObject(depth + 1);
                    return;
                case '[':
                    if (depth >= MaxUnknownContainerDepth)
                    {
                        throw Incomplete();
                    }

                    SkipArray(depth + 1);
                    return;
                case 't':
                    ReadLiteral("rue"u8);
                    return;
                case 'f':
                    ReadLiteral("alse"u8);
                    return;
                case 'n':
                    ReadLiteral("ull"u8);
                    return;
                default:
                    if (first is '-' or >= '0' and <= '9')
                    {
                        SkipNumber();
                        return;
                    }

                    throw Incomplete();
            }
        }

        private void SkipObject(int depth)
        {
            int next = ReadNonWhitespace();
            if (next == '}')
            {
                return;
            }

            Unread(next);
            while (true)
            {
                SkipString();
                Expect((byte)':');
                SkipValue(depth);
                int separator = ReadNonWhitespace();
                if (separator == '}')
                {
                    return;
                }

                if (separator != ',')
                {
                    throw Incomplete();
                }
            }
        }

        private void SkipArray(int depth)
        {
            int next = ReadNonWhitespace();
            if (next == ']')
            {
                return;
            }

            Unread(next);
            while (true)
            {
                SkipValue(depth);
                int separator = ReadNonWhitespace();
                if (separator == ']')
                {
                    return;
                }

                if (separator != ',')
                {
                    throw Incomplete();
                }
            }
        }

        private void SkipString()
        {
            Expect((byte)'"');
            SkipStringBody();
        }

        private void SkipStringBody()
        {
            bool escaped = false;
            while (true)
            {
                int value = ReadByte();
                if (value < 0 || value < 0x20)
                {
                    throw Incomplete();
                }

                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (value == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (value == '"')
                {
                    return;
                }
            }
        }

        private void SkipNumber()
        {
            while (true)
            {
                int next = ReadByte();
                if (IsValueDelimiter(next))
                {
                    Unread(next);
                    return;
                }

                if (next < 0)
                {
                    throw Incomplete();
                }
            }
        }

        private void ReadLiteral(ReadOnlySpan<byte> literal)
        {
            foreach (byte expected in literal)
            {
                if (ReadByte() != expected)
                {
                    throw Incomplete();
                }
            }
        }

        private void Expect(byte expected)
        {
            if (ReadNonWhitespace() != expected)
            {
                throw Incomplete();
            }
        }

        private int ReadNonWhitespace()
        {
            int value;
            do
            {
                value = ReadByte();
            }
            while (value is ' ' or '\t' or '\r' or '\n');

            if (value < 0)
            {
                throw Incomplete();
            }

            return value;
        }

        private int ReadPropertyCharacter()
        {
            int value = ReadByte();
            if (value < 0 || value < 0x20)
            {
                throw Incomplete();
            }

            if (value == '"')
            {
                return -1;
            }

            if (value != '\\')
            {
                return value;
            }

            int escape = ReadByte();
            return escape switch
            {
                '"' => '"',
                '\\' => '\\',
                '/' => '/',
                'b' => '\b',
                'f' => '\f',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'u' => ReadUnicodeEscape(),
                _ => throw Incomplete()
            };
        }

        private int ReadUnicodeEscape()
        {
            int value = 0;
            for (int index = 0; index < 4; index++)
            {
                int digit = ReadByte();
                value = digit switch
                {
                    >= '0' and <= '9' => (value * 16) + digit - '0',
                    >= 'a' and <= 'f' => (value * 16) + digit - 'a' + 10,
                    >= 'A' and <= 'F' => (value * 16) + digit - 'A' + 10,
                    _ => throw Incomplete()
                };
            }

            return value <= byte.MaxValue ? value : -2;
        }

        private int ReadByte()
        {
            if (_lookahead >= 0)
            {
                int value = _lookahead;
                _lookahead = -1;
                return value;
            }

            return _stream.ReadByte();
        }

        private void Unread(int value)
        {
            if (value < 0 || _lookahead >= 0)
            {
                throw Incomplete();
            }

            _lookahead = value;
        }

        private static bool IsValueDelimiter(int value) =>
            value is ',' or '}' or ']' or ' ' or '\t' or '\r' or '\n';

        private static InvalidDataException Incomplete() => new("Grok credentials are incomplete.");
    }

    private enum CredentialScope { Other, Preferred, Legacy }
}
