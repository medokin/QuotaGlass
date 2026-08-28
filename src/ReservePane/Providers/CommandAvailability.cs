using System.IO;

namespace ReservePane.Providers;

public static class CommandAvailability
{
    public static bool IsAvailable(string commandName) => IsAvailable(
        commandName,
        Environment.GetEnvironmentVariable,
        File.Exists);

    internal static bool IsAvailable(
        string commandName,
        Func<string, EnvironmentVariableTarget, string?> getVariable,
        Func<string, bool> fileExists)
    {
        EffectiveCommandEnvironment environment = EffectiveCommandEnvironment.Capture(getVariable);
        return IsAvailable(
            commandName,
            environment.SearchPath,
            environment.PathExtensions,
            fileExists);
    }

    internal static bool IsAvailable(
        string commandName,
        string? path,
        string? pathExtensions,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (string.IsNullOrWhiteSpace(commandName) ||
            Path.GetFileName(commandName) != commandName)
        {
            return false;
        }

        string[] candidates = GetCandidates(commandName, pathExtensions);
        foreach (string pathEntry in (path ?? string.Empty).Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string directory = pathEntry.Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            foreach (string candidate in candidates)
            {
                if (fileExists(Path.Combine(directory, candidate)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string[] GetCandidates(string commandName, string? pathExtensions)
    {
        var candidates = new List<string> { commandName };
        foreach (string extension in (pathExtensions ?? string.Empty).Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string normalized = extension.StartsWith('.') ? extension : $".{extension}";
            string candidate = commandName + normalized;
            if (!candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(candidate);
            }
        }

        return [.. candidates];
    }
}
