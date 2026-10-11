using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
namespace UClear;
// ProgramGone: the target's folder is missing (its top-level folder on the drive still exists) or holds no program files.
public sealed record ReferenceProbe(string Target, string State, bool Missing, bool ProgramGone = false);
public sealed class InvalidReference : INotifyPropertyChanged
{
    public required string Category { get; init; }
    public required string Location { get; init; }
    public string Name { get; init; } = "";
    public string View { get; init; } = "Registry64";
    public required string Original { get; init; }
    public RegistryValueKind ValueKind { get; init; }
    public required ReferenceProbe Probe { get; init; }
    public string Target => Probe.Target;
    public string State => Probe.State;
    public bool CanDelete { get; init; }
    public bool Certain { get; init; }
    public bool WindowsOwned { get; init; }
    public int Rank => Certain ? 0 : CanDelete ? 1 : WindowsOwned ? 3 : 2;
    public string Policy => Certain ? L.T("Kesin silinebilir", "Safe to delete") : CanDelete ? L.T("Seçerek silinebilir", "Deletable by choice") : WindowsOwned ? L.T("Windows'a ait · dokunulmaz", "Belongs to Windows · untouched") : L.T("İnceleme gerekli", "Needs review");
    public DateTime Modified { get; init; }
    private bool selected;
    public bool Selected { get => selected; set { selected = value && CanDelete; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public string Result { get; set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;
}
public sealed record InvalidReport(List<InvalidReference> Items, List<string> Warnings);
public static class InvalidReferences
{
    public static ReferenceProbe Probe(string command, bool literal = false)
    {
        string expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        if (expanded.Length == 0) return new("", "Boş komut; inceleme gerekli", false);
        string path;
        if (literal) path = expanded.Trim('"');
        else if (expanded.StartsWith('"'))
        {
            int end = expanded.IndexOf('"', 1);
            if (end < 0) return new(expanded, "Eksik tırnak; komut çözümlenemedi", false);
            path = expanded[1..end];
        }
        else
        {
            var match = Regex.Match(expanded, @"^(.+?\.(?:exe|com|bat|cmd|dll))(?=\s|$)", RegexOptions.IgnoreCase);
            if (!match.Success) return new(expanded, "Dolaylı veya çözümlenemeyen komut", false);
            path = match.Groups[1].Value;
            if (path.Contains(' ')) return new(path, "Tırnaksız boşluklu yol; belirsiz", false);
        }
        try
        {
            if (path.Contains('%') || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") || path.Contains(".." + Path.DirectorySeparatorChar)) return new(path, "Ağ, göreli veya çözümlenemeyen yol", false);
            path = Path.GetFullPath(path);
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed) return new(path, "Disk bağlı değil veya çıkarılabilir/ağ diski", false);
            // Missing descendants of a junction are not proof of an abandoned target.
            for (var parent = Directory.GetParent(path); parent != null; parent = parent.Parent)
            {
                try { if ((File.GetAttributes(parent.FullName) & FileAttributes.ReparsePoint) != 0) return new(path, "Bağlantı üzerinden hedef; inceleme gerekli", false); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            var attributes = File.GetAttributes(path);
            return new(path, (attributes & FileAttributes.Directory) == 0 ? "Hedef mevcut" : "Hedef bir klasör; inceleme gerekli", false);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            try
            {
                // 32-bit registrations expand %ProgramFiles% to Program Files (x86); either counterpart existing is not proof of absence.
                if (Counterparts(path).Any(p => File.Exists(p) || Directory.Exists(p))) return new(path, "32/64 bit karşılığı mevcut; inceleme gerekli", false);
                string relative = Path.GetRelativePath(Path.GetPathRoot(path)!, path);
                string[] parts = relative.Split(Path.DirectorySeparatorChar);
                if (parts.Length < 3) return new(path, e is DirectoryNotFoundException ? "Hedef yolu bulunamadı" : "Hedef dosya bulunamadı", true);
                if (e is DirectoryNotFoundException)
                    return new(path, "Hedef yolu bulunamadı", true, Directory.Exists(Path.Combine(Path.GetPathRoot(path)!, parts[0])));
                // A folder left without any program files is an uninstall leftover, not an update in progress.
                return NoProgramFiles(Path.GetDirectoryName(path)!) ? new(path, "Hedef dosya bulunamadı; klasörde program dosyası kalmamış", true, true) : new(path, "Hedef dosya bulunamadı", true);
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException or ArgumentException) { return new(path, "Doğrulanamadı: " + inner.Message, false); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { return new(path, "Doğrulanamadı: " + e.Message, false); }
    }
    static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".dll", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".msi", ".sys", ".ocx", ".cpl", ".scr", ".jar", ".py", ".asar", ".node" };
    static bool NoProgramFiles(string folder)
    {
        int seen = 0;
        try
        {
            // Attributes come with the enumeration; a link is reported before it would be entered, and stops the check.
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            {
                // Large or linked folders are left to manual review.
                if (++seen > 5000 || (entry.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                if (entry is FileInfo && ProgramExtensions.Contains(entry.Extension)) return false;
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }
    internal static IEnumerable<string> Counterparts(string path)
    {
        string native = Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (string.IsNullOrEmpty(native) || string.IsNullOrEmpty(x86) || native.Equals(x86, StringComparison.OrdinalIgnoreCase)) yield break;
        if (Safety.IsWithin(path, native)) yield return Path.Combine(x86, Path.GetRelativePath(native, path));
        else if (Safety.IsWithin(path, x86)) yield return Path.Combine(native, Path.GetRelativePath(x86, path));
    }
    static readonly string[] WindowsProgramFolders = ["Internet Explorer", "Common Files", "WindowsApps", "ModifiableWindowsApps", "dotnet", "Reference Assemblies", "MSBuild", "Uninstall Information"];
    // Windows components also live outside the Windows folder; their references are never cleaned here.
    internal static bool Protected(string target)
    {
        if (string.IsNullOrEmpty(target)) return true;
        try
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), AppContext.BaseDirectory,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft"),
                Path.Combine(local, "Microsoft"), Path.Combine(local, "Packages"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft") };
            if (roots.Any(root => !string.IsNullOrEmpty(root) && Safety.IsWithin(target, root))) return true;
            var programFiles = new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
            foreach (string? root in programFiles.Where(r => !string.IsNullOrEmpty(r)))
            {
                if (!Safety.IsWithin(target, root!)) continue;
                string first = Path.GetRelativePath(root!, target).Split(Path.DirectorySeparatorChar)[0];
                if (first.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) || first.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) || WindowsProgramFolders.Contains(first, StringComparer.OrdinalIgnoreCase)) return true;
            }
            string system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
            if (Safety.IsWithin(target, system))
            {
                string first = Path.GetRelativePath(system, target).Split(Path.DirectorySeparatorChar)[0];
                if (first.StartsWith('$') || new[] { "Recovery", "System Volume Information", "Boot", "EFI" }.Contains(first, StringComparer.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return true; }
    }
    internal static InvalidReference? RegistryItem(string category, string location, string name, string view, string value, RegistryValueKind kind, bool allowDelete, bool literal = false)
    {
        var probe = Probe(value, literal);
        if (probe.State == "Hedef mevcut") return null;
        bool canDelete = allowDelete && probe.Missing && !Protected(probe.Target);
        return new() { Category = category, Location = location, Name = name, View = view, Original = value, ValueKind = kind, Probe = probe, CanDelete = canDelete, Certain = canDelete && probe.ProgramGone, WindowsOwned = Protected(probe.Target) };
    }
    public static InvalidReport Scan(CancellationToken token, IProgress<ScanProgress>? progress = null, Action<InvalidReference>? onFound = null)
    {
        var result = new List<InvalidReference>(); var warnings = new List<string>(); long count = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // HKCU Run/Uninstall and most of Software\Classes are shared by both registry views, so one key is listed once.
        var keys = new HashSet<(string, string, string, string)>(); var unique = new List<InvalidReference>(); int announced = 0;
        void Announce()
        {
            for (; announced < result.Count; announced++)
            {
                var item = result[announced];
                if (keys.Add((item.Category, item.Location.ToUpperInvariant(), item.Name.ToUpperInvariant(), item.Original))) { unique.Add(item); onFound?.Invoke(item); }
            }
        }
        void Tick(string stage) { token.ThrowIfCancellationRequested(); count++; Announce(); if (watch.ElapsedMilliseconds > 100) { progress?.Report(new(count, null, stage)); watch.Restart(); } }
        // Registry branches are walked by several workers; shared state (results, warnings, progress) is only touched under this lock.
        var gate = new object();
        void Branch(RegistryKey root, string prefix, string view, string start, string category)
        {
            RegistryWalker.Walk(root, start, token, (key, path, depth) =>
            {
                lock (gate) Tick(category);
                if (key.GetValueNames().Contains("SymbolicLinkValue", StringComparer.OrdinalIgnoreCase)) return false;
                var local = new List<InvalidReference>();
                    string? name = category switch
                    {
                        "Kaldırma" => "UninstallString",
                        "Servis" => "ImagePath",
                        "Registry" when path.EndsWith("\\InprocServer32", StringComparison.OrdinalIgnoreCase) || path.EndsWith("\\LocalServer32", StringComparison.OrdinalIgnoreCase) || path.EndsWith("\\command", StringComparison.OrdinalIgnoreCase) => "",
                        "Uygulama yolu" when path != start => "",
                        _ => null
                    };
                    var names = category == "Başlangıç" ? key.GetValueNames() : name == null ? [] : new[] { name };
                    bool allowDelete = category switch
                    {
                        "Başlangıç" or "Registry" or "Uygulama yolu" => true,
                        // Only stand-alone Win32 services; drivers and shared-process services stay review-only.
                        "Servis" => path.Count(c => c == '\\') == start.Count(c => c == '\\') + 1 && key.GetValue("Type") is int type && type is 0x10 or 0x20,
                        _ => false
                    };
                    foreach (string valueName in names)
                    {
                        if (key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string value) continue;
                        // Services may run under another profile, so per-user variables cannot prove absence.
                        bool allow = allowDelete && !(category == "Servis" && value.Contains('%'));
                        var item = RegistryItem(category, prefix + "\\" + path, valueName, view, value, key.GetValueKind(valueName), allow, category == "Uygulama yolu" || path.EndsWith("\\InprocServer32", StringComparison.OrdinalIgnoreCase));
                        if (item != null) local.Add(item);
                    }
                if (local.Count > 0) lock (gate) result.AddRange(local);
                return category != "Başlangıç";
            },
            (path, e) => { lock (gate) warnings.Add(path + ": " + e.Message); },
            // In HKLM's 64-bit view Classes\Wow6432Node is the 32-bit hive, which the Registry32 pass lists under its real paths.
            prefix == "HKLM" && view == "Registry64" ? child => child.Equals("Wow6432Node", StringComparison.OrdinalIgnoreCase) : null);
        }
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            // HKCU's 32-bit view shows the same keys (its redirected Classes\Wow6432Node is walked in the 64-bit pass).
            if (hive == RegistryHive.CurrentUser && view == RegistryView.Registry32) continue;
            using var root = RegistryKey.OpenBaseKey(hive, view); string prefix = hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
            foreach (string run in new[] { "Run", "RunOnce" }) Branch(root, prefix, view.ToString(), @"Software\Microsoft\Windows\CurrentVersion\" + run, "Başlangıç");
            Branch(root, prefix, view.ToString(), @"Software\Microsoft\Windows\CurrentVersion\Uninstall", "Kaldırma");
            Branch(root, prefix, view.ToString(), @"Software\Microsoft\Windows\CurrentVersion\App Paths", "Uygulama yolu");
            Branch(root, prefix, view.ToString(), @"Software\Classes", "Registry");
            Branch(root, prefix, view.ToString(), StartupTools.Disabled, "Başlangıç");
        }
        using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)) Branch(machine, "HKLM", "Registry64", @"SYSTEM\CurrentControlSet\Services", "Servis");
        // Windows Settings can retain a disabled item after its Run command is gone.
        HashSet<string>? everywhere = null;
        try { everywhere = AllStartupNames(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { warnings.Add(L.T("Başlangıç kayıtları: ", "Startup entries: ") + e.Message); }
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (string group in hive == RegistryHive.LocalMachine ? new[] { "Run", "Run32" } : new[] { "Run" })
        {
            Tick(L.T("Eski başlangıç durumları", "Old startup states"));
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                string subkey = Approved + group;
                using var approved = root.OpenSubKey(subkey);
                var names = StartupNames(hive, group == "Run32" ? RegistryView.Registry32 : RegistryView.Registry64);
                foreach (string name in approved?.GetValueNames() ?? [])
                {
                    if (names.Contains(name) || approved!.GetValue(name) is not byte[] bytes) continue;
                    bool? enabled = StartupTools.DecodeApproval(bytes);
                    // A disabled state may still matter if the program re-creates its Run value later.
                    string state = enabled == false ? "Başlangıç komutu silinmiş; devre dışı durum kaydı kalmış" : "Başlangıç komutu silinmiş; Windows durum kaydı kalmış";
                    result.Add(new() { Category = "Başlangıç durumu", Location = (hive == RegistryHive.CurrentUser ? "HKCU\\" : "HKLM\\") + subkey, Name = name, Original = Convert.ToBase64String(bytes), ValueKind = RegistryValueKind.Binary, Probe = new(name, state, true), CanDelete = true, Certain = enabled == true && everywhere != null && !everywhere.Contains(name) });
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { warnings.Add(L.T("Başlangıç durumları: ", "Startup states: ") + e.Message); }
        }
        // Startup folder shortcuts keep their enabled/disabled state under StartupApproved\StartupFolder.
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            Tick(L.T("Eski başlangıç durumları", "Old startup states"));
            try
            {
                string folder = StartupFolder(hive);
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                string subkey = Approved + "StartupFolder";
                using var approved = root.OpenSubKey(subkey);
                foreach (string name in approved?.GetValueNames() ?? [])
                {
                    if (!ValidFileName(name) || File.Exists(Path.Combine(folder, name)) || Directory.Exists(Path.Combine(folder, name)) || approved!.GetValue(name) is not byte[] bytes) continue;
                    bool? enabled = StartupTools.DecodeApproval(bytes);
                    string state = enabled == false ? "Başlangıç klasörü öğesi silinmiş; devre dışı durum kaydı kalmış" : "Başlangıç klasörü öğesi silinmiş; Windows durum kaydı kalmış";
                    result.Add(new() { Category = "Başlangıç durumu", Location = (hive == RegistryHive.CurrentUser ? "HKCU\\" : "HKLM\\") + subkey, Name = name, Original = Convert.ToBase64String(bytes), ValueKind = RegistryValueKind.Binary, Probe = new(Path.Combine(folder, name), state, true), CanDelete = true, Certain = enabled == true });
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { warnings.Add(L.T("Başlangıç klasörü durumları: ", "Startup folder states: ") + e.Message); }
        }
        // Desktops often hold whole projects (hundreds of thousands of files); shortcuts there live near the top, so only a few levels are searched.
        var shortcutRoots = new[] { (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), 3), (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), 3), (Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), 16), (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), 16) };
        foreach (var (root, depth) in shortcutRoots.DistinctBy(r => r.Item1, StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            Walk(root, "*.lnk", depth, path =>
            {
                Tick(L.T("Kısayollar", "Shortcuts"));
                try
                {
                    string target = ReadShortcut(path); var probe = Probe(target, true);
                    bool canDelete = probe.Missing && !Protected(probe.Target);
                    if (probe.State != "Hedef mevcut") result.Add(new() { Category = "Kısayol", Location = path, Original = target, Modified = File.GetLastWriteTimeUtc(path), Probe = probe, CanDelete = canDelete, Certain = canDelete && probe.ProgramGone, WindowsOwned = Protected(probe.Target) });
                }
                catch (Exception e) { warnings.Add(path + ": " + e.Message); }
            }, token, warnings);
        }
        string tasks = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");
        Walk(tasks, "*", 32, path =>
        {
            Tick(L.T("Zamanlanmış görevler", "Scheduled tasks"));
            try { result.AddRange(TaskItems(tasks, path)); }
            catch (Exception e) { warnings.Add(path + ": " + e.Message); }
        }, token, warnings);
        Announce();
        progress?.Report(new(count, count, L.T("Başvuru taraması tamamlandı", "Reference scan complete")));
        return new(unique, warnings);
    }
    const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
    static string StartupFolder(RegistryHive hive) => Environment.GetFolderPath(hive == RegistryHive.CurrentUser ? Environment.SpecialFolder.Startup : Environment.SpecialFolder.CommonStartup);
    static bool ValidFileName(string name) => name.Length > 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name is not ("." or "..");
    sealed record TaskInfo(string Name, int Actions, List<string> Commands);
    static TaskInfo ReadTask(string tasksRoot, string path)
    {
        using var reader = System.Xml.XmlReader.Create(path, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
        var xml = XDocument.Load(reader);
        var actions = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "Actions")?.Elements().ToList() ?? [];
        var commands = actions.Where(e => e.Name.LocalName == "Exec").SelectMany(e => e.Elements().Where(c => c.Name.LocalName == "Command")).Select(c => c.Value).ToList();
        return new("\\" + Path.GetRelativePath(tasksRoot, path), actions.Count, commands);
    }
    // Windows' own tasks live under \Microsoft\; a task is removable only when its single action points at a missing program.
    internal static IEnumerable<InvalidReference> TaskItems(string tasksRoot, string path)
    {
        var task = ReadTask(tasksRoot, path);
        bool windowsTask = task.Name.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase);
        foreach (string command in task.Commands)
        {
            var probe = Probe(command, true);
            if (probe.State == "Hedef mevcut") continue;
            bool canDelete = !windowsTask && task.Actions == 1 && !command.Contains('%') && probe.Missing && !Protected(probe.Target);
            yield return new() { Category = "Görev", Location = path, Name = task.Name, Original = command, Modified = File.GetLastWriteTimeUtc(path), Probe = probe, CanDelete = canDelete, Certain = canDelete && probe.ProgramGone, WindowsOwned = windowsTask || Protected(probe.Target) };
        }
    }
    static HashSet<string> StartupNames(RegistryHive hive, RegistryView view)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var root = RegistryKey.OpenBaseKey(hive, view);
        foreach (string path in new[] { @"Software\Microsoft\Windows\CurrentVersion\Run", StartupTools.Disabled })
        {
            using var key = root.OpenSubKey(path);
            foreach (string name in key?.GetValueNames() ?? []) names.Add(name);
        }
        return names;
    }
    static HashSet<string> AllStartupNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            names.UnionWith(StartupNames(hive, view));
        return names;
    }
    // Native recursive enumeration never follows links or cloud-only files, so only the root itself needs the link check.
    static void Walk(string root, string pattern, int depth, Action<string> file, CancellationToken token, List<string> warnings)
    {
        try
        {
            DiskTools.NoLinks(root);
            foreach (string path in Directory.EnumerateFiles(root, pattern, DiskTools.Local(depth))) { token.ThrowIfCancellationRequested(); file(path); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add(root + ": " + e.Message); }
    }
    // Windows' own shell-link object, created once per thread: creating a WScript.Shell per shortcut took tens of seconds on a full Start menu.
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class ShellLink;
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW { void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int size, IntPtr findData, uint flags); }
    [ThreadStatic] static object? shellLink;
    static string ReadShortcut(string path)
    {
        shellLink ??= new ShellLink();
        ((System.Runtime.InteropServices.ComTypes.IPersistFile)shellLink).Load(path, 0);
        var target = new System.Text.StringBuilder(1024);
        ((IShellLinkW)shellLink).GetPath(target, target.Capacity, IntPtr.Zero, 0);
        return target.ToString();
    }
    public static void Delete(InvalidReference item)
    {
        if (!item.CanDelete || !item.Probe.Missing) throw new IOException(L.T("Bu bulgu yalnızca inceleme içindir.", "This finding is for review only."));
        if (item.Category == "Başlangıç durumu")
        {
            var (approvalRoot, approvalSubkey) = Cleaner.OpenRegistry(item.Location, "Registry64");
            using (approvalRoot)
            {
                var hive = item.Location.StartsWith("HKCU\\") ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
                if (approvalSubkey == Approved + "StartupFolder")
                {
                    string folder = StartupFolder(hive), file = Path.Combine(folder, item.Name);
                    if (!ValidFileName(item.Name) || !Directory.Exists(folder) || !file.Equals(item.Target, StringComparison.OrdinalIgnoreCase)) throw new IOException(L.T("Başlangıç klasörü doğrulanamadı.", "The startup folder could not be verified."));
                    if (File.Exists(file) || Directory.Exists(file)) throw new IOException(L.T("Başlangıç klasörü öğesi yeniden oluşmuş; silinmedi.", "The startup folder item has reappeared; not deleted."));
                }
                else
                {
                    if (approvalSubkey != Approved + "Run" && approvalSubkey != Approved + "Run32") throw new IOException(L.T("Durum kaydı kapsam dışında.", "The state entry is out of scope."));
                    if (StartupNames(hive, approvalSubkey.EndsWith("Run32") ? RegistryView.Registry32 : RegistryView.Registry64).Contains(item.Name)) throw new IOException(L.T("Başlangıç komutu yeniden oluşmuş; silinmedi.", "The startup command has reappeared; not deleted."));
                    if (item.Certain && AllStartupNames().Contains(item.Name)) throw new IOException(L.T("Aynı adlı başlangıç kaydı oluşmuş; silinmedi.", "A startup entry with the same name has appeared; not deleted."));
                }
                using var approved = approvalRoot.OpenSubKey(approvalSubkey, true);
                if (approved?.GetValue(item.Name) is not byte[] bytes || Convert.ToBase64String(bytes) != item.Original) throw new IOException(L.T("Durum kaydı değişmiş; yeniden tara.", "The state entry has changed; scan again."));
                approved.DeleteValue(item.Name);
                if (approved.GetValueNames().Contains(item.Name, StringComparer.OrdinalIgnoreCase)) throw new IOException(L.T("Durum kaydı hâlâ mevcut.", "The state entry still exists."));
            }
            return;
        }
        bool literal = item.Category is "Kısayol" or "Uygulama yolu" or "Görev" || item.Location.EndsWith("\\InprocServer32", StringComparison.OrdinalIgnoreCase);
        var probe = Probe(item.Original, literal);
        if (!probe.Missing || Protected(probe.Target) || !probe.Target.Equals(item.Target, StringComparison.OrdinalIgnoreCase)) throw new IOException(L.T("Hedef artık eksik değil veya doğrulanamadı; yeniden tara.", "The target is no longer missing or could not be verified; scan again."));
        if (item.Certain && !probe.ProgramGone) throw new IOException(L.T("Hedef klasörü yeniden oluşmuş; yeniden tara.", "The target folder has reappeared; scan again."));
        if (item.Category == "Kısayol")
        {
            DiskTools.NoLinks(Path.GetDirectoryName(item.Location)!);
            if ((File.GetAttributes(item.Location) & FileAttributes.ReparsePoint) != 0 || File.GetLastWriteTimeUtc(item.Location) != item.Modified || ReadShortcut(item.Location) != item.Original) throw new IOException(L.T("Kısayol değişmiş; silinmedi.", "The shortcut has changed; not deleted."));
            File.Delete(item.Location); return;
        }
        if (item.Category == "Görev") { DeleteTask(item); return; }
        if (item.Category == "Servis") { DeleteService(item); return; }
        if (item.Category is not ("Başlangıç" or "Registry" or "Uygulama yolu")) throw new IOException(L.T("Bu kayıt türü silinmez.", "This entry type is not deleted."));
        var (root, subkey) = Cleaner.OpenRegistry(item.Location, item.View);
        using (root)
        {
            bool validScope = item.Category switch
            {
                "Başlangıç" => subkey.Equals(@"Software\Microsoft\Windows\CurrentVersion\Run", StringComparison.OrdinalIgnoreCase) || subkey.Equals(@"Software\Microsoft\Windows\CurrentVersion\RunOnce", StringComparison.OrdinalIgnoreCase) || subkey.Equals(StartupTools.Disabled, StringComparison.OrdinalIgnoreCase),
                "Uygulama yolu" => subkey.StartsWith(@"Software\Microsoft\Windows\CurrentVersion\App Paths\", StringComparison.OrdinalIgnoreCase) && item.Name == "",
                "Registry" => subkey.StartsWith(@"Software\Classes\", StringComparison.OrdinalIgnoreCase) && item.Name == "" && new[] { "\\InprocServer32", "\\LocalServer32", "\\command" }.Any(s => subkey.EndsWith(s, StringComparison.OrdinalIgnoreCase)),
                _ => false
            };
            if (!validScope) throw new IOException(L.T("Registry konumu temizleme kapsamı dışında.", "The Registry location is outside the cleanup scope."));
            using var key = root.OpenSubKey(subkey, true);
            if (key == null) return;
            if (key.GetValue(item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string != item.Original || key.GetValueKind(item.Name) != item.ValueKind) throw new IOException(L.T("Registry değeri değişmiş; silinmedi.", "The Registry value has changed; not deleted."));
            key.DeleteValue(item.Name);
            if (key.GetValueNames().Contains(item.Name, StringComparer.OrdinalIgnoreCase)) throw new IOException(L.T("Kayıt hâlâ mevcut.", "The entry still exists."));
        }
    }
    static void DeleteTask(InvalidReference item)
    {
        string tasksRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");
        if (!Safety.IsWithin(item.Location, tasksRoot)) throw new IOException(L.T("Görev konumu kapsam dışında.", "The task location is out of scope."));
        DiskTools.NoLinks(Path.GetDirectoryName(item.Location)!);
        if ((File.GetAttributes(item.Location) & FileAttributes.ReparsePoint) != 0 || File.GetLastWriteTimeUtc(item.Location) != item.Modified) throw new IOException(L.T("Görev değişmiş; yeniden tara.", "The task has changed; scan again."));
        var task = ReadTask(tasksRoot, item.Location);
        if (task.Name != item.Name || task.Name.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase) || task.Actions != 1 || task.Commands.Count != 1 || task.Commands[0] != item.Original || item.Original.Contains('%')) throw new IOException(L.T("Görev silme koşullarını karşılamıyor.", "The task does not meet the deletion conditions."));
        // schtasks also removes the TaskCache registration; deleting the XML file alone would leave Windows inconsistent.
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "/Delete", "/TN", task.Name, "/F" }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException(L.T("schtasks başlatılamadı.", "schtasks could not be started."));
        var error = process.StandardError.ReadToEndAsync(); process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(30000)) { process.Kill(); throw new IOException(L.T("Görev silme zaman aşımına uğradı.", "Task deletion timed out.")); }
        if (process.ExitCode != 0) throw new IOException(L.T("Görev silinemedi: ", "The task could not be deleted: ") + error.Result.Trim());
        if (File.Exists(item.Location)) throw new IOException(L.T("Görev hâlâ mevcut.", "The task still exists."));
    }
    const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";
    static void DeleteService(InvalidReference item)
    {
        const string prefix = @"HKLM\" + ServicesKey + @"\";
        string service = item.Location.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? item.Location[prefix.Length..] : "";
        if (service.Length == 0 || service.Contains('\\') || item.Name != "ImagePath" || item.View != "Registry64" || item.Original.Contains('%')) throw new IOException(L.T("Servis kaydı kapsam dışında.", "The service entry is out of scope."));
        using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        {
            using var key = machine.OpenSubKey(ServicesKey + "\\" + service) ?? throw new IOException(L.T("Servis artık yok.", "The service no longer exists."));
            if (key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string != item.Original || key.GetValueKind("ImagePath") != item.ValueKind) throw new IOException(L.T("Servis yolu değişmiş; silinmedi.", "The service path has changed; not deleted."));
            if (key.GetValue("Type") is not (0x10 or 0x20)) throw new IOException(L.T("Sürücü veya paylaşılan servis silinmez.", "Drivers and shared services are not deleted."));
            using var services = machine.OpenSubKey(ServicesKey) ?? throw new IOException(L.T("Servis listesi okunamadı.", "The service list could not be read."));
            foreach (string other in services.GetSubKeyNames())
            {
                using var dependent = services.OpenSubKey(other);
                if (dependent?.GetValue("DependOnService") is string[] dependencies && dependencies.Contains(service, StringComparer.OrdinalIgnoreCase)) throw new IOException(L.T($"'{other}' servisi buna bağlı; silinmedi.", $"The '{other}' service depends on it; not deleted."));
            }
        }
        IntPtr manager = OpenSCManagerW(null, null, 0x0001), handle = IntPtr.Zero;
        if (manager == IntPtr.Zero) throw new Win32Exception();
        try
        {
            handle = OpenServiceW(manager, service, 0x00010000 | 0x0004);
            if (handle == IntPtr.Zero) throw new Win32Exception();
            if (!QueryServiceStatus(handle, out var status)) throw new Win32Exception();
            if (status.CurrentState != 1) throw new IOException(L.T("Servis çalışıyor; silinmedi.", "The service is running; not deleted."));
            if (!DeleteServiceNative(handle)) throw new Win32Exception();
        }
        finally { if (handle != IntPtr.Zero) CloseServiceHandle(handle); CloseServiceHandle(manager); }
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ServiceStatus { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint; }
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "DeleteService")] static extern bool DeleteServiceNative(IntPtr service);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool CloseServiceHandle(IntPtr handle);
}
