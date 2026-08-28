using System.Runtime.InteropServices;
using System.ComponentModel;
using ReservePane.Core;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;
using Window = System.Windows.Window;

namespace ReservePane.Ui;

public enum TaskbarEdge
{
    Left,
    Top,
    Right,
    Bottom,
}

public sealed record MonitorWorkArea(
    string DeviceId,
    Rect Bounds,
    Rect WorkingArea,
    bool IsPrimary,
    double DpiScale);

public sealed record CustomOverlayPosition(Point Position, string MonitorId);

public sealed class WindowPlacementService : IPopupPlacementService
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint NoSize = 0x0001;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;

    public IReadOnlyList<MonitorWorkArea> GetMonitorWorkAreas()
    {
        var monitors = new List<MonitorWorkArea>();
        NativeMethods.EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (monitorHandle, _, _, _) =>
            {
                MonitorWorkArea? monitor = GetMonitorWorkArea(monitorHandle);
                if (monitor is not null)
                {
                    monitors.Add(monitor);
                }

                return true;
            },
            IntPtr.Zero);
        return monitors;
    }

    public (MonitorWorkArea Monitor, Rect Area, TaskbarEdge Edge)? GetNotificationArea()
    {
        IntPtr taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        IntPtr notification = taskbar == IntPtr.Zero
            ? IntPtr.Zero
            : NativeMethods.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        IntPtr anchor = notification != IntPtr.Zero ? notification : taskbar;
        if (anchor == IntPtr.Zero || !NativeMethods.GetWindowRect(anchor, out NativeRect physicalArea))
        {
            return null;
        }

        var center = new NativePoint(
            physicalArea.Left + ((physicalArea.Right - physicalArea.Left) / 2),
            physicalArea.Top + ((physicalArea.Bottom - physicalArea.Top) / 2));
        IntPtr monitorHandle = NativeMethods.MonitorFromPoint(center, MonitorDefaultToNearest);
        MonitorWorkArea? monitor = GetMonitorWorkArea(monitorHandle);
        if (monitor is null)
        {
            return null;
        }

        Rect area = ToPhysicalRect(physicalArea);
        return (monitor, area, DetectTaskbarEdge(monitor, area));
    }

    public void PositionWindow(Window window, Point physicalPosition)
    {
        ArgumentNullException.ThrowIfNull(window);
        IntPtr handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (!NativeMethods.SetWindowPos(
                handle,
                IntPtr.Zero,
                checked((int)Math.Round(physicalPosition.X)),
                checked((int)Math.Round(physicalPosition.Y)),
                0,
                0,
                NoSize | NoZOrder | NoActivate))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public Rect GetWindowBounds(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        IntPtr handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (!NativeMethods.GetWindowRect(handle, out NativeRect bounds))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return ToPhysicalRect(bounds);
    }

    public static Point ClampToWorkArea(Point desired, Size windowSize, Rect workingArea)
    {
        double maxX = Math.Max(workingArea.Left, workingArea.Right - windowSize.Width);
        double maxY = Math.Max(workingArea.Top, workingArea.Bottom - windowSize.Height);
        return new Point(
            Math.Clamp(desired.X, workingArea.Left, maxX),
            Math.Clamp(desired.Y, workingArea.Top, maxY));
    }

    public static Point GetOverlayPosition(
        AppSettings settings,
        IReadOnlyList<MonitorWorkArea> monitors,
        Size windowSizeInDips,
        double margin = 12)
    {
        ArgumentNullException.ThrowIfNull(settings);
        MonitorWorkArea monitor = ResolveConfiguredMonitor(settings.OverlayMonitorId, monitors);
        Rect work = monitor.WorkingArea;
        Size windowSize = ToPhysicalPixels(windowSizeInDips, monitor);
        double physicalMargin = Math.Ceiling(margin * monitor.DpiScale);

        Point desired = settings.OverlayCorner switch
        {
            OverlayCorner.TopLeft => new(work.Left + physicalMargin, work.Top + physicalMargin),
            OverlayCorner.TopRight => new(work.Right - windowSize.Width - physicalMargin, work.Top + physicalMargin),
            OverlayCorner.BottomLeft => new(work.Left + physicalMargin, work.Bottom - windowSize.Height - physicalMargin),
            OverlayCorner.BottomRight => new(work.Right - windowSize.Width - physicalMargin, work.Bottom - windowSize.Height - physicalMargin),
            OverlayCorner.Custom when settings.OverlayPosition is not null =>
                new(settings.OverlayPosition.X, settings.OverlayPosition.Y),
            _ => new(work.Right - windowSize.Width - physicalMargin, work.Bottom - windowSize.Height - physicalMargin),
        };

        return ClampToWorkArea(desired, windowSize, work);
    }

    public static Point GetPopupPosition(
        MonitorWorkArea monitor,
        Rect notificationArea,
        Size windowSizeInDips,
        TaskbarEdge edge)
    {
        Rect work = monitor.WorkingArea;
        Size windowSize = ToPhysicalPixels(windowSizeInDips, monitor);
        Point desired = edge switch
        {
            TaskbarEdge.Top => new(notificationArea.Right - windowSize.Width, work.Top),
            TaskbarEdge.Bottom => new(notificationArea.Right - windowSize.Width, work.Bottom - windowSize.Height),
            TaskbarEdge.Left => new(work.Left, notificationArea.Bottom - windowSize.Height),
            TaskbarEdge.Right => new(work.Right - windowSize.Width, notificationArea.Bottom - windowSize.Height),
            _ => throw new ArgumentOutOfRangeException(nameof(edge)),
        };
        return ClampToWorkArea(desired, windowSize, work);
    }

    public static CustomOverlayPosition ClampCustomPosition(
        Point desiredPhysicalPosition,
        Size windowSizeInDips,
        MonitorWorkArea monitor)
    {
        Size physicalWindowSize = ToPhysicalPixels(windowSizeInDips, monitor);
        Point clamped = ClampToWorkArea(desiredPhysicalPosition, physicalWindowSize, monitor.WorkingArea);
        return new CustomOverlayPosition(clamped, monitor.DeviceId);
    }

    public static Size ToPhysicalPixels(Size sizeInDips, MonitorWorkArea monitor) => new(
        Math.Ceiling(sizeInDips.Width * monitor.DpiScale),
        Math.Ceiling(sizeInDips.Height * monitor.DpiScale));

    public static MonitorWorkArea FromPhysicalPixels(
        string deviceId,
        Rect bounds,
        Rect workingArea,
        bool isPrimary,
        double dpiScale)
    {
        if (!double.IsFinite(dpiScale) || dpiScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpiScale));
        }

        return new MonitorWorkArea(deviceId, bounds, workingArea, isPrimary, dpiScale);
    }

    public static MonitorWorkArea ResolveConfiguredMonitor(
        string? configuredDeviceId,
        IReadOnlyList<MonitorWorkArea> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0)
        {
            throw new InvalidOperationException("No display working areas are available.");
        }

        MonitorWorkArea? configured = monitors.FirstOrDefault(
            monitor => string.Equals(monitor.DeviceId, configuredDeviceId, StringComparison.OrdinalIgnoreCase));
        return configured ?? monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors[0];
    }

    public static MonitorWorkArea FindNearestMonitor(Point point, IReadOnlyList<MonitorWorkArea> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0)
        {
            throw new InvalidOperationException("No display working areas are available.");
        }

        return monitors
            .OrderBy(monitor => DistanceSquared(point, monitor.WorkingArea))
            .First();
    }

    private static double DistanceSquared(Point point, Rect area)
    {
        double dx = point.X < area.Left ? area.Left - point.X : point.X > area.Right ? point.X - area.Right : 0;
        double dy = point.Y < area.Top ? area.Top - point.Y : point.Y > area.Bottom ? point.Y - area.Bottom : 0;
        return (dx * dx) + (dy * dy);
    }

    private static TaskbarEdge DetectTaskbarEdge(MonitorWorkArea monitor, Rect notificationArea)
    {
        const double tolerance = 1;
        if (notificationArea.Bottom <= monitor.WorkingArea.Top + tolerance)
        {
            return TaskbarEdge.Top;
        }

        if (notificationArea.Top >= monitor.WorkingArea.Bottom - tolerance)
        {
            return TaskbarEdge.Bottom;
        }

        if (notificationArea.Right <= monitor.WorkingArea.Left + tolerance)
        {
            return TaskbarEdge.Left;
        }

        return TaskbarEdge.Right;
    }

    private static MonitorWorkArea? GetMonitorWorkArea(IntPtr monitorHandle)
    {
        if (monitorHandle == IntPtr.Zero)
        {
            return null;
        }

        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        if (!NativeMethods.GetMonitorInfo(monitorHandle, ref info))
        {
            return null;
        }

        double dpiScale = GetDpiScale(monitorHandle);
        return FromPhysicalPixels(
            info.DeviceName,
            ToPhysicalRect(info.Monitor),
            ToPhysicalRect(info.WorkArea),
            (info.Flags & 1) != 0,
            dpiScale);
    }

    private static double GetDpiScale(IntPtr monitorHandle)
    {
        try
        {
            return NativeMethods.GetDpiForMonitor(monitorHandle, 0, out uint dpiX, out _) == 0
                ? dpiX / 96d
                : 1;
        }
        catch (DllNotFoundException)
        {
            return 1;
        }
        catch (EntryPointNotFoundException)
        {
            return 1;
        }
    }

    private static Rect ToPhysicalRect(NativeRect rect) => new(
        rect.Left,
        rect.Top,
        rect.Right - rect.Left,
        rect.Bottom - rect.Top);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    private delegate bool MonitorEnumProcedure(IntPtr monitor, IntPtr deviceContext, IntPtr monitorRect, IntPtr data);

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clipRect, MonitorEnumProcedure callback, IntPtr data);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

        [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
        internal static extern IntPtr FindWindow(string? className, string? windowName);

        [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode)]
        internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

        [DllImport("shcore.dll")]
        internal static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
    }
}
