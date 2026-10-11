using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace UClear;
public sealed record LockOwner(int Pid, string Program, string Service, string Kind, long StartTime = 0);
public sealed record FileUse(string File, string Program, int Pid);
public sealed record LockReport(List<LockOwner> Owners, int FilesChecked, List<string> Warnings, List<FileUse> Uses);

public static class FileLocks
{
    [StructLayout(LayoutKind.Sequential)]
    private struct UniqueProcess { public uint Pid; public System.Runtime.InteropServices.ComTypes.FILETIME Start; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceName;
        public uint AppType, Status, SessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmStartSession(out uint session, int flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmRegisterResources(uint session, uint fileCount, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] files, uint appCount, IntPtr apps, uint serviceCount, IntPtr services);
    [DllImport("rstrtmgr.dll")] private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[]? processes, ref uint reasons);
    [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint session);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    public static string? DeleteAccessIssue(string path)
    {
        // Request DELETE access without deleting, renaming, or modifying the object.
        using var handle = CreateFile(path, 0x10000, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (!handle.IsInvalid) return null;
        int code = Marshal.GetLastWin32Error();
        return code switch { 5 => L.T("Silme erişimi reddedildi (izin veya koruma).", "Delete access denied (permission or protection)."), 32 or 33 => L.T("Paylaşım/kilit ihlali; açık bir dosya tutamacı silmeyi engelliyor.", "Sharing/lock violation; an open file handle prevents deletion."), 2 or 3 => L.T("Konum artık mevcut değil.", "The location no longer exists."), _ => L.T($"Silme erişimi sınanamadı: {new Win32Exception(code).Message} ({code}).", $"Delete access could not be tested: {new Win32Exception(code).Message} ({code}).") };
    }

    public static LockReport Inspect(string folder, CancellationToken token, int limit = 3000, IProgress<ScanProgress>? progress = null)
    {
        var warnings = new List<string>(); var files = new List<string>(); var pending = new Stack<string>();
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(folder)) throw new IOException(L.T("Tam dosya veya klasör yolu gerekli.", "A full file or folder path is required."));
        var rootAttributes = File.GetAttributes(folder);
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0) return new([], 0, [L.T("Bağlantı atlandı: ", "Link skipped: ") + folder], []);
        if ((rootAttributes & FileAttributes.Directory) != 0) pending.Push(folder);
        else { files.Add(folder); if ((rootAttributes & FileAttributes.ReadOnly) != 0) warnings.Add(folder + L.T(" — Salt okunur özniteliği var.", " — Has the read-only attribute.")); }
        string? rootIssue = DeleteAccessIssue(folder); if (rootIssue != null) warnings.Add(folder + " — " + rootIssue);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { warnings.Add(L.T("Bağlantı atlandı: ", "Link skipped: ") + directory); continue; }
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested(); var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { warnings.Add(L.T("Bağlantı atlandı: ", "Link skipped: ") + entry); continue; }
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                    else { files.Add(entry); string? issue = DeleteAccessIssue(entry); if (issue != null) warnings.Add(entry + " — " + issue); if ((attributes & FileAttributes.ReadOnly) != 0) warnings.Add(entry + L.T(" — Salt okunur özniteliği var; silmeyi engelleyebilir.", " — Has the read-only attribute; it may prevent deletion.")); }
                    if (files.Count % 64 == 0) progress?.Report(new(files.Count, null, L.T("Dosyalar keşfediliyor", "Discovering files")));
                    if (files.Count >= limit) break;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add(directory + " — " + e.Message); }
            if (files.Count >= limit) { warnings.Add(L.T($"İlk {limit} dosya incelendi; sonuç kısmi olabilir.", $"The first {limit} files were inspected; the result may be partial.")); break; }
        }
        if (files.Count == 0) return new([], 0, warnings, []);
        progress?.Report(new(files.Count, null, L.T("Dosya kullanım sorgusu", "File usage query")));
        var owners = Query(files, token, progress);
        var uses = new List<FileUse>();
        var groups = new Stack<List<string>>();
        if (owners.Count > 0) groups.Push(files);
        int queries = 0; int resolvedFiles = 0;
        while (groups.TryPop(out var group))
        {
            token.ThrowIfCancellationRequested();
            if (++queries > 128) { warnings.Add(L.T("Dosya bazındaki ayrıntı sınırına ulaşıldı; bazı kullanım yolları listelenememiş olabilir.", "The per-file detail limit was reached; some usage paths may not be listed.")); break; }
            var current = Query(group, token);
            if (current.Count == 0) { resolvedFiles += group.Count; progress?.Report(new(resolvedFiles, files.Count, L.T("Dosya kullanım ayrıntıları", "File usage details"))); continue; }
            if (group.Count == 1) { progress?.Report(new(++resolvedFiles, files.Count, L.T("Dosya kullanım ayrıntıları", "File usage details"))); uses.AddRange(current.Select(owner => new FileUse(group[0], owner.Program, owner.Pid))); continue; }
            int middle = group.Count / 2;
            groups.Push(group.Take(middle).ToList()); groups.Push(group.Skip(middle).ToList());
        }
        progress?.Report(new(files.Count, files.Count, L.T("İnceleme tamamlandı", "Inspection complete")));
        return new(owners, files.Count, warnings, uses);
    }
    public static List<LockOwner> Query(List<string> files, CancellationToken token, IProgress<ScanProgress>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        int error = RmStartSession(out uint session, 0, new StringBuilder(33));
        if (error != 0) throw new Win32Exception(error, L.T("Dosya kullanım sorgusu başlatılamadı.", "The file usage query could not be started."));
        try
        {
            int registered = 0;
            foreach (var chunk in files.Chunk(128))
            {
                token.ThrowIfCancellationRequested();
                error = RmRegisterResources(session, (uint)chunk.Length, chunk, 0, IntPtr.Zero, 0, IntPtr.Zero);
                if (error != 0) throw new Win32Exception(error, L.T("Dosyalar kullanım sorgusuna eklenemedi.", "Files could not be added to the usage query."));
                registered += chunk.Length; progress?.Report(new(registered, files.Count, L.T("Kullanım sorgusuna hazırlık", "Preparing the usage query")));
            }
            uint count = 0, reasons = 0;
            error = RmGetList(session, out uint needed, ref count, null, ref reasons);
            for (int attempt = 0; error == 234 && attempt < 4; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var processes = new ProcessInfo[needed]; count = needed;
                error = RmGetList(session, out needed, ref count, processes, ref reasons);
                if (error == 0) return processes.Take((int)count).Select(p => new LockOwner((int)p.Process.Pid, p.AppName, p.ServiceName, p.AppType == 3 ? "Servis" : p.AppType == 4 ? "Dosya Gezgini" : p.AppType == 1000 ? "Kritik" : "Uygulama", ((long)p.Process.Start.dwHighDateTime << 32) | (uint)p.Process.Start.dwLowDateTime)).DistinctBy(p => p.Pid).ToList();
            }
            if (error != 0) throw new Win32Exception(error, L.T("Dosyaları kullanan süreçler sorgulanamadı.", "Processes using the files could not be queried."));
            return [];
        }
        finally { RmEndSession(session); }
    }
}

