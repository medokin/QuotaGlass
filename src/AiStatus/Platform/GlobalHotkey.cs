using System.Runtime.InteropServices;
using System.Windows.Interop;
using AiStatus.Core;

namespace AiStatus.Platform;

internal interface IHotkeyNative
{
    bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint key);
    bool UnregisterHotKey(IntPtr windowHandle, int id);
}

internal interface IHotkeyWindow
{
    IntPtr Handle { get; }
    void AddHook(HwndSourceHook hook);
    void RemoveHook(HwndSourceHook hook);
}

internal sealed class HwndSourceHotkeyWindow(HwndSource source) : IHotkeyWindow
{
    private readonly HwndSource _source = source ?? throw new ArgumentNullException(nameof(source));

    public IntPtr Handle => _source.Handle;
    public void AddHook(HwndSourceHook hook) => _source.AddHook(hook);
    public void RemoveHook(HwndSourceHook hook) => _source.RemoveHook(hook);
}

internal sealed class Win32HotkeyNative : IHotkeyNative
{
    public bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint key) =>
        NativeMethods.RegisterHotKey(windowHandle, id, modifiers, key);

    public bool UnregisterHotKey(IntPtr windowHandle, int id) =>
        NativeMethods.UnregisterHotKey(windowHandle, id);

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint key);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(IntPtr windowHandle, int id);
    }
}

public sealed class GlobalHotkey : IDisposable
{
    internal const int HotkeyId = 0xA157;
    private const int WmHotkey = 0x0312;
    private const uint ModifierAlt = 0x0001;
    private const uint ModifierControl = 0x0002;
    private const uint ModifierShift = 0x0004;
    private const uint ModifierWin = 0x0008;
    private const uint KeyA = 0x41;
    private readonly IHotkeyWindow _window;
    private readonly IHotkeyNative _native;
    private readonly RollingFileLog _log;
    private readonly HwndSourceHook _hook;
    private bool _registered;
    private bool _disposed;

    public GlobalHotkey(HwndSource source, string configuredChord, RollingFileLog log)
        : this(new HwndSourceHotkeyWindow(source), new Win32HotkeyNative(), configuredChord, log)
    {
    }

    internal GlobalHotkey(
        IHotkeyWindow window,
        IHotkeyNative native,
        string configuredChord,
        RollingFileLog log)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(log);

        _window = window;
        _native = native;
        _log = log;
        _hook = WindowHook;
        _window.AddHook(_hook);

        HotkeyChord chord;
        if (!TryParse(configuredChord, out chord))
        {
            _log.Write(LogArea.Platform, LogOutcome.Invalid);
            chord = new HotkeyChord(ModifierControl | ModifierAlt, KeyA);
        }

        try
        {
            _registered = _native.RegisterHotKey(
                _window.Handle,
                HotkeyId,
                chord.Modifiers,
                chord.Key);
            _log.Write(
                LogArea.Platform,
                _registered ? LogOutcome.Registered : LogOutcome.Failed);
        }
        catch (Exception exception)
        {
            _log.Write(LogArea.Platform, LogOutcome.Failed, exception: exception);
        }
    }

    public event EventHandler? Pressed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_registered)
            {
                bool unregistered = _native.UnregisterHotKey(_window.Handle, HotkeyId);
                _log.Write(
                    LogArea.Platform,
                    unregistered ? LogOutcome.Unregistered : LogOutcome.Failed);
            }
        }
        catch (Exception exception)
        {
            _log.Write(LogArea.Platform, LogOutcome.Failed, exception: exception);
        }
        finally
        {
            _registered = false;
            _window.RemoveHook(_hook);
        }
    }

    private IntPtr WindowHook(
        IntPtr windowHandle,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter,
        ref bool handled)
    {
        if (message == WmHotkey && wordParameter.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    private static bool TryParse(string? configuredChord, out HotkeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(configuredChord))
        {
            return false;
        }

        uint modifiers = 0;
        uint? key = null;
        foreach (string rawToken in configuredChord.Split('+'))
        {
            string token = rawToken.Trim();
            uint modifier = token.ToUpperInvariant() switch
            {
                "CTRL" => ModifierControl,
                "ALT" => ModifierAlt,
                "SHIFT" => ModifierShift,
                "WIN" => ModifierWin,
                _ => 0,
            };

            if (modifier != 0)
            {
                if ((modifiers & modifier) != 0)
                {
                    return false;
                }

                modifiers |= modifier;
                continue;
            }

            if (key is not null || token.Length != 1)
            {
                return false;
            }

            char character = char.ToUpperInvariant(token[0]);
            if (character is not (>= 'A' and <= 'Z') and not (>= '0' and <= '9'))
            {
                return false;
            }

            key = character;
        }

        if (key is null)
        {
            return false;
        }

        chord = new HotkeyChord(modifiers, key.Value);
        return true;
    }

    private readonly record struct HotkeyChord(uint Modifiers, uint Key);
}
