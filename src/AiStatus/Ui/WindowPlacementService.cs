using System.Runtime.InteropServices;
using AiStatus.Core;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace AiStatus.Ui;

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

public sealed class WindowPlacementService
{
    private const uint MonitorDefaultToNearest = 2;

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

        Rect area = ToDipRect(physicalArea, monitor.DpiScale);
        return (monitor, area, DetectTaskbarEdge(monitor, area));
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
        Size windowSize,
        double margin = 12)
    {
        ArgumentNullException.ThrowIfNull(settings);
        MonitorWorkArea monitor = ResolveConfiguredMonitor(settings.OverlayMonitorId, monitors);
        Rect work = monitor.WorkingArea;

        Point desired = settings.OverlayCorner switch
        {
            OverlayCorner.TopLeft => new(work.Left + margin, work.Top + margin),
            OverlayCorner.TopRight => new(work.Right - windowSize.Width - margin, work.Top + margin),
            OverlayCorner.BottomLeft => new(work.Left + margin, work.Bottom - windowSize.Height - margin),
            OverlayCorner.BottomRight => new(work.Right - windowSize.Width - margin, work.Bottom - windowSize.Height - margin),
            OverlayCorner.Custom when settings.OverlayPosition is not null =>
                new(settings.OverlayPosition.X, settings.OverlayPosition.Y),
            _ => new(work.Right - windowSize.Width - margin, work.Bottom - windowSize.Height - margin),
        };

        return ClampToWorkArea(desired, windowSize, work);
    }

    public static Point GetPopupPosition(
        MonitorWorkArea monitor,
        Rect notificationArea,
        Size windowSize,
        TaskbarEdge edge)
    {
        Rect work = monitor.WorkingArea;
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

    public static async Task SaveCustomPositionAsync(
        SettingsStore store,
        AppSettings settings,
        Point position,
        Size windowSize,
        IReadOnlyList<MonitorWorkArea> monitors,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        MonitorWorkArea monitor = FindNearestMonitor(
            new Point(position.X + (windowSize.Width / 2), position.Y + (windowSize.Height / 2)),
            monitors);
        Point clamped = ClampToWorkArea(position, windowSize, monitor.WorkingArea);
        AppSettings updated = settings with
        {
            OverlayCorner = OverlayCorner.Custom,
            OverlayMonitorId = monitor.DeviceId,
            OverlayPosition = new OverlayPosition(clamped.X, clamped.Y),
        };
        await store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
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
        return new MonitorWorkArea(
            info.DeviceName,
            ToDipRect(info.Monitor, dpiScale),
            ToDipRect(info.WorkArea, dpiScale),
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

    private static Rect ToDipRect(NativeRect rect, double dpiScale) => new(
        rect.Left / dpiScale,
        rect.Top / dpiScale,
        (rect.Right - rect.Left) / dpiScale,
        (rect.Bottom - rect.Top) / dpiScale);

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

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

        [DllImport("shcore.dll")]
        internal static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
    }
}
