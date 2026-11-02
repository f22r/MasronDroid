using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MasronDroid;

internal static class WindowWorkArea
{
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Bounds, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Limits { public Point Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
    [DllImport("user32")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32")] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    public static void Install(Window owner)
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(owner).Handle);
        source.AddHook((nint hwnd, int message, nint w, nint data, ref bool handled) =>
        {
            if (message != 0x24) return 0;
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfoW(MonitorFromWindow(hwnd, 2), ref info)) return 0;
            var limits = Marshal.PtrToStructure<Limits>(data);
            limits.MaxPosition = new Point { X = info.Work.Left - info.Bounds.Left, Y = info.Work.Top - info.Bounds.Top };
            limits.MaxSize = new Point { X = info.Work.Right - info.Work.Left, Y = info.Work.Bottom - info.Work.Top };
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(owner);
            limits.MinTrack = new Point { X = (int)Math.Ceiling(owner.MinWidth * dpi.DpiScaleX), Y = (int)Math.Ceiling(owner.MinHeight * dpi.DpiScaleY) };
            Marshal.StructureToPtr(limits, data, false);
            handled = true;
            return 0;
        });
    }
}
