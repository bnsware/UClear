using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace UClear;
internal static class WindowAppearance
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out WindowRect rect);
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int Left, Top, Right, Bottom; }
    public static void Attach(Window window)
    {
        bool nativeCorners = false;
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            int round = 2, dark = 1;
            nativeCorners = DwmSetWindowAttribute(handle, 33, ref round, sizeof(int)) == 0;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            if (!nativeCorners) Clip();
        };
        window.SizeChanged += (_, _) => { if (!nativeCorners) Clip(); };
        void Clip()
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            if (window.WindowState == WindowState.Maximized) { SetWindowRgn(handle, IntPtr.Zero, true); return; }
            if (!GetWindowRect(handle, out var rect)) return;
            double scale = System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX;
            var region = CreateRoundRectRgn(0, 0, rect.Right - rect.Left + 1, rect.Bottom - rect.Top + 1, (int)(24 * scale), (int)(24 * scale));
            if (region != IntPtr.Zero && SetWindowRgn(handle, region, true) == 0) DeleteObject(region);
        }
    }
}
