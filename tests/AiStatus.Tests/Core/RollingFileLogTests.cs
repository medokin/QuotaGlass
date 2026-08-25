using AiStatus.Core;
using AiStatus.Tests.Support;
using System.Text;

namespace AiStatus.Tests.Core;

public sealed class RollingFileLogTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();
    private readonly string _path;

    public RollingFileLogTests()
    {
        _path = Path.Combine(_directory.Path, "log.txt");
    }

    [Fact]
    public void Write_WhenNextLineExceedsOneMiB_RotatesOnceWithBoundedFiles()
    {
        // Break caught: appending past the rolling size limit or retaining more than one oversized generation.
        var log = new RollingFileLog(_path);
        string outcome = new('x', 1_000);

        for (int index = 0; index < 1_100; index++)
        {
            log.Write("provider", outcome);
        }

        Assert.True(new FileInfo(_path).Length <= 1_048_576);
        Assert.True(new FileInfo(Path.Combine(_directory.Path, "log.1.txt")).Length <= 1_048_576);
    }

    [Fact]
    public void Write_WhenSingleLineExceedsOneMiB_KeepsTheLogWithinTheLimit()
    {
        // Break caught: a single unusually large outcome bypasses the rolling size limit.
        var log = new RollingFileLog(_path);

        log.Write("provider", new string('x', 1_048_576));

        Assert.True(new FileInfo(_path).Length <= 1_048_576);
    }

    [Fact]
    public void Write_WithSensitiveException_RecordsOnlyItsType()
    {
        // Break caught: logging exception message or stack data that can expose credentials or identifiers.
        var log = new RollingFileLog(_path);

        log.Write("provider", "failed", exception: new InvalidOperationException("Bearer secret@example.com"));

        string contents = File.ReadAllText(_path);
        Assert.DoesNotContain("Bearer", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("secret@example.com", contents, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), contents, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_WithUnsafeArea_RedactsTheEntireArea()
    {
        // Break caught: callers can place a bearer credential or email address directly in the area field.
        var log = new RollingFileLog(_path);
        const string unsafeArea = "Bearer secret@example.com";

        log.Write(unsafeArea, "completed");

        string contents = File.ReadAllText(_path);
        Assert.DoesNotContain("Bearer", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("secret@example.com", contents, StringComparison.Ordinal);
        Assert.Contains("[redacted-area]", contents, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_WithUnsafeOutcome_RedactsTheEntireOutcome()
    {
        // Break caught: callers can place a bearer credential or email address directly in the outcome field.
        var log = new RollingFileLog(_path);
        const string unsafeOutcome = "Bearer secret@example.com";

        log.Write("provider", unsafeOutcome);

        string contents = File.ReadAllText(_path);
        Assert.DoesNotContain("Bearer", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("secret@example.com", contents, StringComparison.Ordinal);
        Assert.Contains("[redacted-outcome]", contents, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_WithNonTokenOutcome_RedactsTheEntireOutcome()
    {
        // Break caught: free-form outcome text can carry an arbitrary identifier without matching a secret keyword.
        var log = new RollingFileLog(_path);

        log.Write("provider", "request completed");

        string contents = File.ReadAllText(_path);
        Assert.DoesNotContain("request completed", contents, StringComparison.Ordinal);
        Assert.Contains("[redacted-outcome]", contents, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_WithNewlineInMetadata_RedactsItWithoutForgingAnotherRecord()
    {
        // Break caught: a metadata newline forges a second log record.
        var log = new RollingFileLog(_path);

        log.Write("provider", "completed\r\nforged-record");

        string[] lines = File.ReadAllLines(_path);
        Assert.Single(lines);
        Assert.DoesNotContain("forged-record", lines[0], StringComparison.Ordinal);
        Assert.Contains("[redacted-outcome]", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Write_WithOversizedMultibyteOutcome_PreservesAValidCompleteRecord()
    {
        // Break caught: raw-byte truncation corrupts UTF-8 or drops the required metadata suffix and newline.
        var log = new RollingFileLog(_path);

        log.Write("provider", new string('界', 400_000), 503, new InvalidOperationException());

        byte[] bytes = File.ReadAllBytes(_path);
        string contents = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        string timestamp = contents.Split(' ', 2)[0];

        Assert.True(bytes.Length <= 1_048_576);
        Assert.True(DateTimeOffset.TryParse(timestamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out _));
        Assert.Contains(" provider ", contents, StringComparison.Ordinal);
        Assert.EndsWith($" status=503 exception={nameof(InvalidOperationException)}{Environment.NewLine}", contents, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _directory.Dispose();
    }
}
