using System.Text;
using System.IO;

namespace AiStatus.Core;

public sealed class RollingFileLog
{
    private const long MaximumBytes = 1_048_576;
    private const int MaximumAreaLength = 64;
    private const string RedactedArea = "[redacted-area]";
    private const string RedactedOutcome = "[redacted-outcome]";
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

    public void Write(string area, string outcome, int? statusCode = null, Exception? exception = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(area);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        string line = FormatLine(SanitizeArea(area), SanitizeOutcome(outcome), statusCode, exception);
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

    private static string FormatLine(string area, string outcome, int? statusCode, Exception? exception)
    {
        string prefix = new StringBuilder()
            .Append(DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(area)
            .Append(' ')
            .ToString();
        var suffix = new StringBuilder();

        if (statusCode is int code)
        {
            suffix.Append(" status=").Append(code);
        }

        if (exception is not null)
        {
            suffix.Append(" exception=").Append(exception.GetType().Name);
        }

        suffix.AppendLine();
        int availableOutcomeBytes = checked((int)(MaximumBytes - Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(suffix.ToString())));
        return prefix + TruncateOnRuneBoundary(outcome, availableOutcomeBytes) + suffix;
    }

    private static string SanitizeArea(string area)
    {
        if (area.Length > MaximumAreaLength || area.Any(character => !IsAreaCharacter(character)))
        {
            return RedactedArea;
        }

        return area;
    }

    private static string SanitizeOutcome(string outcome)
    {
        return ContainsUnsafeOutcomeContent(outcome) || !IsSafeOutcomeToken(outcome) ? RedactedOutcome : outcome;
    }

    private static bool IsAreaCharacter(char character)
    {
        return char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-';
    }

    private static bool ContainsUnsafeOutcomeContent(string outcome)
    {
        return outcome.IndexOfAny(['\r', '\n']) >= 0 ||
            outcome.Contains('@', StringComparison.Ordinal) ||
            outcome.Contains("bearer", StringComparison.OrdinalIgnoreCase) ||
            outcome.Contains("token", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeOutcomeToken(string outcome)
    {
        return outcome.EnumerateRunes().All(rune => Rune.IsLetterOrDigit(rune) || rune.Value is '.' or '_' or '-');
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
}
