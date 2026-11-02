using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MasronDroid;

public sealed class MirrorHost : HwndHost
{
    private nint video;
    private const int WindowStyleIndex = -16, Child = 0x40000000, Visible = 0x10000000;

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        var window = CreateWindowExW(0, "STATIC", "", Child | Visible | 0x02000000, 0, 0, 1, 1, parent.Handle, 0, 0, 0);
        if (window == 0) throw new Win32Exception();
        return new(this, window);
    }

    public void Attach(nint window)
    {
        int style = GetWindowLongW(window, WindowStyleIndex);
        SetWindowLongW(window, WindowStyleIndex, (style & ~unchecked((int)0x80CF0000)) | Child);
        if (SetParent(window, Handle) == 0 && Marshal.GetLastWin32Error() != 0)
            throw new Win32Exception();
        video = window;
        Fit();
    }

    public void Forget() => video = 0;

    private void Fit()
    {
        if (video != 0 && GetClientRect(Handle, out var rect))
            SetWindowPos(video, 0, 0, 0, Math.Max(1, rect.Right), Math.Max(1, rect.Bottom), 0x4054);
    }

    protected override nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 5 && video != 0) Fit();
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    protected override void DestroyWindowCore(HandleRef window) => DestroyWindow(window.Handle);

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowExW(int ex, string cls, string title, int style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint data);
    [DllImport("user32")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32")] private static extern int GetWindowLongW(nint hwnd, int index);
    [DllImport("user32")] private static extern int SetWindowLongW(nint hwnd, int index, int value);
    [DllImport("user32", SetLastError = true)] private static extern nint SetParent(nint child, nint parent);
    [DllImport("user32")] private static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
