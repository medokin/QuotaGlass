using System.Windows.Interop;
using AiStatus.Core;
using AiStatus.Platform;
using AiStatus.Tests.Support;

namespace AiStatus.Tests.Platform;

public sealed class GlobalHotkeyTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    [Theory]
    [InlineData("ctrl+alt+a", 0x0003u, 0x41u)]
    [InlineData("Win+Shift+9", 0x000Cu, 0x39u)]
    [InlineData("ALT+Z", 0x0001u, 0x5Au)]
    public void Constructor_ParsesCaseInsensitiveConfiguredChord(
        string configuredChord,
        uint expectedModifiers,
        uint expectedKey)
    {
        var native = new FakeHotkeyNative();
        var window = new FakeHotkeyWindow();

        using var hotkey = new GlobalHotkey(window, native, configuredChord, CreateLog());

        Assert.Equal(expectedModifiers, native.RegisteredModifiers);
        Assert.Equal(expectedKey, native.RegisteredKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+F1")]
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("Meta+A")]
    public void Constructor_InvalidChordFallsBackToDefaultAndLogsMetadata(string configuredChord)
    {
        var native = new FakeHotkeyNative();
        var window = new FakeHotkeyWindow();
        string logPath = Path.Combine(_directory.Path, $"hotkey-{Guid.NewGuid():N}.log");

        using var hotkey = new GlobalHotkey(
            window,
            native,
            configuredChord,
            new RollingFileLog(logPath));

        Assert.Equal(0x0003u, native.RegisteredModifiers);
        Assert.Equal(0x41u, native.RegisteredKey);
        Assert.Contains(" platform invalid", File.ReadAllText(logPath));
        if (configuredChord.Length > 0)
        {
            Assert.DoesNotContain(configuredChord, File.ReadAllText(logPath));
        }
    }

    [Fact]
    public void Constructor_RegistrationFailureIsLoggedWithoutThrowing()
    {
        var native = new FakeHotkeyNative { RegistrationResult = false };
        string logPath = Path.Combine(_directory.Path, "registration-failure.log");

        using var hotkey = new GlobalHotkey(
            new FakeHotkeyWindow(),
            native,
            "Ctrl+Alt+A",
            new RollingFileLog(logPath));

        Assert.Contains(" platform failed", File.ReadAllText(logPath));
    }

    [Fact]
    public void WindowMessage_ForRegisteredHotkeyRaisesPressed()
    {
        var native = new FakeHotkeyNative();
        var window = new FakeHotkeyWindow();
        using var hotkey = new GlobalHotkey(window, native, "Ctrl+Alt+A", CreateLog());
        int presses = 0;
        hotkey.Pressed += (_, _) => presses++;

        window.SendHotkeyMessage();

        Assert.Equal(1, presses);
    }

    [Fact]
    public void Dispose_UnregistersHotkeyAndRemovesWindowHook()
    {
        var native = new FakeHotkeyNative();
        var window = new FakeHotkeyWindow();
        var hotkey = new GlobalHotkey(window, native, "Ctrl+Alt+A", CreateLog());

        hotkey.Dispose();

        Assert.Equal(1, native.UnregisterCalls);
        Assert.Equal(1, window.RemoveHookCalls);
    }

    public void Dispose() => _directory.Dispose();

    private RollingFileLog CreateLog() =>
        new(Path.Combine(_directory.Path, $"hotkey-{Guid.NewGuid():N}.log"));

    private sealed class FakeHotkeyNative : IHotkeyNative
    {
        public bool RegistrationResult { get; init; } = true;
        public uint RegisteredModifiers { get; private set; }
        public uint RegisteredKey { get; private set; }
        public int UnregisterCalls { get; private set; }

        public bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint key)
        {
            RegisteredModifiers = modifiers;
            RegisteredKey = key;
            return RegistrationResult;
        }

        public bool UnregisterHotKey(IntPtr windowHandle, int id)
        {
            UnregisterCalls++;
            return true;
        }
    }

    private sealed class FakeHotkeyWindow : IHotkeyWindow
    {
        private HwndSourceHook? _hook;
        public IntPtr Handle => new(42);
        public int RemoveHookCalls { get; private set; }

        public void AddHook(HwndSourceHook hook) => _hook = hook;

        public void RemoveHook(HwndSourceHook hook)
        {
            Assert.Same(_hook, hook);
            RemoveHookCalls++;
            _hook = null;
        }

        public void SendHotkeyMessage()
        {
            bool handled = false;
            _hook!(Handle, 0x0312, new IntPtr(GlobalHotkey.HotkeyId), IntPtr.Zero, ref handled);
        }
    }
}
