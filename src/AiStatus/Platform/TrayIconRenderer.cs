using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AiStatus.Platform;

internal interface ITrayIconResource : IDisposable
{
    Icon Icon { get; }
}

internal interface ITrayIconFactory
{
    ITrayIconResource Create(TrayState state, int size);
}

internal interface IIconHandleApi
{
    IntPtr GetHicon(Bitmap bitmap);
    Icon CloneIcon(IntPtr handle);
    bool DestroyIcon(IntPtr handle);
}

internal sealed class ManagedTrayIconResource(Icon icon) : ITrayIconResource
{
    public Icon Icon { get; } = icon ?? throw new ArgumentNullException(nameof(icon));
    public void Dispose() => Icon.Dispose();
}

internal sealed class Win32IconHandleApi : IIconHandleApi
{
    public IntPtr GetHicon(Bitmap bitmap) => bitmap.GetHicon();

    public Icon CloneIcon(IntPtr handle) => (Icon)Icon.FromHandle(handle).Clone();

    public bool DestroyIcon(IntPtr handle) => NativeMethods.DestroyIcon(handle);

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr iconHandle);
    }
}

internal sealed class DrawingTrayIconFactory(IIconHandleApi handles) : ITrayIconFactory
{
    private readonly IIconHandleApi _handles = handles ?? throw new ArgumentNullException(nameof(handles));

    public ITrayIconResource Create(TrayState state, int size)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(TrayIconRenderer.GetColor(state));
        float inset = Math.Max(1, size / 16f);
        graphics.FillEllipse(brush, inset, inset, size - (2 * inset), size - (2 * inset));

        IntPtr handle = _handles.GetHicon(bitmap);
        try
        {
            return new ManagedTrayIconResource(_handles.CloneIcon(handle));
        }
        finally
        {
            _handles.DestroyIcon(handle);
        }
    }
}

public sealed class TrayIconRenderer : IDisposable
{
    private readonly ITrayIconFactory _factory;
    private readonly Dictionary<TrayState, CachedIcon> _cache = [];
    private bool _disposed;

    public TrayIconRenderer()
        : this(new DrawingTrayIconFactory(new Win32IconHandleApi()))
    {
    }

    internal TrayIconRenderer(ITrayIconFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public Icon Create(TrayState state, int size = 32)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_cache.TryGetValue(state, out CachedIcon cached))
        {
            if (cached.Size == size)
            {
                return cached.Resource.Icon;
            }
        }

        ITrayIconResource resource = _factory.Create(state, size);
        cached.Resource?.Dispose();
        _cache[state] = new CachedIcon(size, resource);
        return resource.Icon;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (CachedIcon cached in _cache.Values)
        {
            cached.Resource.Dispose();
        }

        _cache.Clear();
    }

    internal static Color GetColor(TrayState state) => state switch
    {
        TrayState.Green => Color.FromArgb(0x35, 0xC4, 0x6A),
        TrayState.Amber => Color.FromArgb(0xF0, 0xA4, 0x3A),
        TrayState.Red => Color.FromArgb(0xE2, 0x4B, 0x4B),
        TrayState.Grey => Color.FromArgb(0x8B, 0x90, 0x98),
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private readonly record struct CachedIcon(int Size, ITrayIconResource Resource);
}
