using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using QuotaGlass.Core;
using QuotaGlass.Model;

namespace QuotaGlass.Providers;

public sealed class OpenCodeGoProvider : IStatusProvider
{
    private static readonly Uri UsageUri = new("https://opencode.ai/zen/go/v1/usage");
    private readonly string _credentialPath;
    private readonly HttpMessageHandler _handler;
    private readonly Func<double?, Severity> _severityFromPercent;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, Stream> _openCredential;
    private readonly Func<OpenCodeConsoleSettings?> _consoleSettings;
    private readonly IOpenCodeConsoleAccountReader _consoleAccountReader;
    private readonly IOpenCodeConsoleGoClient _consoleClient;

    public OpenCodeGoProvider(
        string credentialPath,
        HttpMessageHandler handler,
        Func<double?, Severity> severityFromPercent,
        TimeProvider? timeProvider = null)
        : this(credentialPath, handler, severityFromPercent, timeProvider, OpenCredentialStream)
    {
    }

    internal OpenCodeGoProvider(
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
            () => null,
            new OpenCodeConsoleAccountReader(),
            new OpenCodeConsoleGoClient(handler, severityFromPercent, timeProvider))
    {
    }

    internal OpenCodeGoProvider(
        string credentialPath,
        HttpMessageHandler handler,
        Func<double?, Severity> severityFromPercent,
        Func<OpenCodeConsoleSettings?> consoleSettings,
        TimeProvider? timeProvider = null)
        : this(
            credentialPath,
            handler,
            severityFromPercent,
            timeProvider,
            OpenCredentialStream,
            consoleSettings,
            new OpenCodeConsoleAccountReader(),
            new OpenCodeConsoleGoClient(handler, severityFromPercent, timeProvider))
    {
    }

    internal OpenCodeGoProvider(
        string credentialPath,
        HttpMessageHandler handler,
        Func<double?, Severity> severityFromPercent,
        TimeProvider? timeProvider,
        Func<string, Stream> openCredential,
        Func<OpenCodeConsoleSettings?> consoleSettings,
        IOpenCodeConsoleAccountReader consoleAccountReader,
        IOpenCodeConsoleGoClient consoleClient)
    {
        _credentialPath = credentialPath;
        _handler = handler;
        _severityFromPercent = severityFromPercent;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _openCredential = openCredential;
        _consoleSettings = consoleSettings;
        _consoleAccountReader = consoleAccountReader;
        _consoleClient = consoleClient;
    }

    public string Id => "opencode-go";

    public string Label => "OpenCode Go";

    internal static Stream OpenCredentialStream(string credentialPath) => new FileStream(
        credentialPath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        bufferSize: 1,
        FileOptions.SequentialScan);

    public async Task<ProviderFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset fetchedAt = _timeProvider.GetUtcNow();
        string? apiKey;
        try
        {
            apiKey = ReadApiKey();
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            apiKey = null;
        }

        if (apiKey is not null)
        {
            return await FetchApiKeyAsync(apiKey, fetchedAt, cancellationToken).ConfigureAwait(false);
        }

        OpenCodeConsoleSettings? consoleSettings = _consoleSettings();
        return consoleSettings?.Enabled == true
            ? await FetchConsoleAsync(consoleSettings, fetchedAt, cancellationToken).ConfigureAwait(false)
            : NotConfigured(fetchedAt);
    }

    private async Task<ProviderFetchResult> FetchApiKeyAsync(
        string apiKey,
        DateTimeOffset fetchedAt,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient(_handler, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        using HttpResponseMessage response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new ProviderFetchResult(
                ProviderFetchOutcome.AuthenticationRequired,
                Snapshot(HealthState.AuthExpired, [], "re-auth: run opencode auth login", fetchedAt),
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

        JsonDocument document;
        try
        {
            document = await ProviderHttpSafety.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return new ProviderFetchResult(
                ProviderFetchOutcome.InvalidResponse,
                statusCode: response.StatusCode);
        }

        using (document)
        {
            ImmutableArray<UsageWindow> windows = ReadWindows(document.RootElement);
            return windows.IsEmpty
                ? new ProviderFetchResult(ProviderFetchOutcome.InvalidResponse, statusCode: response.StatusCode)
                : new ProviderFetchResult(
                    ProviderFetchOutcome.Success,
                    Snapshot(HealthState.Ok, windows, null, fetchedAt),
                    response.StatusCode);
        }
    }

    private async Task<ProviderFetchResult> FetchConsoleAsync(
        OpenCodeConsoleSettings settings,
        DateTimeOffset fetchedAt,
        CancellationToken cancellationToken)
    {
        ImmutableArray<OpenCodeConsoleAccount> discovered;
        try
        {
            discovered = await _consoleAccountReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return new ProviderFetchResult(ProviderFetchOutcome.InvalidResponse);
        }

        if (discovered.IsEmpty)
        {
            return NotConfigured(fetchedAt);
        }

        ImmutableArray<OpenCodeConsoleAccount> current = discovered
            .Where(account => account.ExpiresAt is null || account.ExpiresAt > fetchedAt)
            .ToImmutableArray();
        if (current.IsEmpty)
        {
            return ConsoleAuthenticationRequired(fetchedAt);
        }

        OpenCodeConsoleFetchResult result = await _consoleClient
            .FetchAsync(current, cancellationToken, settings.WorkspaceSelector)
            .ConfigureAwait(false);
        if (result.Outcome != OpenCodeConsoleFetchOutcome.Success)
        {
            return MapConsoleFailure(result, fetchedAt);
        }

        if (result.Workspaces.IsEmpty)
        {
            return settings.WorkspaceSelector is null
                ? NotConfigured(fetchedAt)
                : SelectionRequired([], fetchedAt);
        }

        OpenCodeConsoleWorkspace? selected;
        if (settings.WorkspaceSelector is string selector)
        {
            selected = result.Workspaces.FirstOrDefault(workspace =>
                string.Equals(workspace.Selector, selector, StringComparison.Ordinal));
            if (selected is null)
            {
                return SelectionRequired(result.Workspaces, fetchedAt);
            }
        }
        else if (result.Workspaces.Length == 1)
        {
            selected = result.Workspaces[0];
        }
        else
        {
            return SelectionRequired(result.Workspaces, fetchedAt);
        }

        return new ProviderFetchResult(
            ProviderFetchOutcome.Success,
            Snapshot(HealthState.Ok, selected.Windows, null, fetchedAt),
            result.StatusCode);
    }

    private static ProviderFetchResult MapConsoleFailure(
        OpenCodeConsoleFetchResult result,
        DateTimeOffset fetchedAt) => result.Outcome switch
        {
            OpenCodeConsoleFetchOutcome.AuthenticationRequired => ConsoleAuthenticationRequired(
                fetchedAt,
                result.StatusCode),
            OpenCodeConsoleFetchOutcome.RateLimited => new ProviderFetchResult(
                ProviderFetchOutcome.RateLimited,
                statusCode: result.StatusCode,
                retryAfter: result.RetryAfter),
            OpenCodeConsoleFetchOutcome.InvalidResponse => new ProviderFetchResult(
                ProviderFetchOutcome.InvalidResponse,
                statusCode: result.StatusCode),
            _ => new ProviderFetchResult(
                ProviderFetchOutcome.TransientFailure,
                statusCode: result.StatusCode),
        };

    private static ProviderFetchResult ConsoleAuthenticationRequired(
        DateTimeOffset fetchedAt,
        HttpStatusCode? statusCode = null) => new(
        ProviderFetchOutcome.AuthenticationRequired,
        Snapshot(HealthState.AuthExpired, [], "re-auth: run opencode console login", fetchedAt),
        statusCode);

    private static ProviderFetchResult SelectionRequired(
        ImmutableArray<OpenCodeConsoleWorkspace> workspaces,
        DateTimeOffset fetchedAt)
    {
        const string settingPath = "Providers.opencode-go.OpenCodeConsole.WorkspaceSelector";
        if (workspaces.IsEmpty)
        {
            return new ProviderFetchResult(
                ProviderFetchOutcome.NotConfigured,
                Snapshot(
                    HealthState.Unreachable,
                    [],
                    $"configured {settingPath} is not available",
                    fetchedAt));
        }

        string selectors = string.Join(", ", workspaces.Select(workspace => workspace.Selector));
        return new ProviderFetchResult(
            ProviderFetchOutcome.NotConfigured,
            Snapshot(
                HealthState.Unreachable,
                [],
                $"set {settingPath} to one of: {selectors}",
                fetchedAt));
    }

    private string? ReadApiKey()
    {
        using Stream stream = _openCredential(_credentialPath);
        return new CredentialScanner(stream).Read();
    }

    private ImmutableArray<UsageWindow> ReadWindows(JsonElement root)
    {
        var windows = ImmutableArray.CreateBuilder<UsageWindow>();
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("usage", out JsonElement usage) ||
            usage.ValueKind != JsonValueKind.Object)
        {
            return windows.ToImmutable();
        }

        AddWindow(usage, "rolling", windows);
        AddWindow(usage, "weekly", windows);
        AddWindow(usage, "monthly", windows);
        return windows.ToImmutable();
    }

    private void AddWindow(
        JsonElement usage,
        string name,
        ImmutableArray<UsageWindow>.Builder windows)
    {
        if (!usage.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("percent", out JsonElement percentElement) ||
            percentElement.ValueKind != JsonValueKind.Number ||
            !percentElement.TryGetDouble(out double percent) ||
            !double.IsFinite(percent) ||
            !value.TryGetProperty("resetsAt", out JsonElement resetsAtElement) ||
            resetsAtElement.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(
                resetsAtElement.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset resetsAt))
        {
            return;
        }

        windows.Add(new UsageWindow(name, percent, resetsAt, _severityFromPercent(percent)));
    }

    private static ProviderFetchResult NotConfigured(DateTimeOffset fetchedAt) => new(
        ProviderFetchOutcome.NotConfigured,
        Snapshot(HealthState.Unreachable, [], null, fetchedAt));

    private static ProviderSnapshot Snapshot(
        HealthState health,
        ImmutableArray<UsageWindow> windows,
        string? error,
        DateTimeOffset fetchedAt) =>
        new("opencode-go", "OpenCode Go", health, null, windows, [], error, fetchedAt, 0);

    private sealed class CredentialScanner(Stream stream)
    {
        private const int MaximumContainerDepth = 64;
        private const int MaximumStringCharacters = 16_384;
        private readonly Stream _stream = stream;
        private int _lookahead = -1;

        public string? Read()
        {
            Expect((byte)'{');
            int next = ReadNonWhitespace();
            if (next == '}')
            {
                return null;
            }

            Unread(next);
            while (true)
            {
                string propertyName = ReadString();
                Expect((byte)':');
                string? result = string.Equals(propertyName, "opencode-go", StringComparison.Ordinal)
                    ? ReadProvider()
                    : SkipAndReturnNull();
                if (result is not null)
                {
                    return result;
                }

                int separator = ReadNonWhitespace();
                if (separator == '}')
                {
                    return null;
                }

                if (separator != ',')
                {
                    throw Incomplete();
                }
            }
        }

        private string? ReadProvider()
        {
            Expect((byte)'{');
            string? type = null;
            string? key = null;
            int next = ReadNonWhitespace();
            if (next == '}')
            {
                return null;
            }

            Unread(next);
            while (true)
            {
                string propertyName = ReadString();
                Expect((byte)':');
                if (string.Equals(propertyName, "type", StringComparison.Ordinal))
                {
                    type = ReadString();
                }
                else if (string.Equals(propertyName, "key", StringComparison.Ordinal))
                {
                    key = ReadString();
                }
                else
                {
                    SkipValue(0);
                }

                int separator = ReadNonWhitespace();
                if (separator is not ',' and not '}')
                {
                    throw Incomplete();
                }

                if (string.Equals(type, "api", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(key))
                {
                    return key;
                }

                if (separator == '}')
                {
                    return null;
                }
            }
        }

        private string? SkipAndReturnNull()
        {
            SkipValue(0);
            return null;
        }

        private string ReadString()
        {
            Expect((byte)'"');
            var value = new StringBuilder();
            while (true)
            {
                int character = ReadByte();
                if (character < 0 || character < 0x20)
                {
                    throw Incomplete();
                }

                if (character == '"')
                {
                    return value.ToString();
                }

                if (character == '\\')
                {
                    character = ReadEscapedCharacter();
                }

                if (value.Length >= MaximumStringCharacters)
                {
                    throw Incomplete();
                }

                value.Append((char)character);
            }
        }

        private int ReadEscapedCharacter()
        {
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
                _ => throw Incomplete(),
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
                    _ => throw Incomplete(),
                };
            }

            return value;
        }

        private void SkipValue(int depth)
        {
            int first = ReadNonWhitespace();
            switch (first)
            {
                case '"':
                    SkipStringBody();
                    return;
                case '{':
                    EnsureDepth(depth);
                    SkipObject(depth + 1);
                    return;
                case '[':
                    EnsureDepth(depth);
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
                _ = ReadString();
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
                }
                else if (value == '\\')
                {
                    escaped = true;
                }
                else if (value == '"')
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
                if (next is ',' or '}' or ']' or ' ' or '\t' or '\r' or '\n')
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

        private static void EnsureDepth(int depth)
        {
            if (depth >= MaximumContainerDepth)
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

        private static InvalidDataException Incomplete() =>
            new("OpenCode Go credentials are incomplete or invalid.");
    }
}
