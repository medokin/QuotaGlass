using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AiStatus.Ui;

internal readonly record struct NativeWindowLongResult(IntPtr Value, int ErrorCode);

internal interface IOverlayWindowStyleNative
{
    NativeWindowLongResult GetExtendedStyle(IntPtr window);
    NativeWindowLongResult SetExtendedStyle(IntPtr window, IntPtr styles);
}

internal interface IOverlayExtendedStyle
{
    void Apply(IntPtr window);
}

internal sealed class OverlayWindowStyleNative : IOverlayWindowStyleNative
{
    private const int ExtendedStyleIndex = -20;

    public NativeWindowLongResult GetExtendedStyle(IntPtr window)
    {
        Marshal.SetLastPInvokeError(0);
        IntPtr value = NativeMethods.GetWindowLongPtr(window, ExtendedStyleIndex);
        return new NativeWindowLongResult(value, Marshal.GetLastPInvokeError());
    }

    public NativeWindowLongResult SetExtendedStyle(IntPtr window, IntPtr styles)
    {
        Marshal.SetLastPInvokeError(0);
        IntPtr value = NativeMethods.SetWindowLongPtr(window, ExtendedStyleIndex, styles);
        return new NativeWindowLongResult(value, Marshal.GetLastPInvokeError());
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr newValue);
    }
}

internal sealed class OverlayExtendedStyle : IOverlayExtendedStyle
{
    private const long NoActivateStyle = 0x08000000L;
    private const long ToolWindowStyle = 0x00000080L;
    private readonly IOverlayWindowStyleNative _native;

    public OverlayExtendedStyle()
        : this(new OverlayWindowStyleNative())
    {
    }

    internal OverlayExtendedStyle(IOverlayWindowStyleNative native)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }

    internal const long RequiredStyles = NoActivateStyle | ToolWindowStyle;

    public void Apply(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            throw new ArgumentException("A native overlay window handle is required.", nameof(window));
        }

        NativeWindowLongResult current = _native.GetExtendedStyle(window);
        ThrowForAmbiguousZero(current);
        long desired = current.Value.ToInt64() | RequiredStyles;
        if (desired != current.Value.ToInt64())
        {
            NativeWindowLongResult set = _native.SetExtendedStyle(window, new IntPtr(desired));
            ThrowForAmbiguousZero(set);
        }

        NativeWindowLongResult verified = _native.GetExtendedStyle(window);
        ThrowForAmbiguousZero(verified);
        if ((verified.Value.ToInt64() & RequiredStyles) != RequiredStyles)
        {
            throw new InvalidOperationException("The overlay no-activate style could not be verified.");
        }
    }

    private static void ThrowForAmbiguousZero(NativeWindowLongResult result)
    {
        if (result.Value == IntPtr.Zero && result.ErrorCode != 0)
        {
            throw new Win32Exception(result.ErrorCode);
        }
    }
}
