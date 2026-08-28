namespace ReservePane.Providers;

internal enum OpenCodeCommandFailure
{
    DatabaseBusy,
    TimedOut,
    CommandNotFound,
    Failed,
}

internal sealed class OpenCodeCommandException : Exception
{
    public OpenCodeCommandException(OpenCodeCommandFailure failure, int exitCode)
        : base("OpenCode command failed.")
    {
        if (!Enum.IsDefined(failure))
        {
            throw new ArgumentOutOfRangeException(nameof(failure));
        }

        Failure = failure;
        ExitCode = exitCode;
    }

    public OpenCodeCommandFailure Failure { get; }

    public int ExitCode { get; }

    public static OpenCodeCommandException FromStderr(int exitCode, string stderr)
    {
        OpenCodeCommandFailure failure = Contains(stderr, "locked") || Contains(stderr, "busy")
            ? OpenCodeCommandFailure.DatabaseBusy
            : Contains(stderr, "timeout") || Contains(stderr, "timed out")
                ? OpenCodeCommandFailure.TimedOut
                : Contains(stderr, "not recognized") ||
                    Contains(stderr, "not found") ||
                    Contains(stderr, "cannot find")
                    ? OpenCodeCommandFailure.CommandNotFound
                    : OpenCodeCommandFailure.Failed;
        return new OpenCodeCommandException(failure, exitCode);
    }

    private static bool Contains(string value, string expected) =>
        value.Contains(expected, StringComparison.OrdinalIgnoreCase);
}
