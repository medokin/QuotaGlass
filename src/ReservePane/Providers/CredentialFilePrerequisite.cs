using System.IO;
using System.Security;

namespace QuotaGlass.Providers;

internal static class CredentialFilePrerequisite
{
    public static Task<bool> IsPresentOrIndeterminateAsync(
        string path,
        Func<string, bool> probe,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(IsPresentOrIndeterminate(path, probe));
    }

    public static bool IsPresentOrIndeterminate(string path, Func<string, bool> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        try
        {
            return probe(path);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is
            UnauthorizedAccessException or
            SecurityException or
            IOException)
        {
            return true;
        }
    }

    public static bool Probe(string path)
    {
        _ = File.GetAttributes(path);
        return true;
    }
}
