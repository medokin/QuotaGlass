using System.Reflection;
using System.Text;
using QuotaGlass.Core;
using QuotaGlass.Providers;
using QuotaGlass.Tests.Support;

namespace QuotaGlass.Tests.Core;

public sealed class RollingFileLogTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();
    private readonly string _path;

    public RollingFileLogTests()
    {
        _path = Path.Combine(_directory.Path, "log.txt");
    }

    [Fact]
    public void Write_WhenLogExceedsOneMiB_RotatesOnceWithBoundedFiles()
    {
        // Break caught: closed metadata produces an unbounded current or rotated log file.
        var log = new RollingFileLog(_path);

        File.WriteAllText(_path, new string('x', 1_048_570));
        log.Write(LogArea.Provider, LogOutcome.Succeeded);

        Assert.True(new FileInfo(_path).Length <= 1_048_576);
        Assert.True(new FileInfo(Path.Combine(_directory.Path, "log.1.txt")).Length <= 1_048_576);
    }

    [Fact]
    public void Write_ClosedMetadataContractCannotAcceptIdentifierShapedStrings()
    {
        // Break caught: public metadata parameters regress to strings, allowing UUIDs or API keys to be supplied.
        MethodInfo write = typeof(RollingFileLog).GetMethod(nameof(RollingFileLog.Write))!;

        Assert.Equal(typeof(LogArea), write.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(LogOutcome), write.GetParameters()[1].ParameterType);
        Assert.Equal(typeof(ProviderFetchOutcome?), write.GetParameters()[5].ParameterType);
    }

    [Fact]
    public void Write_WithUndefinedArea_RejectsTheValueWithoutWritingACallerControlledToken()
    {
        // Break caught: a cast UUID/API-key-shaped enum value is serialized into the log.
        var log = new RollingFileLog(_path);

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => log.Write((LogArea)int.MaxValue, LogOutcome.Succeeded));

        Assert.Equal("area", exception.ParamName);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Write_WithUndefinedOutcome_RejectsTheValueWithoutWritingACallerControlledToken()
    {
        // Break caught: a cast UUID/API-key-shaped enum value is serialized into the log.
        var log = new RollingFileLog(_path);

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => log.Write(LogArea.Provider, (LogOutcome)int.MaxValue));

        Assert.Equal("outcome", exception.ParamName);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Write_EnumMetadataUsesStableLowercaseTokensAndSafeExceptionDetails()
    {
        // Break caught: enum serialization is unstable or logs exception message data.
        var log = new RollingFileLog(_path);

        log.Write(LogArea.Provider, LogOutcome.AuthExpired, 401, new InvalidOperationException("Bearer secret@example.com"));

        string contents = File.ReadAllText(_path);
        Assert.DoesNotContain("Bearer", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("secret@example.com", contents, StringComparison.Ordinal);
        Assert.Contains(" provider auth-expired status=401 exception=InvalidOperationException", contents, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_EnumMetadataProducesAValidCompleteUtf8Record()
    {
        // Break caught: record construction loses the timestamp, metadata suffix, or newline.
        var log = new RollingFileLog(_path);

        log.Write(LogArea.Platform, LogOutcome.TimedOut, 503, new InvalidOperationException());

        byte[] bytes = File.ReadAllBytes(_path);
        string contents = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        string timestamp = contents.Split(' ', 2)[0];

        Assert.True(bytes.Length <= 1_048_576);
        Assert.True(DateTimeOffset.TryParse(timestamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out _));
        Assert.EndsWith($" platform timed-out status=503 exception={nameof(InvalidOperationException)}{Environment.NewLine}", contents, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_ProviderDiagnosticsUseSanitizedScalarFields()
    {
        // Break caught: provider diagnostics omit transition data or include caller-controlled secrets.
        var log = new RollingFileLog(_path);

        log.Write(
            LogArea.Provider,
            LogOutcome.Failed,
            statusCode: 429,
            providerId: "codex",
            providerOutcome: ProviderFetchOutcome.RateLimited,
            cooldownSeconds: 300,
            consecutiveFailures: 2);

        string contents = File.ReadAllText(_path);
        Assert.Contains(
            " provider failed provider=codex fetch-outcome=rate-limited status=429 cooldown-seconds=300 consecutive-failures=2",
            contents,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Write_OpenCodeCommandFailureUsesOnlySanitizedMetadata()
    {
        // Break caught: command failures lose their cause or copy raw stderr into the application log.
        var log = new RollingFileLog(_path);
        var exception = new OpenCodeCommandException(
            OpenCodeCommandFailure.DatabaseBusy,
            17);

        log.Write(
            LogArea.Provider,
            LogOutcome.Failed,
            exception: exception,
            providerId: "opencode-company-seat",
            providerOutcome: ProviderFetchOutcome.TransientFailure);

        string contents = File.ReadAllText(_path);
        Assert.Contains(
            " command-failure=database-busy process-exit-code=17 exception=OpenCodeCommandException",
            contents,
            StringComparison.Ordinal);
        Assert.DoesNotContain("stderr", contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("account", contents, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("secret@example.com")]
    [InlineData("codex token")]
    [InlineData("")]
    public void Write_UnsafeProviderIdIsRejected(string providerId)
    {
        // Break caught: credentials or account identifiers can be passed through a free-form provider field.
        var log = new RollingFileLog(_path);

        Assert.Throws<ArgumentException>(() => log.Write(
            LogArea.Provider,
            LogOutcome.Failed,
            providerId: providerId,
            providerOutcome: ProviderFetchOutcome.TransientFailure));

        Assert.False(File.Exists(_path));
    }

    public void Dispose()
    {
        _directory.Dispose();
    }
}
