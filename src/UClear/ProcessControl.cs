using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UClear;
public static class ProcessControl
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessCritical(IntPtr process, out bool critical);
    public static bool Allowed(LockOwner owner) => owner.Pid > 4 && owner.Pid != Environment.ProcessId && owner.StartTime > 0 && string.IsNullOrWhiteSpace(owner.Service) && owner.Kind is not ("Servis" or "Kritik" or "Dosya Gezgini");
    public static async Task<string> Stop(LockOwner owner, bool force)
    {
        if (!Allowed(owner)) throw new InvalidOperationException(L.T("Bu süreç sistem/servis süreci veya UClear'ın kendisi; buradan sonlandırılamaz.", "This is a system/service process or UClear itself; it cannot be ended here."));
        using var process = Process.GetProcessById(owner.Pid);
        // Hold the process handle while validating identity and performing the operation.
        var handle = process.Handle;
        if (process.StartTime.ToUniversalTime().ToFileTimeUtc() != owner.StartTime) throw new InvalidOperationException(L.T("PID başka bir süreç tarafından kullanılıyor; yeniden tara.", "The PID is now used by another process; scan again."));
        if (process.SessionId == 0) throw new InvalidOperationException(L.T("Sistem oturumundaki süreçler sonlandırılamaz.", "Processes in the system session cannot be ended."));
        if (!IsProcessCritical(handle, out bool critical)) throw new Win32Exception(Marshal.GetLastWin32Error(), L.T("Sürecin sistem açısından kritik olup olmadığı doğrulanamadı.", "Could not verify whether the process is critical to the system."));
        if (critical) throw new InvalidOperationException(L.T("Kritik sistem süreci sonlandırılamaz.", "A critical system process cannot be ended."));
        if (force)
        {
            // Applications often keep their visible window in a child process.
            // Terminate the selected process tree, not unrelated same-name processes.
            process.Kill(entireProcessTree: true);
        }
        else if (!process.CloseMainWindow()) return L.T("Bu sürecin kapatılabilir ana penceresi yok. Gerekirse zorla sonlandırmayı kullan.", "This process has no main window to close. Use force end if needed.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await process.WaitForExitAsync(timeout.Token); return force ? L.T($"PID {owner.Pid} sonlandırıldı; alt süreçlerine de zorla sonlandırma uygulandı.", $"PID {owner.Pid} was ended; its child processes were force-ended too.") : L.T($"PID {owner.Pid} kapandı.", $"PID {owner.Pid} closed."); }
        catch (OperationCanceledException) { return force ? L.T($"PID {owner.Pid} için zorla sonlandırma uygulandı ancak kapanışı doğrulanamadı.", $"Force end was applied to PID {owner.Pid}, but its exit could not be confirmed.") : L.T("Kapatma isteği gönderildi; süreç hâlâ açık. Kaydetme veya onay penceresini kontrol et.", "A close request was sent; the process is still open. Check for a save or confirmation window."); }
    }
}
