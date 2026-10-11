using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using UClear;

internal static class Program
{
    [DllImport("ole32.dll")] private static extern int RevokeDragDrop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [STAThread]
    private static void Main()
    {
        var window = new Window();
        string[]? received = null;
        FileDrop.Attach(window, paths => received = paths);
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (RevokeDragDrop(handle) != unchecked((int)0x80040100)) throw new Exception("OLE drop target still registered.");
        Console.WriteLine("PASS WPF OLE target removed; Shell receiver can take precedence.");
        string[] expected = [@"C:\Fixture\Türkçe dosya.txt", @"C:\Fixture\Folder"];
        byte[] names = Encoding.Unicode.GetBytes(string.Join('\0', expected) + "\0\0");
        var drop = GlobalAlloc(0x42, (UIntPtr)(20 + names.Length));
        if (drop == IntPtr.Zero) throw new Exception("Allocation failed");
        var data = GlobalLock(drop);
        Marshal.WriteInt32(data, 0, 20); // DROPFILES.pFiles
        Marshal.WriteInt32(data, 16, 1); // DROPFILES.fWide
        Marshal.Copy(names, 0, data + 20, names.Length);
        GlobalUnlock(drop);
        SendMessage(handle, 0x233, drop, IntPtr.Zero); // Receiver owns and frees HDROP.
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        if (received == null || !received.SequenceEqual(expected)) throw new Exception("Shell drop paths not delivered correctly");
        Console.WriteLine("PASS Native file drop delivered both Unicode paths to callback.");
        window.Close();
    }
}
namespace UClear
{
    internal static class AppDialog
    {
        public static void Show(Window owner, string text, string title) => throw new Exception(text);
    }
}
