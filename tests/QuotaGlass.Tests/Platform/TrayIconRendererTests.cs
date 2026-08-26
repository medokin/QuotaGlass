using System.Drawing;
using QuotaGlass.Platform;

namespace QuotaGlass.Tests.Platform;

public sealed class TrayIconRendererTests
{
    [Theory]
    [InlineData(TrayState.Green, unchecked((int)0xFF35C46A))]
    [InlineData(TrayState.Amber, unchecked((int)0xFFF0A43A))]
    [InlineData(TrayState.Red, unchecked((int)0xFFE24B4B))]
    [InlineData(TrayState.Grey, unchecked((int)0xFF8B9098))]
    public void GetColor_ReturnsSpecifiedColor(TrayState state, int expectedArgb)
    {
        Assert.Equal(expectedArgb, TrayIconRenderer.GetColor(state).ToArgb());
    }

    [Fact]
    public void Create_CachesOneIconPerStateAndSize()
    {
        var factory = new FakeTrayIconFactory();
        using var renderer = new TrayIconRenderer(factory);

        Icon first = renderer.Create(TrayState.Green);
        Icon second = renderer.Create(TrayState.Green);
        Icon red = renderer.Create(TrayState.Red);

        Assert.Same(first, second);
        Assert.NotSame(first, red);
        Assert.Equal(2, factory.CreateCalls);
    }

    [Fact]
    public void Create_ReplacesAndDisposesCachedStateWhenSizeChanges()
    {
        var factory = new FakeTrayIconFactory();
        using var renderer = new TrayIconRenderer(factory);
        renderer.Create(TrayState.Green, 16);
        FakeIconResource first = factory.Resources[0];

        renderer.Create(TrayState.Green, 32);

        Assert.True(first.IsDisposed);
        Assert.Equal(2, factory.CreateCalls);
    }

    [Fact]
    public void Create_WhenReplacementFailsKeepsExistingCachedIconAlive()
    {
        var factory = new FakeTrayIconFactory();
        using var renderer = new TrayIconRenderer(factory);
        Icon existing = renderer.Create(TrayState.Green, 16);
        FakeIconResource existingResource = factory.Resources[0];
        factory.ThrowOnNextCreate = true;

        Assert.Throws<InvalidOperationException>(() => renderer.Create(TrayState.Green, 32));

        Assert.False(existingResource.IsDisposed);
        Assert.Same(existing, renderer.Create(TrayState.Green, 16));
    }

    [Fact]
    public void Dispose_DisposesEveryCachedIconAndPreventsFurtherCreation()
    {
        var factory = new FakeTrayIconFactory();
        var renderer = new TrayIconRenderer(factory);
        renderer.Create(TrayState.Green);
        renderer.Create(TrayState.Red);

        renderer.Dispose();

        Assert.All(factory.Resources, resource => Assert.True(resource.IsDisposed));
        Assert.Throws<ObjectDisposedException>(() => renderer.Create(TrayState.Amber));
    }

    [Fact]
    public void DrawingFactory_DrawsRequestedColorAndAlwaysDestroysNativeHandle()
    {
        var native = new FakeIconHandleApi();
        var factory = new DrawingTrayIconFactory(native);

        using ITrayIconResource resource = factory.Create(TrayState.Amber, 32);

        Assert.Equal(unchecked((int)0xFFF0A43A), native.CenterPixel.ToArgb());
        Assert.Equal(new IntPtr(123), native.DestroyedHandle);
    }

    private sealed class FakeTrayIconFactory : ITrayIconFactory
    {
        public List<FakeIconResource> Resources { get; } = [];
        public int CreateCalls => Resources.Count;
        public bool ThrowOnNextCreate { get; set; }

        public ITrayIconResource Create(TrayState state, int size)
        {
            if (ThrowOnNextCreate)
            {
                ThrowOnNextCreate = false;
                throw new InvalidOperationException("Synthetic factory failure.");
            }

            var resource = new FakeIconResource();
            Resources.Add(resource);
            return resource;
        }
    }

    private sealed class FakeIconResource : ITrayIconResource
    {
        public Icon Icon { get; } = (Icon)SystemIcons.Application.Clone();
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            Icon.Dispose();
            IsDisposed = true;
        }
    }

    private sealed class FakeIconHandleApi : IIconHandleApi
    {
        public Color CenterPixel { get; private set; }
        public IntPtr DestroyedHandle { get; private set; }

        public IntPtr GetHicon(Bitmap bitmap)
        {
            CenterPixel = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
            return new IntPtr(123);
        }

        public Icon CloneIcon(IntPtr handle) => (Icon)SystemIcons.Application.Clone();

        public bool DestroyIcon(IntPtr handle)
        {
            DestroyedHandle = handle;
            return true;
        }
    }
}
