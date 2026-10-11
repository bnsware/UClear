using System.IO;
using System.Net;
using System.Text;
using Microsoft.Win32;

namespace UClear;
public sealed record DiskEntry(string Path, string Kind, long Bytes, DateTime Modified) : System.ComponentModel.INotifyPropertyChanged
{
    private bool selected;
    public bool Selected { get => selected; set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public string Size => Finding.FormatBytes(Bytes);
    public DateTime ModifiedLocal => Modified.ToLocalTime();
    public string Result { get; set; } = "";
}
public sealed record DiskReport(List<DiskEntry> Entries, List<string> Warnings);
public static class DiskTools
{
    // Recursive enumeration that skips links and cloud-only (OneDrive placeholder) files, so a scan never triggers downloads.
    public static EnumerationOptions Local(int depth = int.MaxValue) => new() { RecurseSubdirectories = true, MaxRecursionDepth = depth, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x400000 };
    public static void NoLinks(string path)
    {
        for (var current = new DirectoryInfo(path); current != null; current = current.Parent)
            if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0) throw new IOException(L.T("Bağlantı konumu işlenmez: ", "Link locations are not processed: ") + path);
    }
    public static DiskReport Scan(string root, string mode, CancellationToken token, int minimumAgeDays = 7, IProgress<ScanProgress>? progress = null, Action<DiskEntry>? onFound = null)
    {
        root = System.IO.Path.GetFullPath(root); NoLinks(root);
        var entries = new List<DiskEntry>(); var warnings = new List<string>();
        var totals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var modified = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var start = new DirectoryInfo(root); modified[root] = start.LastWriteTimeUtc;
        var pending = new Stack<DirectoryInfo>(); pending.Push(start); long count = 0; var tick = System.Diagnostics.Stopwatch.StartNew();
        var cutoff = DateTime.UtcNow.AddDays(-minimumAgeDays);
        // Attributes, sizes and dates come with the enumeration; a subfolder is entered only after it was seen not to be a link, so only the root needs the full link check.
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested(); string folder = directory.FullName; totals.TryAdd(folder, 0);
            try
            {
                bool hasChildren = false;
                foreach (var info in directory.EnumerateFileSystemInfos("*", options))
                {
                    token.ThrowIfCancellationRequested();
                    hasChildren = true; count++; if (tick.ElapsedMilliseconds >= 100) { progress?.Report(new(count, null, L.T("Dosya ve klasör keşfi", "Discovering files and folders"))); tick.Restart(); }
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { warnings.Add(L.T("Bağlantı atlandı: ", "Link skipped: ") + info.FullName); continue; }
                    if (info is DirectoryInfo sub) { pending.Push(sub); modified[sub.FullName] = sub.LastWriteTimeUtc; continue; }
                    var file = (FileInfo)info; totals[folder] += file.Length;
                    if (mode == "large" || (mode == "temp" && (minimumAgeDays == 0 || file.LastWriteTimeUtc < cutoff)))
                    {
                        var entry = new DiskEntry(file.FullName, "Dosya", file.Length, file.LastWriteTimeUtc); entries.Add(entry); onFound?.Invoke(entry);
                    }
                }
                if (mode == "empty" && !hasChildren && !folder.Equals(root, StringComparison.OrdinalIgnoreCase))
                {
                    var entry = new DiskEntry(folder, "Boş klasör", 0, modified[folder]); entries.Add(entry); onFound?.Invoke(entry);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add(folder + ": " + e.Message); }
        }
        int calculated = 0;
        foreach (string path in totals.Keys.OrderByDescending(p => p.Length).ToArray())
        {
            token.ThrowIfCancellationRequested();
            if (++calculated % 128 == 0 || calculated == totals.Count) progress?.Report(new(calculated, totals.Count, L.T("Klasör boyutları", "Folder sizes")));
            var parent = System.IO.Path.GetDirectoryName(path);
            if (parent != null && totals.ContainsKey(parent)) totals[parent] += totals[path];
            if (mode == "large") entries.Add(new(path, "Klasör", totals[path], modified.TryGetValue(path, out var time) ? time : default));
        }
        progress?.Report(new(count, count, L.T("Tarama tamamlandı", "Scan complete")));
        return new(entries.OrderByDescending(e => e.Bytes).ToList(), warnings);
    }
    public static void Delete(DiskEntry item, string root, string mode, int minimumAgeDays = 7)
    {
        if (!Safety.IsWithin(item.Path, root)) throw new IOException(L.T("Tarama kökü dışındaki öğe silinmez.", "Items outside the scanned folder are not deleted."));
        NoLinks(System.IO.Path.GetDirectoryName(item.Path)!);
        if ((File.GetAttributes(item.Path) & FileAttributes.ReparsePoint) != 0) throw new IOException(L.T("Bağlantı silinmez.", "Links are not deleted."));
        if (mode != "temp" && (Safety.IsWithin(item.Path, Environment.GetFolderPath(Environment.SpecialFolder.Windows)) || Safety.IsWithin(item.Path, AppContext.BaseDirectory) || Safety.Canonical(item.Path).Equals(Safety.Canonical(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))) throw new IOException(L.T("Sistem ve UClear dosyaları burada silinmez.", "System and UClear files are not deleted here."));
        if (item.Kind == "Boş klasör") { Directory.Delete(item.Path, false); return; }
        if (item.Kind != "Dosya") throw new IOException(L.T("Dolu klasörler buradan silinmez; konumunu açarak incele.", "Non-empty folders are not deleted here; open the location to review it."));
        var info = new FileInfo(item.Path);
        if (info.Length != item.Bytes || info.LastWriteTimeUtc != item.Modified) throw new IOException(L.T("Dosya taramadan sonra değişmiş; tekrar tara.", "The file changed after the scan; scan again."));
        if (mode == "temp" && minimumAgeDays > 0 && info.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-minimumAgeDays)) throw new IOException(L.T("Yeni geçici dosya atlandı.", "A recent temporary file was skipped."));
        File.Delete(item.Path);
        if (File.Exists(item.Path)) throw new IOException(L.T("Dosya hâlâ mevcut.", "The file still exists."));
    }
}
public static class HostsTools
{
    public static string Path => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
    public static string Default => L.T("# UClear - Varsayilan hosts\r\n# Localhost ad cozumu DNS tarafindan yonetilir.\r\n# 127.0.0.1 localhost\r\n# ::1 localhost\r\n", "# UClear - Default hosts\r\n# Localhost name resolution is handled by DNS.\r\n# 127.0.0.1 localhost\r\n# ::1 localhost\r\n");
    public static void Validate(string content)
    {
        int line = 0;
        foreach (string text in content.Split('\n'))
        {
            line++; var parts = text.Split('#')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            if (parts.Length < 2 || !IPAddress.TryParse(parts[0], out _) || parts.Skip(1).Any(p => Uri.CheckHostName(p) == UriHostNameType.Unknown)) throw new IOException(L.T($"{line}. satırda geçerli IP ve alan adı gerekli.", $"Line {line} needs a valid IP address and host name."));
        }
    }
    public static void Save(string path, string original, string content)
    {
        Validate(content); DiskTools.NoLinks(System.IO.Path.GetDirectoryName(path)!);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException(L.T("Hosts dosyası bağlantı olamaz.", "The hosts file cannot be a link."));
        // Exclusive access prevents overwriting another editor's changes during validation.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, true);
        string current = reader.ReadToEnd();
        if (current != original) throw new IOException(L.T("Hosts başka bir işlem tarafından değiştirildi. Yeniden yükleyip düzenle.", "The hosts file was changed by another program. Reload it and edit again."));
        var bytes = new UTF8Encoding(false).GetBytes(content);
        stream.Position = 0; stream.Write(bytes); stream.SetLength(bytes.Length); stream.Flush(true);
    }
}
public sealed record StartupEntry(string Name, string Command, string Hive, string View, bool Disabled, RegistryValueKind ValueKind)
{
        public bool? WindowsEnabled { get; init; } = true;
    public string ApprovalSnapshot { get; init; } = "";
    public bool? Enabled => Disabled ? false : WindowsEnabled;
    public string State => Disabled ? L.T("Kapalı · UClear", "Off · UClear") : WindowsEnabled == true ? L.T("Açık", "On") : WindowsEnabled == false ? L.T("Kapalı · Windows", "Off · Windows") : L.T("Doğrulanamadı", "Could not verify");
    public string Scope => (Hive == "CurrentUser" ? L.T("Bu kullanıcı", "This user") : L.T("Tüm kullanıcılar", "All users")) + (Hive != "CurrentUser" && View == "Registry32" ? L.T(" · 32 bit", " · 32-bit") : "");
}
public static class StartupTools
{
    const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string Disabled = @"Software\UClear\DisabledStartup";
    public static List<StartupEntry> Read()
    {
        var result = new List<StartupEntry>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            foreach (bool off in new[] { false, true })
            {
                using var key = root.OpenSubKey(off ? Disabled : Run);
                if (key == null) continue;
                foreach (string name in key.GetValueNames())
                    if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string command)
                                            {
                        var approval = ReadApproval(hive, view, name);
                        result.Add(new(name, command, hive.ToString(), view.ToString(), off, key.GetValueKind(name)) { WindowsEnabled = approval.Enabled, ApprovalSnapshot = approval.Snapshot });
                    }
            }
        }
        return result.DistinctBy(e => e.Hive == "CurrentUser" ? (e.Hive, "shared", e.Name, e.Disabled) : (e.Hive, e.View, e.Name, e.Disabled)).ToList();
    }
    const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
    internal static string ApprovalGroup(RegistryHive hive, RegistryView view) => hive == RegistryHive.LocalMachine && view == RegistryView.Registry32 ? "Run32" : "Run";
    public static bool? DecodeApproval(object? value) => value == null ? true : value is byte[] { Length: 12 } bytes ? BitConverter.ToUInt32(bytes, 0) switch { 2 or 6 => true, 3 or 7 => false, _ => null } : null;
    static (bool? Enabled, string Snapshot) ReadApproval(RegistryHive hive, RegistryView view, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(Approved + ApprovalGroup(hive, view));
            var value = key?.GetValue(name);
            return (DecodeApproval(value), value is byte[] bytes ? Convert.ToBase64String(bytes) : value == null ? "" : "unknown");
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return (null, "unreadable"); }
    }
    public static void Toggle(StartupEntry entry)
    {
        if (entry.WindowsEnabled == null) throw new IOException(L.T("Windows başlangıç durumu doğrulanamadı. Windows başlangıç ayarlarından kontrol et.", "The Windows startup state could not be verified. Check it in Windows startup settings."));
        var hive = Enum.Parse<RegistryHive>(entry.Hive); var view = Enum.Parse<RegistryView>(entry.View);
        var live = ReadApproval(hive, view, entry.Name);
        if (live.Snapshot != entry.ApprovalSnapshot) throw new IOException(L.T("Başlangıç durumu değişmiş; listeyi yenile.", "The startup state has changed; refresh the list."));
        if (entry.Disabled) ToggleAt(entry, Run, Disabled);
        else
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var run = root.OpenSubKey(Run);
            if (run?.GetValue(entry.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string != entry.Command || run.GetValueKind(entry.Name) != entry.ValueKind) throw new IOException(L.T("Başlangıç komutu değişti; yenile.", "The startup command changed; refresh."));
        }
        using var approvalRoot = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var approval = approvalRoot.CreateSubKey(Approved + ApprovalGroup(hive, view));
        byte[] value = new byte[12]; bool enable = entry.Enabled == false;
        value[0] = enable ? (byte)2 : (byte)3;
        if (!enable) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(value, 4);
        approval.SetValue(entry.Name, value, RegistryValueKind.Binary);
        if (DecodeApproval(approval.GetValue(entry.Name)) != enable) throw new IOException(L.T("Yeni durum doğrulanamadı.", "The new state could not be verified."));
    }
    internal static void ToggleAt(StartupEntry entry, string runPath, string disabledPath)
    {
        using var root = RegistryKey.OpenBaseKey(Enum.Parse<RegistryHive>(entry.Hive), Enum.Parse<RegistryView>(entry.View));
        using var source = root.OpenSubKey(entry.Disabled ? disabledPath : runPath, true) ?? throw new IOException(L.T("Kayıt artık yok.", "The entry no longer exists."));
        if (source.GetValue(entry.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string != entry.Command || source.GetValueKind(entry.Name) != entry.ValueKind) throw new IOException(L.T("Kayıt değişti; listeyi yenile.", "The entry changed; refresh the list."));
        using var destination = root.CreateSubKey(entry.Disabled ? runPath : disabledPath);
        if (destination.GetValueNames().Contains(entry.Name, StringComparer.OrdinalIgnoreCase)) throw new IOException(L.T("Aynı isimli başka kayıt var; üzerine yazılmadı.", "Another entry with the same name exists; it was not overwritten."));
        destination.SetValue(entry.Name, entry.Command, entry.ValueKind);
        try { source.DeleteValue(entry.Name); }
        catch { destination.DeleteValue(entry.Name, false); throw; }
    }
}




