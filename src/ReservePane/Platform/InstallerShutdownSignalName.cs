using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ReservePane.Platform;

internal static class InstallerShutdownSignalName
{
    public static string FromExecutablePath(string executablePath)
    {
        string normalizedPath = Path.GetFullPath(executablePath).ToUpperInvariant();
        using SHA256 sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(normalizedPath));
        string hexHash = BitConverter.ToString(hash).Replace("-", string.Empty);
        return $"Local\\ReservePane.InstallerShutdown.{hexHash}";
    }
}
