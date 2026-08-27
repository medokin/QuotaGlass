using System.Text;
using System.IO;
using QuotaGlass.Providers;

namespace QuotaGlass.Core;

public enum LogArea
{
    Application,
    Settings,
    Poller,
    Provider,
    Platform,
    Ui,
}

public enum LogOutcome
{
    Started,
    Succeeded,
    Failed,
    TimedOut,
    Degraded,
    AuthExpired,
    Unreachable,
    Disabled,
    Changed,
    Invalid,
    Registered,
    Unregistered,
}

public sealed class RollingFileLog
{
    private const long MaximumBytes = 1_048_576;
    private readonly object _gate = new();
    private readonly string _path;

    public RollingFileLog(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(_path)
            ?? throw new ArgumentException("The log path must include a directory.", nameof(path));
        Directory.CreateDirectory(directory);
    }

    public void Write(
        LogArea area,
        LogOutcome outcome,
        int? statusCode = null,
        Exception? exception = null,
        string? providerId = null,
        ProviderFetchOutcome? providerOutcome = null,
        int? cooldownSeconds = null,
        int? consecutiveFailures = null)
    {
        if (!Enum.IsDefined(area))
        {
            throw new ArgumentOutOfRangeException(nameof(area));
        }

        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if (providerId is not null && !IsSafeProviderId(providerId))
        {
            throw new ArgumentException("Provider ID must be a safe lowercase token.", nameof(providerId));
        }

        if (providerOutcome is ProviderFetchOutcome fetchOutcome && !Enum.IsDefined(fetchOutcome))
        {
            throw new ArgumentOutOfRangeException(nameof(providerOutcome));
        }

        if (cooldownSeconds is < 0 or > 3600)
        {
            throw new ArgumentOutOfRangeException(nameof(cooldownSeconds));
        }

        if (consecutiveFailures is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(consecutiveFailures));
        }

        string line = FormatLine(
            ToToken(area),
            ToToken(outcome),
            statusCode,
            exception,
            providerId,
            providerOutcome,
            cooldownSeconds,
            consecutiveFailures);
        byte[] bytes = Encoding.UTF8.GetBytes(line);

        lock (_gate)
        {
            long existingLength = File.Exists(_path) ? new FileInfo(_path).Length : 0;
            if (existingLength > 0 && existingLength + bytes.Length > MaximumBytes)
            {
                File.Move(_path, GetRotatedPath(), overwrite: true);
            }

            using FileStream stream = new(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
    }

    private string GetRotatedPath()
    {
        string directory = Path.GetDirectoryName(_path)!;
        string name = Path.GetFileNameWithoutExtension(_path);
        string extension = Path.GetExtension(_path);
        return Path.Combine(directory, name + ".1" + extension);
    }

    private static string FormatLine(
        string area,
        string outcome,
        int? statusCode,
        Exception? exception,
        string? providerId,
        ProviderFetchOutcome? providerOutcome,
        int? cooldownSeconds,
        int? consecutiveFailures)
    {
        string prefix = new StringBuilder()
            .Append(DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(area)
            .Append(' ')
            .ToString();
        var suffix = new StringBuilder();

        if (providerId is not null)
        {
            suffix.Append(" provider=").Append(providerId);
        }

        if (providerOutcome is ProviderFetchOutcome fetchOutcome)
        {
            suffix.Append(" fetch-outcome=").Append(ToToken(fetchOutcome));
        }

        if (statusCode is int code)
        {
            suffix.Append(" status=").Append(code);
        }

        if (cooldownSeconds is int seconds)
        {
            suffix.Append(" cooldown-seconds=").Append(seconds);
        }

        if (consecutiveFailures is int failures)
        {
            suffix.Append(" consecutive-failures=").Append(failures);
        }

        if (exception is OpenCodeCommandException commandException)
        {
            suffix
                .Append(" command-failure=")
                .Append(ToToken(commandException.Failure))
                .Append(" process-exit-code=")
                .Append(commandException.ExitCode);
        }

        if (exception is not null)
        {
            suffix.Append(" exception=").Append(exception.GetType().Name);
        }

        suffix.AppendLine();
        int availableOutcomeBytes = checked((int)(MaximumBytes - Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(suffix.ToString())));
        return prefix + TruncateOnRuneBoundary(outcome, availableOutcomeBytes) + suffix;
    }

    private static string TruncateOnRuneBoundary(string value, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes)
        {
            return value;
        }

        var truncated = new StringBuilder();
        int usedBytes = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            int runeBytes = rune.Utf8SequenceLength;
            if (usedBytes + runeBytes > maximumBytes)
            {
                break;
            }

            truncated.Append(rune.ToString());
            usedBytes += runeBytes;
        }

        return truncated.ToString();
    }

    private static string ToToken(LogArea area)
    {
        return area switch
        {
            LogArea.Application => "application",
            LogArea.Settings => "settings",
            LogArea.Poller => "poller",
            LogArea.Provider => "provider",
            LogArea.Platform => "platform",
            LogArea.Ui => "ui",
            _ => throw new InvalidOperationException("Validated log area was not mapped."),
        };
    }

    private static string ToToken(LogOutcome outcome)
    {
        return outcome switch
        {
            LogOutcome.Started => "started",
            LogOutcome.Succeeded => "succeeded",
            LogOutcome.Failed => "failed",
            LogOutcome.TimedOut => "timed-out",
            LogOutcome.Degraded => "degraded",
            LogOutcome.AuthExpired => "auth-expired",
            LogOutcome.Unreachable => "unreachable",
            LogOutcome.Disabled => "disabled",
            LogOutcome.Changed => "changed",
            LogOutcome.Invalid => "invalid",
            LogOutcome.Registered => "registered",
            LogOutcome.Unregistered => "unregistered",
            _ => throw new InvalidOperationException("Validated log outcome was not mapped."),
        };
    }

    private static string ToToken(ProviderFetchOutcome outcome) => outcome switch
    {
        ProviderFetchOutcome.Success => "success",
        ProviderFetchOutcome.PartialSuccess => "partial-success",
        ProviderFetchOutcome.NotConfigured => "not-configured",
        ProviderFetchOutcome.AuthenticationRequired => "authentication-required",
        ProviderFetchOutcome.TransientFailure => "transient-failure",
        ProviderFetchOutcome.RateLimited => "rate-limited",
        ProviderFetchOutcome.InvalidResponse => "invalid-response",
        _ => throw new InvalidOperationException("Validated provider outcome was not mapped."),
    };

    private static string ToToken(OpenCodeCommandFailure failure) => failure switch
    {
        OpenCodeCommandFailure.DatabaseBusy => "database-busy",
        OpenCodeCommandFailure.TimedOut => "timed-out",
        OpenCodeCommandFailure.CommandNotFound => "command-not-found",
        OpenCodeCommandFailure.Failed => "failed",
        _ => throw new InvalidOperationException("Validated command failure was not mapped."),
    };

    private static bool IsSafeProviderId(string providerId) =>
        providerId.Length is > 0 and <= 64 &&
        providerId.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}
