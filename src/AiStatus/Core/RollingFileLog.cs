using System.Text;
using System.IO;

namespace AiStatus.Core;

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

    public void Write(string area, string outcome, int? statusCode = null, Exception? exception = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(area);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        string line = FormatLine(area, outcome, statusCode, exception);
        byte[] bytes = Encoding.UTF8.GetBytes(line);
        if (bytes.Length > MaximumBytes)
        {
            Array.Resize(ref bytes, (int)MaximumBytes);
        }

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
        var line = new StringBuilder()
            .Append(DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(area)
            .Append(' ')
            .Append(outcome);

        if (statusCode is int code)
        {
            line.Append(" status=").Append(code);
        }

        if (exception is not null)
        {
            line.Append(" exception=").Append(exception.GetType().Name);
        }

        return line.AppendLine().ToString();
    }
}
