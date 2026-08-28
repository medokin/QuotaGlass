using System.Diagnostics;
using System.IO;
using System.Security;

namespace ReservePane.Providers;

internal sealed record EffectiveCommandEnvironment(
    string? SearchPath,
    string? PathExtensions)
{
    public static EffectiveCommandEnvironment Capture() => Capture(
        Environment.GetEnvironmentVariable);

    internal static EffectiveCommandEnvironment Capture(
        Func<string, EnvironmentVariableTarget, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        return new EffectiveCommandEnvironment(
            Merge(
                ReadVariable(getVariable, "PATH", EnvironmentVariableTarget.Process),
                ReadVariable(getVariable, "PATH", EnvironmentVariableTarget.User),
                ReadVariable(getVariable, "PATH", EnvironmentVariableTarget.Machine)),
            Merge(
                ReadVariable(getVariable, "PATHEXT", EnvironmentVariableTarget.Process),
                ReadVariable(getVariable, "PATHEXT", EnvironmentVariableTarget.User),
                ReadVariable(getVariable, "PATHEXT", EnvironmentVariableTarget.Machine)));
    }

    public void ApplyTo(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (SearchPath is not null)
        {
            startInfo.Environment["PATH"] = SearchPath;
        }

        if (PathExtensions is not null)
        {
            startInfo.Environment["PATHEXT"] = PathExtensions;
        }
    }

    private static string? ReadVariable(
        Func<string, EnvironmentVariableTarget, string?> getVariable,
        string name,
        EnvironmentVariableTarget target)
    {
        try
        {
            return getVariable(name, target);
        }
        catch (Exception exception) when (exception is
            UnauthorizedAccessException or
            SecurityException or
            IOException or
            System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string? Merge(params string?[] values)
    {
        var entries = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string value in values.OfType<string>())
        {
            foreach (string entry in value.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (seen.Add(entry))
                {
                    entries.Add(entry);
                }
            }
        }

        return entries.Count == 0
            ? null
            : string.Join(Path.PathSeparator, entries);
    }
}
