using System.ComponentModel;
using ReservePane.Ui;

namespace ReservePane.Tests.Ui;

[Collection(WpfStaCollection.Name)]
public sealed class OverlayExtendedStyleTests
{
    [Fact]
    public void Apply_AcceptsAmbiguousZeroWhenLastErrorIsClearAndVerifiesRequiredBits()
    {
        // Break caught: a valid zero previous style is mistaken for Win32 failure, or required bits are not read back.
        var native = new FakeOverlayWindowStyleNative(
            [new NativeWindowLongResult(IntPtr.Zero, 0), RequiredStyleResult],
            new NativeWindowLongResult(IntPtr.Zero, 0));
        var style = new OverlayExtendedStyle(native);

        style.Apply(new IntPtr(42));

        Assert.Equal(OverlayExtendedStyle.RequiredStyles, native.SetValue.ToInt64());
        Assert.Equal(2, native.GetCalls);
        Assert.Equal(1, native.SetCalls);
    }

    [Fact]
    public void Apply_ThrowsNativeErrorWhenInitialAmbiguousZeroHasLastError()
    {
        // Break caught: GetWindowLongPtr failure is treated as an empty style and overwritten.
        var native = new FakeOverlayWindowStyleNative(
            [new NativeWindowLongResult(IntPtr.Zero, 5)],
            new NativeWindowLongResult(IntPtr.Zero, 0));

        Win32Exception failure = Assert.Throws<Win32Exception>(
            () => new OverlayExtendedStyle(native).Apply(new IntPtr(42)));

        Assert.Equal(5, failure.NativeErrorCode);
        Assert.Equal(0, native.SetCalls);
    }

    [Fact]
    public void Apply_ThrowsNativeErrorWhenSetAmbiguousZeroHasLastError()
    {
        // Break caught: SetWindowLongPtr failure is hidden by its valid-looking zero return value.
        var native = new FakeOverlayWindowStyleNative(
            [new NativeWindowLongResult(IntPtr.Zero, 0)],
            new NativeWindowLongResult(IntPtr.Zero, 5));

        Win32Exception failure = Assert.Throws<Win32Exception>(
            () => new OverlayExtendedStyle(native).Apply(new IntPtr(42)));

        Assert.Equal(5, failure.NativeErrorCode);
        Assert.Equal(1, native.SetCalls);
    }

    [Fact]
    public void Apply_RejectsReadbackWithoutNoActivateAndToolWindowBits()
    {
        // Break caught: the native call reports success while the overlay remains focusable or appears in Alt+Tab.
        var native = new FakeOverlayWindowStyleNative(
            [
                new NativeWindowLongResult(IntPtr.Zero, 0),
                new NativeWindowLongResult(new IntPtr(0x80), 0),
            ],
            new NativeWindowLongResult(IntPtr.Zero, 0));

        Assert.Throws<InvalidOperationException>(
            () => new OverlayExtendedStyle(native).Apply(new IntPtr(42)));
    }

    [Fact]
    public void OverlayWindow_NativeStyleFailureDisablesInputAndAbortsShow()
    {
        // Break caught: an unverifiable overlay remains interactive and can steal focus after native style failure.
        Exception? observed = null;
        bool? hitTestVisible = null;
        var thread = new Thread(() =>
        {
            var window = new OverlayWindow(
                null,
                new WindowPlacementService(),
                new OverlayDragState(),
                new ThrowingOverlayExtendedStyle());
            try
            {
                window.Show();
            }
            catch (Exception exception)
            {
                observed = exception;
                hitTestVisible = window.IsHitTestVisible;
            }
            finally
            {
                window.Close();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The overlay style-failure thread did not finish.");

        Assert.IsType<Win32Exception>(observed);
        Assert.False(hitTestVisible);
    }

    private static NativeWindowLongResult RequiredStyleResult =>
        new(new IntPtr(OverlayExtendedStyle.RequiredStyles), 0);

    private sealed class FakeOverlayWindowStyleNative(
        IEnumerable<NativeWindowLongResult> getResults,
        NativeWindowLongResult setResult) : IOverlayWindowStyleNative
    {
        private readonly Queue<NativeWindowLongResult> _getResults = new(getResults);

        public int GetCalls { get; private set; }
        public int SetCalls { get; private set; }
        public IntPtr SetValue { get; private set; }

        public NativeWindowLongResult GetExtendedStyle(IntPtr window)
        {
            GetCalls++;
            return _getResults.Dequeue();
        }

        public NativeWindowLongResult SetExtendedStyle(IntPtr window, IntPtr styles)
        {
            SetCalls++;
            SetValue = styles;
            return setResult;
        }
    }

    private sealed class ThrowingOverlayExtendedStyle : IOverlayExtendedStyle
    {
        public void Apply(IntPtr window) => throw new Win32Exception(5);
    }
}
