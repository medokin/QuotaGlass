using Microsoft.Win32;

namespace AiStatus.Platform;

internal interface IRunKey
{
    string? GetValue(string name);
    void SetValue(string name, string value);
    void DeleteValue(string name);
}

internal sealed class RegistryRunKey : IRunKey
{
    internal const string SubKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? GetValue(string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SubKeyPath, writable: false);
        return key?.GetValue(name) as string;
    }

    public void SetValue(string name, string value)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(SubKeyPath, writable: true);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void DeleteValue(string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SubKeyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

public sealed class AutostartService
{
    internal const string ValueName = "QuotaGlass";
    private readonly IRunKey _runKey;
    private readonly string _command;

    public AutostartService(string executablePath)
        : this(new RegistryRunKey(), executablePath)
    {
    }

    internal AutostartService(IRunKey runKey, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(runKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        _runKey = runKey;
        _command = $"\"{executablePath.Trim().Trim('"')}\"";
    }

    public bool IsEnabled => string.Equals(
        _runKey.GetValue(ValueName),
        _command,
        StringComparison.OrdinalIgnoreCase);

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            _runKey.SetValue(ValueName, _command);
        }
        else
        {
            _runKey.DeleteValue(ValueName);
        }
    }
}
