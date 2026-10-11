using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace UClear;
internal static class FileDrop
{
    [DllImport("ole32.dll")] private static extern int RevokeDragDrop(IntPtr window);
    [DllImport("shell32.dll")] private static extern void DragAcceptFiles(IntPtr window, bool accept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? path, uint size);
    [DllImport("shell32.dll")] private static extern void DragFinish(IntPtr drop);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ChangeWindowMessageFilterEx(IntPtr window, uint message, uint action, IntPtr change);
    public static void Attach(Window window, Action<string[]> received)
    {
        window.SourceInitialized += (_, _) =>
        {
            var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            // HwndSource registers an OLE target even when AllowDrop is false.
            // It takes precedence over Shell drops and cannot accept Explorer's
            // lower-integrity OLE drag. Use only the Shell receiver for this HWND.
            int revokeResult = RevokeDragDrop(source.Handle);
            if (revokeResult < 0 && revokeResult != unchecked((int)0x80040100))
                Marshal.ThrowExceptionForHR(revokeResult);
            // Shell file drops must also reach the elevated application from Explorer.
            foreach (uint message in new uint[] { 0x233, 0x49 })
                if (!ChangeWindowMessageFilterEx(source.Handle, message, 1, IntPtr.Zero))
                    System.Diagnostics.Trace.WriteLine("File drop filter: " + Marshal.GetLastWin32Error());
            DragAcceptFiles(source.Handle, true);
            source.AddHook((IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled) =>
            {
                if (message != 0x233) return IntPtr.Zero;
                handled = true;
                var paths = new List<string>();
                try
                {
                    uint count = DragQueryFile(wparam, uint.MaxValue, null, 0);
                    if (count > 32) { window.Dispatcher.BeginInvoke(new Action(() => AppDialog.Show(window, L.T("Tek seferde en fazla 32 öğe bırakabilirsin.", "You can drop at most 32 items at once."), L.T("Dosya incelemesi", "File inspection")))); return IntPtr.Zero; }
                    for (uint i = 0; i < count; i++)
                    {
                        uint length = DragQueryFile(wparam, i, null, 0);
                        if (length == 0 || length > 32767) continue;
                        var path = new StringBuilder((int)length + 1);
                        if (DragQueryFile(wparam, i, path, (uint)path.Capacity) > 0) paths.Add(path.ToString());
                    }
                }
                finally { DragFinish(wparam); }
                window.Dispatcher.BeginInvoke(new Action(() => received(paths.ToArray())));
                return IntPtr.Zero;
            });
            window.Closed += (_, _) => { if (!source.IsDisposed) DragAcceptFiles(source.Handle, false); };
        };
    }
}
