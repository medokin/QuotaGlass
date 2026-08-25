using AiStatus.Core;
using AiStatus.Tests.Support;

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

    public void Dispose()
    {
        _directory.Dispose();
    }
}
