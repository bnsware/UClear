using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace UClear;
// A class, not a record: selection is mutable and must not change identity inside list views.
public sealed class LeftoverCandidate(string name, string path, long bytes, bool sizeComplete, DateTime modified, string confidence, string reason, bool recent = false) : System.ComponentModel.INotifyPropertyChanged
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    public long Bytes { get; } = bytes;
    public bool SizeComplete { get; } = sizeComplete;
    public DateTime Modified { get; } = modified;
    public string Confidence { get; } = confidence;
    public string Reason { get; } = reason;
    public bool Recent { get; } = recent;
    // A folder that still holds a working program is never removed in bulk.
    public bool CanRemove => Confidence != "Kayıtsız program";
    private bool selected;
    public bool Selected { get => selected; set { selected = value && CanRemove; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public string SizeText => (SizeComplete ? "" : "≥ ") + Finding.FormatBytes(Bytes);
    public string Summary => $"{L.V(Confidence)} · {SizeText} · {Modified.ToLocalTime():dd.MM.yyyy}" + (Recent ? L.T(" · yeni kullanılmış", " · recently used") : "");
    // Left-behind program folders first, then settings/data folders; recently used ones and folders that still hold a working program go last.
    public int Rank => Confidence == "Kayıtsız program" ? 3 : Recent ? 2 : Confidence == "Program klasörü" ? 0 : 1;
}
// Finds application folders that no installed program, service, startup entry or running process accounts for.
// It only proposes names; removal still goes through the normal leftover scan and its safety checks.
public static class LeftoverFinder
{
    static readonly string[] Ignored = ["Packages", "Temp", "CrashDumps", "D3DSCache", "ConnectedDevicesPlatform", "Comms", "PeerDistRepub", "Publishers", "History", "Programs", "Package Cache", "SoftwareDistribution", "USOPrivate", "USOShared", "ssh", "regid.1991-06.com.microsoft", "PlaceholderTileLogoFolder", "IsolatedStorage", "VirtualStore", "fontconfig", "Downloaded Installations", "Desktop", "Documents", "Application Data", "Start Menu", "Templates", "Favorites", "WindowsPowerShell", "PowerShell", "Common Files", "Uninstall Information", "Reference Assemblies", "ModifiableWindowsApps", "WindowsApps", "dotnet", "MSBuild", "Installer", "Internet Explorer",
        // Windows / Windows Update components that live outside the Windows folder.
        "RUXIM", "PCHealthCheck", "VulkanRT", "Windows Kits", "IIS", "IIS Express", "MSECache", "DataSharing", "ElevatedDiagnostics", "Diagnostics", "SquirrelTemp", "assembly", "Speech", "Sysinternals", "wsl", "lxss"];
    // Temp and generated folders (tw-1a2.tmp, GUIDs, hashes) are not program leftovers; the temp tool handles them.
    static readonly Regex Generated = new(@"\.tmp$|^tmp|^~|^\{?[0-9a-f]{8}-[0-9a-f]{4}-|^[0-9a-f\-_.]{8,}$|^[0-9_.\-]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Generic folder names say nothing about which product they belong to; a scan by such a name could hit unrelated programs.
    static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase) { "logs", "log", "cache", "caches", "data", "config", "configs", "settings", "temp", "tmp", "backup", "backups", "user", "users", "update", "updates", "updater", "bin", "lib", "libs", "plugins", "resources", "crash", "crashes", "crashpad", "dumps", "profiles", "profile", "storage", "local", "roaming", "shared", "common", "tools", "service", "services", "setup", "install", "installer", "downloads", "default", "internet explorer", "online services", "crashreports", "crash reports", "reports", "telemetry", "analytics", "license", "licenses", "packages", "modules", "components", "runtime", "drivers", "firmware", "assets", "media", "images", "icons", "fonts", "locales", "web", "browser", "db", "database", "state", "session", "sessions", "history", "work", "workspace", "projects", "files", "documents", "output", "debug", "release", "x64", "x86", "win64", "win32", "versions", "themes", "dictionaries", "partitions", "network", "sentry", "gpucache", "local storage", "session storage", "blob_storage", "indexeddb", "service worker", "webstorage", "shared dictionary", "shared_proto_db", "code cache", "dawncache", "dawngraphitecache", "dawnwebgpucache", "videodecodestats", "component_crx_cache", "widevinecdm", "mediafoundationwidevinecdm", "module_data" };
    sealed record Owners(HashSet<string> Names, HashSet<string> Publishers, List<string> Paths, HashSet<string> Vendors);
    public static List<string> Roots()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Safety.Roots().Append(Path.Combine(local, "Programs")).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
    static bool InstallRoot(string root) => root.EndsWith(@"\Programs", StringComparison.OrdinalIgnoreCase) || root.Equals(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase) || root.Equals(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), StringComparison.OrdinalIgnoreCase);
    public static void Find(CancellationToken token, Action<LeftoverCandidate> onFound, List<string> warnings, IProgress<ScanProgress>? progress = null)
    {
        var roots = Roots();
        progress?.Report(new(0, null, L.T("Yüklü programlar okunuyor", "Reading installed programs")));
        var owners = Collect(Registered(warnings), roots, warnings);
        int checkedCount = 0;
        void Inspect(string directory, string root, bool allowPublisher)
        {
            token.ThrowIfCancellationRequested();
            if (++checkedCount % 16 == 0) progress?.Report(new(checkedCount, null, L.T("Kalıntı adayları aranıyor", "Searching for leftover candidates")));
            string name = Path.GetFileName(directory);
            if (name.StartsWith('.') || name.StartsWith('$') || Generated.IsMatch(name) || Ignored.Contains(name, StringComparer.OrdinalIgnoreCase) || Generic.Contains(name) || !Safety.ValidName(name) || name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase)) return;
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || !Safety.SafeDirectory(directory, roots)) return;
            var owner = Owner(name, directory, owners, allowPublisher);
            if (owner == "app") return;
            if (owner == "publisher")
            {
                // A known vendor folder can still hold a product that was uninstalled.
                try { foreach (string child in Directory.EnumerateDirectories(directory)) Inspect(child, root, false); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add(L.T("Okunamadı: ", "Could not read: ") + directory); }
                return;
            }
            var (bytes, complete, program) = Size(directory, token);
            var modified = Directory.GetLastWriteTimeUtc(directory);
            bool recent = modified > DateTime.UtcNow.AddDays(-3);
            bool install = InstallRoot(root);
            string confidence = program != null ? "Kayıtsız program" : install ? "Program klasörü" : "Veri klasörü";
            string reason = program != null
                ? L.T($"Klasörde çalışır program dosyası var ({program}). Program kaldırılmamış, yalnızca Windows'ta kaldırma kaydı yok olabilir (kopyalanmış veya taşınabilir program). Silersen program çalışmaz; emin değilsen dokunma.", $"The folder contains a working program file ({program}). The program may not be uninstalled at all, just missing its Windows uninstall entry (copied or portable program). Deleting it will break the program; leave it alone if unsure.")
                : (install ? L.T("Programın kurulum klasörü kalmış; bu ada kayıtlı yüklü bir program yok.", "The program's install folder was left behind; no installed program is registered under this name.") : L.T("Programın ayar/veri klasörü kalmış; yüklü hiçbir programla eşleşmedi. Oyun kayıtları veya ayarlar içerebilir.", "The program's settings/data folder was left behind; it matches no installed program. It may contain game saves or settings.")) + (recent ? L.T(" Son 3 gün içinde değişmiş; hâlâ kullanılan taşınabilir bir programa ait olabilir.", " Changed within the last 3 days; it may belong to a portable program still in use.") : "");
            onFound(new(name, directory, bytes, complete, modified, confidence, reason, recent));
        }
        foreach (string root in roots)
        {
            try { foreach (string directory in Directory.EnumerateDirectories(root)) { try { Inspect(directory, root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add(L.T("Okunamadı: ", "Could not read: ") + directory); } } }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add(L.T("Okunamadı: ", "Could not read: ") + root); }
        }
        progress?.Report(new(checkedCount, checkedCount, L.T("Kalıntı araması tamamlandı", "Leftover search complete")));
    }
    internal static string Owner(string name, string directory, IEnumerable<InstalledApp> apps, IEnumerable<string>? vendors = null)
    {
        var warnings = new List<string>();
        var owners = Collect(apps, Roots(), warnings, includeSystem: false);
        foreach (string vendor in vendors ?? []) owners.Vendors.Add(Safety.Normalize(vendor));
        return Owner(name, directory, owners, true);
    }
    static readonly string[] ProfileMarkers = ["Local Storage", "Session Storage", "GPUCache", "blob_storage", "IndexedDB", "Code Cache", "Preferences", "User Data", "Versions"];
    // A Chromium/Electron profile or an app's own version store is one program's data; its subfolders are never separate products.
    static bool AppData(string directory)
    {
        try { return ProfileMarkers.Any(m => Directory.Exists(Path.Combine(directory, m)) || File.Exists(Path.Combine(directory, m))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return true; }
    }
    static bool Related(string folder, string token) => folder == token || (folder.Length >= 4 && token.StartsWith(folder, StringComparison.Ordinal)) || (token.Length >= 4 && folder.StartsWith(token, StringComparison.Ordinal));
    static string Owner(string name, string directory, Owners owners, bool allowPublisher)
    {
        string n = Safety.Normalize(name);
        foreach (string path in owners.Paths)
            if (Safety.Canonical(path).Equals(Safety.Canonical(directory), StringComparison.OrdinalIgnoreCase) || Safety.IsWithin(path, directory) || Safety.IsWithin(directory, path)) return "app";
        // Hardware vendors' folders (Intel, NVIDIA, Realtek…) serve drivers that have no uninstall entry; never propose them.
        if (owners.Vendors.Any(t => Related(n, t))) return "app";
        // "discord" is Discord's own folder even though the publisher is "Discord Inc."; its contents are app data, not products.
        if (owners.Names.Contains(n)) return "app";
        // A pure vendor folder ("Google", "JetBrains") is opened to look for removed products, even though "Google Chrome" also starts with it.
        if (owners.Publishers.Any(t => t.StartsWith(n, StringComparison.Ordinal) && (n == t || n.Length >= 4))) return allowPublisher && !AppData(directory) ? "publisher" : "app";
        foreach (string t in owners.Names)
            if (Related(n, t) || (n.Length >= 5 && t.Contains(n, StringComparison.Ordinal))) return "app";
        // "Mozilla Maintenance Service" of an installed vendor is owned.
        if (owners.Publishers.Any(t => Related(n, t))) return "app";
        return "";
    }
    // Every uninstall entry counts, including hidden system components and entries without an uninstaller.
    static List<InstalledApp> Registered(List<string> warnings)
    {
        var result = new List<InstalledApp>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(Inventory.Uninstall);
                foreach (string id in uninstall?.GetSubKeyNames() ?? [])
                {
                    using var key = uninstall!.OpenSubKey(id); if (key == null) continue;
                    string Read(string value) => key.GetValue(value)?.ToString()?.Trim() ?? "";
                    result.Add(new(Read("DisplayName").Length > 0 ? Read("DisplayName") : id, Read("Publisher"), "", Read("InstallLocation"), Read("UninstallString"), "", view.ToString(), 0, "", Read("DisplayIcon")));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { warnings.Add(L.T("Kaldırma kayıtları okunamadı: ", "Uninstall entries could not be read: ") + e.Message); }
        }
        return result;
    }
    static Owners Collect(IEnumerable<InstalledApp> apps, List<string> roots, List<string> warnings, bool includeSystem = true)
    {
        var names = new HashSet<string>(); var publishers = new HashSet<string>(); var paths = new List<string>(); var vendors = new HashSet<string>();
        void Name(string value) { string n = Safety.Normalize(Regex.Replace(value, @"[\d.]+$|\(.*?\)|x64|x86|64-bit|32-bit", "", RegexOptions.IgnoreCase)); if (n.Length >= 3) names.Add(n); }
        void Publisher(string value)
        {
            string n = Safety.Normalize(value); if (n.Length >= 3) publishers.Add(n);
            string first = Safety.Normalize(value.Split(' ', ',', '.')[0]); if (first.Length >= 4) publishers.Add(first);
        }
        // A path only proves ownership when it is more specific than a scan root (bad registry data may point at Program Files itself).
        void PathOf(string? value)
        {
            string? path = ExecutableDirectory(value);
            if (path == null) return;
            try
            {
                path = Safety.Canonical(path);
                if (roots.Any(r => Safety.Canonical(r).Equals(path, StringComparison.OrdinalIgnoreCase) || Safety.IsWithin(r, path))) return;
                if (path.Count(c => c == '\\') < 2) return;
                paths.Add(path);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        // Folder and executable names identify a product too ("Microsoft VS Code" installs Code.exe and keeps data in "Code").
        void FileName(string? command, bool directory)
        {
            string? path = directory ? command?.Trim('"') : ExecutablePath(command);
            if (string.IsNullOrWhiteSpace(path)) return;
            string name = directory ? Path.GetFileName(path.TrimEnd('\\')) : Path.GetFileNameWithoutExtension(path);
            if (Regex.IsMatch(name, @"^(unins\d*|uninst.*|uninstall.*|setup|install.*|update.*|icon.*|app|main|launcher|helper|msiexec)$", RegexOptions.IgnoreCase) || Generic.Contains(name)) return;
            Name(name);
        }
        foreach (var app in apps)
        {
            Name(app.Name); Publisher(app.Publisher);
            if (!string.IsNullOrWhiteSpace(app.Location)) { PathOf('"' + app.Location.Trim('"') + '"'); FileName(app.Location, true); }
            PathOf(app.UninstallCommand); PathOf(app.DisplayIcon.Split(',')[0]);
            FileName(app.DisplayIcon.Split(',')[0], false); FileName(ExecutableDirectory(app.DisplayIcon.Split(',')[0]), true); FileName(ExecutableDirectory(app.UninstallCommand), true);
        }
        if (!includeSystem) return new(names, publishers, paths, vendors);
        try
        {
            using var classes = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class");
            foreach (string cls in classes?.GetSubKeyNames() ?? [])
            {
                using var deviceClass = classes!.OpenSubKey(cls);
                foreach (string device in deviceClass?.GetSubKeyNames() ?? [])
                {
                    try
                    {
                        using var key = deviceClass!.OpenSubKey(device);
                        if (key?.GetValue("ProviderName") is string provider && !provider.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase))
                        {
                            string n = Safety.Normalize(provider); if (n.Length >= 3) vendors.Add(n);
                            string first = Safety.Normalize(provider.Split(' ', ',', '.')[0]); if (first.Length >= 3) vendors.Add(first);
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { warnings.Add(L.T("Sürücü sağlayıcıları okunamadı: ", "Driver providers could not be read: ") + e.Message); }
        // Programs without an uninstall entry still count as present when Windows runs them.
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            foreach (string service in services?.GetSubKeyNames() ?? [])
            {
                using var key = services!.OpenSubKey(service);
                PathOf(key?.GetValue("ImagePath") as string);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { warnings.Add(L.T("Servisler okunamadı: ", "Services could not be read: ") + e.Message); }
        foreach (var entry in SafeStartup(warnings)) PathOf(entry.Command);
        foreach (string process in RunningPrograms()) PathOf('"' + process + '"');
        return new(names, publishers, paths, vendors);
    }
    static List<StartupEntry> SafeStartup(List<string> warnings)
    {
        try { return StartupTools.Read(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { warnings.Add(L.T("Başlangıç kayıtları okunamadı: ", "Startup entries could not be read: ") + e.Message); return []; }
    }
    static string? ExecutableDirectory(string? command)
    {
        string? path = ExecutablePath(command);
        return path == null ? null : Path.HasExtension(path) ? Path.GetDirectoryName(path) : path;
    }
    static string? ExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string value = Environment.ExpandEnvironmentVariables(command.Trim());
        string path;
        if (value.StartsWith('"')) { int end = value.IndexOf('"', 1); if (end < 0) return null; path = value[1..end]; }
        else { var match = Regex.Match(value, @"^(.+?\.(?:exe|dll|ico|bat|cmd|sys))\b", RegexOptions.IgnoreCase); if (!match.Success) return null; path = match.Groups[1].Value; }
        if (path.StartsWith(@"\??\")) path = path[4..];
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\")) return null;
        return path;
    }
    // Also reports the first working executable: an uninstaller removes its binaries, so a remaining one means the program itself is still there.
    static (long Bytes, bool Complete, string? Program) Size(string directory, CancellationToken token)
    {
        long bytes = 0; int seen = 0; string? program = null; var pending = new Stack<string>(); pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            try
            {
                foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    // Very large folders get a lower-bound size so the list keeps moving.
                    if (++seen > 20000) return (bytes, false, program);
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (entry is DirectoryInfo) { pending.Push(entry.FullName); continue; }
                    bytes += ((FileInfo)entry).Length;
                    if (program == null && entry.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(entry.Name, @"^(unins|uninst|uninstall)", RegexOptions.IgnoreCase)) program = entry.Name;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return (bytes, true, program);
    }
    internal static List<string> RunningPrograms()
    {
        var result = new List<string>();
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            using (process)
            {
                IntPtr handle = OpenProcess(0x1000, false, process.Id);
                if (handle == IntPtr.Zero) continue;
                try { var text = new StringBuilder(1024); int size = text.Capacity; if (QueryFullProcessImageNameW(handle, 0, text, ref size)) result.Add(text.ToString()); }
                finally { CloseHandle(handle); }
            }
        }
        return result;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
