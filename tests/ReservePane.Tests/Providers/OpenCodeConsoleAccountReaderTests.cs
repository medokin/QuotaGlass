using System.Collections.Immutable;
using System.Text;
using ReservePane.Core;
using ReservePane.Providers;
using ReservePane.Tests.Support;

namespace ReservePane.Tests.Providers;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class OpenCodeConsoleAccountReaderTests
{
    [Fact]
    public void CreateStartInfo_UsesTheWindowsCommandShimWithFixedArgumentOrder()
    {
        // Catches CreateProcess failing to resolve Volta's extensionless and .cmd OpenCode shims.
        const string query = "select 1 as ok;";

        System.Diagnostics.ProcessStartInfo startInfo =
            OpenCodeConsoleAccountReader.CreateStartInfo(query);

        Assert.EndsWith("cmd.exe", startInfo.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(
            ["/d", "/c", "opencode", "db", query, "--format", "json"],
            startInfo.ArgumentList);
    }

    [Fact]
    public void CreateStartInfo_CapturesCommandSearchEnvironmentForTheFetchAttempt()
    {
        // Catches cmd.exe inheriting a later or stale process PATH instead of the fetch snapshot.
        const string refreshedPath = @"C:\refreshed-tools;C:\windows-tools";
        const string refreshedPathExtensions = ".EXE;.CMD";
        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        string? originalPathExtensions = Environment.GetEnvironmentVariable("PATHEXT");
        System.Diagnostics.ProcessStartInfo startInfo;

        try
        {
            Environment.SetEnvironmentVariable("PATH", refreshedPath);
            Environment.SetEnvironmentVariable("PATHEXT", refreshedPathExtensions);
            startInfo = OpenCodeConsoleAccountReader.CreateStartInfo("select 1;");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("PATHEXT", originalPathExtensions);
        }

        Assert.StartsWith(
            refreshedPath,
            startInfo.Environment["PATH"],
            StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(
            refreshedPathExtensions,
            startInfo.Environment["PATHEXT"],
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateStartInfo_AppliesTheRefreshedEffectiveCommandEnvironment()
    {
        // Catches discovery and cmd.exe execution using different PATH or PATHEXT values.
        string? ReadVariable(string name, EnvironmentVariableTarget target) => (name, target) switch
        {
            ("PATH", EnvironmentVariableTarget.Process) => @"C:\process-tools",
            ("PATH", EnvironmentVariableTarget.User) => @"C:\user-tools",
            ("PATH", EnvironmentVariableTarget.Machine) => @"C:\machine-tools",
            ("PATHEXT", EnvironmentVariableTarget.Process) => ".EXE",
            ("PATHEXT", EnvironmentVariableTarget.User) => ".CMD",
            ("PATHEXT", EnvironmentVariableTarget.Machine) => ".BAT",
            _ => null,
        };
        EffectiveCommandEnvironment environment = EffectiveCommandEnvironment.Capture(ReadVariable);

        System.Diagnostics.ProcessStartInfo startInfo =
            OpenCodeConsoleAccountReader.CreateStartInfo("select 1;", environment);

        Assert.Equal(
            @"C:\process-tools;C:\user-tools;C:\machine-tools",
            startInfo.Environment["PATH"]);
        Assert.Equal(".EXE;.CMD;.BAT", startInfo.Environment["PATHEXT"]);
    }

    [Fact]
    public async Task ReadAsync_SelectsOnlyRequiredFieldsAndMapsAccounts()
    {
        // Catches credential discovery selecting unrelated identity or refresh-token data.
        string? capturedQuery = null;
        var reader = new OpenCodeConsoleAccountReader((query, _) =>
        {
            capturedQuery = query;
            return Task.FromResult<byte[]?>(Encoding.UTF8.GetBytes(
                """
                [{"id":"account_test","url":"https://opencode.ai/console","access_token":"access-test","token_expiry":1787832000000}]
                """));
        });

        ImmutableArray<OpenCodeConsoleAccount> accounts = await reader.ReadAsync(CancellationToken.None);

        OpenCodeConsoleAccount account = Assert.Single(accounts);
        Assert.Equal("account_test", account.AccountId);
        Assert.Equal("access-test", account.AccessToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1787832000000), account.ExpiresAt);
        Assert.NotNull(capturedQuery);
        Assert.DoesNotContain("email", capturedQuery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refresh_token", capturedQuery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("active_org_id", capturedQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "where a.url = 'https://opencode.ai/console'",
            capturedQuery,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_MissingCommandReturnsNoAccounts()
    {
        // Catches a missing OpenCode installation becoming an alert or exception.
        var reader = new OpenCodeConsoleAccountReader((_, _) => Task.FromResult<byte[]?>(null));

        ImmutableArray<OpenCodeConsoleAccount> accounts = await reader.ReadAsync(CancellationToken.None);

        Assert.Empty(accounts);
    }

    [Theory]
    [InlineData("http://opencode.ai/console")]
    [InlineData("https://example.test/console")]
    [InlineData("https://opencode.ai/other")]
    public async Task ReadAsync_NonConsoleAccountUrlIsIgnored(string url)
    {
        // Catches a database-controlled URL receiving the Console access token.
        string json = $$"""
            [{"id":"account_test","url":"{{url}}","access_token":"access-test","token_expiry":null}]
            """;
        var reader = Reader(json);

        ImmutableArray<OpenCodeConsoleAccount> accounts = await reader.ReadAsync(CancellationToken.None);

        Assert.Empty(accounts);
    }

    [Fact]
    public async Task ReadAsync_RejectsMoreThanMaximumAccounts()
    {
        // Catches unbounded account discovery caused by corrupt or hostile local data.
        string rows = string.Join(
            ',',
            Enumerable.Range(0, 33).Select(index =>
                $"{{\"id\":\"account_{index}\",\"url\":\"https://opencode.ai/console\",\"access_token\":\"access-{index}\",\"token_expiry\":null}}"));
        var reader = Reader($"[{rows}]");

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => reader.ReadAsync(CancellationToken.None));

        Assert.DoesNotContain("access-", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_RejectsOversizedOrMalformedOutputWithoutLeakingIt()
    {
        // Catches child-process output bypassing bounds or escaping through parse errors.
        byte[] oversized = Encoding.UTF8.GetBytes(new string('x', 1_048_577));
        var oversizedReader = new OpenCodeConsoleAccountReader((_, _) => Task.FromResult<byte[]?>(oversized));
        var malformedReader = Reader("[{\"access_token\":\"sensitive-test-token\"");

        InvalidDataException oversizedError = await Assert.ThrowsAsync<InvalidDataException>(
            () => oversizedReader.ReadAsync(CancellationToken.None));
        InvalidDataException malformedError = await Assert.ThrowsAsync<InvalidDataException>(
            () => malformedReader.ReadAsync(CancellationToken.None));

        Assert.DoesNotContain("sensitive", oversizedError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sensitive-test-token", malformedError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_CallerCancellationPropagates()
    {
        // Catches the child command outliving the provider timeout or application shutdown.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new OpenCodeConsoleAccountReader(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        });
        using var cancellation = new CancellationTokenSource();

        Task<ImmutableArray<OpenCodeConsoleAccount>> read = reader.ReadAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    [Fact]
    public async Task RunQueryAsync_NonZeroExitReportsSanitizedFailureMetadata()
    {
        // Break caught: a local OpenCode command failure is collapsed to an unexplained transient result.
        using var directory = new TemporaryDirectory();
        string commandPath = Path.Combine(directory.Path, "opencode.cmd");
        await File.WriteAllTextAsync(
            commandPath,
            "@echo database is locked: sensitive-account-id 1>&2\r\n@exit /b 17\r\n");
        var environment = new EffectiveCommandEnvironment(directory.Path, ".CMD");

        OpenCodeCommandException exception = await Assert.ThrowsAsync<OpenCodeCommandException>(
            () => OpenCodeConsoleAccountReader.RunQueryAsync(
                "select 1;",
                environment,
                CancellationToken.None));

        Assert.Equal(OpenCodeCommandFailure.DatabaseBusy, exception.Failure);
        Assert.Equal(17, exception.ExitCode);
        Assert.DoesNotContain("sensitive-account-id", exception.Message, StringComparison.Ordinal);

        string logPath = Path.Combine(directory.Path, "diagnostic.log");
        var log = new RollingFileLog(logPath);
        log.Write(
            LogArea.Provider,
            LogOutcome.Failed,
            exception: exception,
            providerId: "opencode-company-seat",
            providerOutcome: ProviderFetchOutcome.TransientFailure);
        string contents = await File.ReadAllTextAsync(logPath);

        Assert.Contains("command-failure=database-busy", contents, StringComparison.Ordinal);
        Assert.Contains("process-exit-code=17", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-account-id", contents, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("database is busy", "DatabaseBusy")]
    [InlineData("operation timed out", "TimedOut")]
    [InlineData("opencode is not recognized", "CommandNotFound")]
    [InlineData("unexpected local failure", "Failed")]
    public void FromStderr_ClassifiesWithoutRetainingRawOutput(
        string stderr,
        string expected)
    {
        // Break caught: a known failure is misclassified or raw command output survives classification.
        OpenCodeCommandException exception = OpenCodeCommandException.FromStderr(23, stderr);

        Assert.Equal(expected, exception.Failure.ToString());
        Assert.Equal(23, exception.ExitCode);
        Assert.DoesNotContain(stderr, exception.Message, StringComparison.Ordinal);
    }

    private static OpenCodeConsoleAccountReader Reader(string json) => new(
        (_, _) => Task.FromResult<byte[]?>(Encoding.UTF8.GetBytes(json)));
}
