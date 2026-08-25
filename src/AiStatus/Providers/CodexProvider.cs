using System.Buffers;
using System.Collections.Immutable;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using AiStatus.Model;

namespace AiStatus.Providers;

public sealed class CodexProvider : IStatusProvider
{
    private static readonly Uri UsageUri = new("https://chatgpt.com/backend-api/wham/usage");
    private readonly string _credentialPath;
    private readonly HttpMessageHandler _handler;
    private readonly Func<double?, Severity> _severityFromPercent;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, Stream> _openCredential;

    public CodexProvider(
        string credentialPath,
        HttpMessageHandler handler,
        Func<double?, Severity> severityFromPercent,
        TimeProvider? timeProvider = null)
        : this(credentialPath, handler, severityFromPercent, timeProvider, OpenCredentialStream)
    {
    }

    internal CodexProvider(
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

    public string Id => "codex";

    public string Label => "Codex";

    internal static Stream OpenCredentialStream(string credentialPath) =>
        new FileStream(credentialPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan);

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset fetchedAt = _timeProvider.GetUtcNow();

        try
        {
            Credential credential = ReadCredential();
            using var client = new HttpClient(_handler, disposeHandler: false);
            using HttpResponseMessage response = await SendAsync(client, credential, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Snapshot(HealthState.AuthExpired, null, [], "re-auth: run codex login", fetchedAt);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Snapshot(HealthState.Degraded, null, [], "Codex usage request failed", fetchedAt);
            }

            if (!IsJson(response))
            {
                return Snapshot(HealthState.Degraded, null, [], "Codex usage endpoint returned non-JSON content", fetchedAt);
            }

            using JsonDocument usage = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            return Snapshot(
                HealthState.Ok,
                TryGetString(usage.RootElement, "plan_type"),
                ReadWindows(usage.RootElement),
                null,
                fetchedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Snapshot(HealthState.Degraded, null, [], "Codex status could not be read", fetchedAt);
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
        request.Headers.Add("chatgpt-account-id", credential.AccountId);
        return await client.SendAsync(request, cancellationToken);
    }

    private ImmutableArray<UsageWindow> ReadWindows(JsonElement root)
    {
        var windows = ImmutableArray.CreateBuilder<UsageWindow>();
        AddRateLimitWindows(TryGetObject(root, "rate_limit"), null, windows);

        if (TryGetProperty(root, "additional_rate_limits", out JsonElement additionalLimits) &&
            additionalLimits.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement additional in additionalLimits.EnumerateArray())
            {
                if (additional.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? name = TryGetString(additional, "limit_name");
                AddRateLimitWindows(TryGetObject(additional, "rate_limit"), name, windows);
            }
        }

        return windows.ToImmutable();
    }

    private void AddRateLimitWindows(
        JsonElement? rateLimit,
        string? prefix,
        ImmutableArray<UsageWindow>.Builder windows)
    {
        if (rateLimit is not JsonElement limit)
        {
            return;
        }

        bool limitReached = TryGetBool(limit, "limit_reached") == true;
        AddWindow(TryGetObject(limit, "primary_window"), prefix, limitReached, windows);
        AddWindow(TryGetObject(limit, "secondary_window"), prefix, limitReached, windows);
    }

    private void AddWindow(
        JsonElement? window,
        string? prefix,
        bool limitReached,
        ImmutableArray<UsageWindow>.Builder windows)
    {
        if (window is not JsonElement value || TryGetInt32(value, "limit_window_seconds") is not int seconds)
        {
            return;
        }

        double? percent = TryGetDouble(value, "used_percent");
        string duration = FormatWindow(seconds);
        windows.Add(new UsageWindow(
            prefix is null ? duration : $"{prefix} {duration}",
            percent,
            TryGetUnixSeconds(value, "reset_at"),
            limitReached ? Severity.Critical : _severityFromPercent(percent)));
    }

    private static string FormatWindow(int seconds) => seconds switch
    {
        18_000 => "5h",
        604_800 => "7d",
        _ when seconds % 86_400 == 0 => $"{seconds / 86_400}d",
        _ when seconds % 3_600 == 0 => $"{seconds / 3_600}h",
        _ => $"{seconds}s"
    };

    private static bool IsJson(HttpResponseMessage response) =>
        string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase);

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

    private static int? TryGetInt32(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out JsonElement value) && value.TryGetInt32(out int result)
            ? result
            : null;

    private static double? TryGetDouble(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out JsonElement value) && value.TryGetDouble(out double result)
            ? result
            : null;

    private static bool? TryGetBool(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static DateTimeOffset? TryGetUnixSeconds(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out JsonElement value) && value.TryGetInt64(out long seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    private static ProviderSnapshot Snapshot(
        HealthState health,
        string? planLabel,
        ImmutableArray<UsageWindow> windows,
        string? error,
        DateTimeOffset fetchedAt) =>
        new("codex", "Codex", health, planLabel, windows, [], error, fetchedAt, 0);

    private sealed record Credential(string AccessToken, string AccountId);

    private sealed class CredentialScanner(Stream stream)
    {
        private const int MaxUnknownContainerDepth = 64;
        private static readonly byte[] TokensProperty = "tokens"u8.ToArray();
        private static readonly byte[] AccessTokenProperty = "access_token"u8.ToArray();
        private static readonly byte[] AccountIdProperty = "account_id"u8.ToArray();
        private readonly Stream _stream = stream;
        private int _lookahead = -1;

        public Credential Read()
        {
            Expect((byte)'{');
            while (true)
            {
                int next = ReadNonWhitespace();
                if (next == '}')
                {
                    throw Incomplete();
                }

                Unread(next);
                bool isTokens = ReadPropertyNameMatches(TokensProperty);
                Expect((byte)':');
                if (isTokens)
                {
                    return ReadTokensObject();
                }

                SkipValue();
                ReadObjectSeparator();
            }
        }

        private Credential ReadTokensObject()
        {
            Expect((byte)'{');
            string? accessToken = null;
            string? accountId = null;

            while (true)
            {
                int next = ReadNonWhitespace();
                if (next == '}')
                {
                    throw Incomplete();
                }

                Unread(next);
                CredentialField property = ReadTokenProperty();
                Expect((byte)':');
                if (property is CredentialField.AccessToken or CredentialField.AccountId)
                {
                    if (ReadNonWhitespace() != '"')
                    {
                        throw Incomplete();
                    }

                    Unread('"');
                    string field = ReadExpectedString();
                    if (string.IsNullOrWhiteSpace(field))
                    {
                        throw Incomplete();
                    }

                    if (property == CredentialField.AccessToken)
                    {
                        if (accessToken is not null)
                        {
                            throw Incomplete();
                        }

                        accessToken = field;
                    }
                    else
                    {
                        if (accountId is not null)
                        {
                            throw Incomplete();
                        }

                        accountId = field;
                    }
                }
                else
                {
                    SkipValue();
                }

                if (accessToken is not null && accountId is not null)
                {
                    return new Credential(accessToken, accountId);
                }

                ReadObjectSeparator();
            }
        }

        private CredentialField ReadTokenProperty()
        {
            Expect((byte)'"');
            int length = 0;
            bool accessTokenMatches = true;
            bool accountIdMatches = true;
            while (true)
            {
                int value = ReadPropertyCharacter();
                if (value == -1)
                {
                    if (accessTokenMatches && length == AccessTokenProperty.Length)
                    {
                        return CredentialField.AccessToken;
                    }

                    return accountIdMatches && length == AccountIdProperty.Length
                        ? CredentialField.AccountId
                        : CredentialField.None;
                }

                if (value < 0 || length >= AccessTokenProperty.Length || value != AccessTokenProperty[length])
                {
                    accessTokenMatches = false;
                }

                if (value < 0 || length >= AccountIdProperty.Length || value != AccountIdProperty[length])
                {
                    accountIdMatches = false;
                }

                length++;
            }
        }

        private bool ReadPropertyNameMatches(ReadOnlySpan<byte> expected)
        {
            Expect((byte)'"');
            int index = 0;
            bool matches = true;
            while (true)
            {
                int value = ReadPropertyCharacter();
                if (value == -1)
                {
                    return matches && index == expected.Length;
                }

                if (value < 0 || index >= expected.Length || value != expected[index])
                {
                    matches = false;
                }

                index++;
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

        private void ReadObjectSeparator()
        {
            if (ReadNonWhitespace() != ',')
            {
                throw Incomplete();
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

        private static InvalidDataException Incomplete() => new("Codex credentials are incomplete.");
    }

    private enum CredentialField { None, AccessToken, AccountId }
}
