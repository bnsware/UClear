using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UClear;

public static class Safety
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    { "windows", "microsoft", "software", "system", "system32", "program", "programs", "programfiles", "common", "commonfiles", "shared", "users", "user", "appdata", "local", "roaming", "temp", "packages", "packagecache", "windowsapps", "installer", "drivers", "uclear", "classes", "clsid", "appid", "typelib", "interface", "protocols", "registeredapplications", "policies", "run", "runonce", "uninstall" };
    public static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    public static bool ValidName(string value) => Normalize(value).Length >= 3 && !Reserved.Contains(Normalize(value));
    public static string Canonical(string path) => System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar);
    public static bool IsWithin(string path, string root) => Canonical(path).StartsWith(Canonical(root) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static bool SafeDirectory(string path, IEnumerable<string> roots)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || path.StartsWith(@"\\")) return false;
        string full = Canonical(path);
        if (!roots.Any(root => IsWithin(full, root))) return false;
        // Roots themselves, broad system folders and the running program can never be removed.
        if (roots.Any(root => Canonical(root).Equals(full, StringComparison.OrdinalIgnoreCase))) return false;
        if (!ValidName(System.IO.Path.GetFileName(full))) return false;
        foreach (string root in roots.Where(root => IsWithin(full, root)))
        {
            string first = System.IO.Path.GetRelativePath(root, full).Split(System.IO.Path.DirectorySeparatorChar)[0];
            if (new[] { "Microsoft", "WindowsApps", "Packages", "Package Cache", "Windows", "Common Files" }.Contains(first, StringComparer.OrdinalIgnoreCase)) return false;
        }
        string own = Canonical(AppContext.BaseDirectory);
        if (full.Equals(own, StringComparison.OrdinalIgnoreCase) || IsWithin(own, full)) return false;
        for (DirectoryInfo? current = new(full); current != null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
    public static List<string> Roots() => new[] {
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
    }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Canonical).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    public static bool Matches(string name, string target) => Normalize(name) == Normalize(target);
    public static bool PossiblyMatches(string name, string target) => Normalize(target).Length >= 5 && Normalize(name).StartsWith(Normalize(target), StringComparison.OrdinalIgnoreCase);
    public static bool References(string value, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;
        string normalized = Environment.ExpandEnvironmentVariables(value).Replace('/', '\\');
        return normalized.Contains(Canonical(directory) + "\\", StringComparison.OrdinalIgnoreCase);
    }
}

public static class Inventory
{
    internal const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    public static List<InstalledApp> Read()
    {
        var result = new List<InstalledApp>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(Uninstall);
            if (uninstall == null) continue;
            foreach (string id in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var key = uninstall.OpenSubKey(id);
                    if (key == null || key.GetValue("SystemComponent") is int n && n == 1) continue;
                    string Read(string name) => key.GetValue(name)?.ToString()?.Trim() ?? "";
                    string name = Read("DisplayName"), command = Read("UninstallString");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(command)) continue;
                    long.TryParse(Read("EstimatedSize"), out long kb);
                    result.Add(new(name, Read("Publisher"), Read("DisplayVersion"), Read("InstallLocation"), command,
                        $"{(hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU")}\\{Uninstall}\\{id}", view.ToString(), kb * 1024, Read("InstallDate"), Read("DisplayIcon"), Read("QuietUninstallString")));
                }
                catch (System.Security.SecurityException) { }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
        }
        return result.DistinctBy(x => (x.RegistryPath, x.UninstallCommand)).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
    public static ProcessStartInfo Uninstaller(string command)
    {
        string expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        string executable, arguments;
        if (expanded.StartsWith('"'))
        {
            int end = expanded.IndexOf('"', 1);
            if (end < 0) throw new InvalidOperationException(L.T("Kaldırıcı komutu geçersiz.", "The uninstall command is invalid."));
            executable = expanded[1..end]; arguments = expanded[(end + 1)..].Trim();
        }
        else
        {
            var match = Regex.Match(expanded, @"^(.+?\.exe)(?:\s+(.*))?$", RegexOptions.IgnoreCase);
            if (!match.Success) throw new InvalidOperationException(L.T("Kaldırıcı komutu güvenilir biçimde ayrıştırılamadı.", "The uninstall command could not be parsed reliably."));
            executable = match.Groups[1].Value; arguments = match.Groups[2].Value;
        }
        if (System.IO.Path.GetFileName(executable).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
        {
            executable = System.IO.Path.Combine(Environment.SystemDirectory, "msiexec.exe");
            arguments = Regex.Replace(arguments, @"/I(?=\s|\{)", "/X", RegexOptions.IgnoreCase);
        }
        else if (!System.IO.Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new InvalidOperationException(L.T("Kaldırıcı bulunamadı veya tam dosya yolu içermiyor. Kalıntı taramasını kullanabilirsin.", "The uninstaller was not found or has no full file path. You can use the leftover scan instead."));
        return new(executable, arguments) { UseShellExecute = true, WorkingDirectory = System.IO.Path.GetDirectoryName(executable) ?? "" };
    }
    // For bulk removal: the program's own silent command, or Windows Installer's no-UI mode; null means only the normal uninstaller window exists.
    public static ProcessStartInfo? QuietUninstaller(InstalledApp app)
    {
        if (!string.IsNullOrWhiteSpace(app.QuietUninstallCommand))
            try { return Uninstaller(app.QuietUninstallCommand); } catch (InvalidOperationException) { }
        try
        {
            var start = Uninstaller(app.UninstallCommand);
            if (!System.IO.Path.GetFileName(start.FileName).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase)) return null;
            if (!Regex.IsMatch(start.Arguments, @"(^|\s)/q", RegexOptions.IgnoreCase)) start.Arguments += " /qn /norestart";
            return start;
        }
        catch (InvalidOperationException) { return null; }
    }
    // Desktop programs plus the user's Microsoft Store apps.
    // Windows-owned Store packages are left out entirely: they can never be removed here.
    public static List<InstalledApp> ReadAll() => Read().Concat(StoreApps.Read().Where(a => !a.SystemPackage)).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
}

public sealed class Scanner
{
    private readonly List<string> roots;
    public Scanner(List<string>? allowedRoots = null) => roots = allowedRoots ?? Safety.Roots();
    public ScanResult Scan(Target target, CancellationToken token, IProgress<ScanProgress>? progress = null, Action<Finding>? onFound = null)
        => ScanMany([target], token, progress, onFound)[0];
    // Several programs at once: names and folders are matched per program, but the slow content-based Registry walk is shared by all of them.
    public List<ScanResult> ScanMany(IReadOnlyList<Target> targets, CancellationToken token, IProgress<ScanProgress>? progress = null, Action<Finding>? onFound = null)
    {
        foreach (var t in targets) if (!Safety.ValidName(t.Name)) throw new InvalidOperationException(L.T("En az 3 harf/rakam içeren belirli bir uygulama adı gir. Genel sistem adları kullanılamaz.", "Enter a specific application name with at least 3 letters/digits. General system names cannot be used."));
        var found = targets.Select(_ => new List<Finding>()).ToList(); var warnings = targets.Select(_ => new List<string>()).ToList();
        var counter = new System.Runtime.CompilerServices.StrongBox<int>(0);
        // Partial results are announced as they appear; the returned lists stay the authoritative, de-duplicated ones.
        var announced = new int[targets.Count]; var announcedKeys = new HashSet<(string, string, string, string?)>();
        // Progress can arrive from parallel Registry workers, so announcing is serialized.
        var announceGate = new object();
        void Announce()
        {
            if (onFound == null) return;
            lock (announceGate)
                for (int i = 0; i < found.Count; i++)
                    for (; announced[i] < found[i].Count; announced[i]++) { var f = found[i][announced[i]]; if (announcedKeys.Add((f.Kind, f.Path.ToUpperInvariant(), f.View, f.ValueName))) onFound(f); }
        }
        if (onFound != null) { var outer = progress; progress = new CallbackProgress<ScanProgress>(p => { Announce(); outer?.Report(p); }); }
        var resolved = new Target[targets.Count];
        for (int i = 0; i < targets.Count; i++) resolved[i] = Collect(targets[i], token, progress, Announce, found[i], warnings[i], counter);
        counter.Value += RegistryReferences.Scan(resolved, found, warnings, token, progress);
        progress?.Report(new(counter.Value, null, L.T("Servis ve görev başvuruları", "Service and task references")));
        for (int i = 0; i < targets.Count; i++) { InspectServices(resolved[i], found[i], warnings[i], token); InspectTasks(resolved[i], found[i], warnings[i], token); }
        Announce();
        progress?.Report(new(counter.Value, counter.Value, L.T("Tarama tamamlandı", "Scan complete")));
        return Enumerable.Range(0, targets.Count).Select(i => new ScanResult(found[i].DistinctBy(x => (x.Kind, x.Path, x.View, x.ValueName)).ToList(), warnings[i].Distinct().ToList(), counter.Value, resolved[i])).ToList();
    }
    // Folders and Registry keys named after the program, plus its exact uninstall entry; returns the target enriched with the folders found.
    private Target Collect(Target target, CancellationToken token, IProgress<ScanProgress>? progress, Action announce, List<Finding> found, List<string> warnings, System.Runtime.CompilerServices.StrongBox<int> counter)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Folder(string path, string reason, bool exact)
        {
            token.ThrowIfCancellationRequested();
            if (!Directory.Exists(path) || !seen.Add(Safety.Canonical(path))) return;
            bool safe = Safety.SafeDirectory(path, roots);
            long bytes = 0;
            if (safe)
            {
                try { bytes = Size(path, token); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add(L.T($"Boyut tam hesaplanamadı: {path} — {e.Message}", $"Size could not be fully calculated: {path} — {e.Message}")); }
            }
            // For a program that is no longer registered, a remaining executable means the program itself may still be in use.
            string? program = safe && string.IsNullOrWhiteSpace(target.RegistryPath) ? WorkingProgram(path) : null;
            if (program != null) reason += L.T($" Klasörde çalışır program dosyası var ({program}); program hâlâ kullanılıyor olabilir, silersen çalışmaz.", $" The folder still contains a working program file ({program}); the program may still be in use and will stop working if deleted.");
            found.Add(new() { Kind = "Klasör", Path = path, Reason = reason + (safe ? L.T(" İçeriği ve kişisel ayarları incele.", " Review its contents and personal settings.") : L.T(" Korunan veya bağlantı içeren konum; silme kapalı.", " Protected location or one containing links; deletion disabled.")), Confidence = exact && program == null ? "Güçlü eşleşme" : "Belirsiz", CanDelete = safe && exact, Bytes = bytes });
            announce();
        }
        if (!string.IsNullOrWhiteSpace(target.Location))
        {
            try { Folder(Safety.Canonical(target.Location), L.T("Uygulamanın kayıtlı kurulum konumu.", "The application's registered install location."), true); }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { warnings.Add(e.Message); }
        }
        foreach (string root in roots)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                foreach (string directory in Directory.EnumerateDirectories(root))
                {
                    token.ThrowIfCancellationRequested(); counter.Value++; if (counter.Value % 64 == 0) progress?.Report(new(counter.Value, null, L.T("Uygulama konumları", "Application locations")));
                    string name = System.IO.Path.GetFileName(directory);
                    if (Safety.Matches(name, target.Name)) Folder(directory, L.T("Klasör adı uygulama adıyla tam eşleşiyor.", "The folder name exactly matches the application name."), true);
                    else if (Safety.PossiblyMatches(name, target.Name)) Folder(directory, L.T("Adın başlangıcı benziyor; uygulamaya aidiyeti doğrulanmadı.", "The beginning of the name is similar; ownership by the application is not confirmed."), false);
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                    // One publisher level only; never select the publisher folder itself.
                    try
                    {
                        foreach (string child in Directory.EnumerateDirectories(directory))
                        {
                            token.ThrowIfCancellationRequested(); counter.Value++; if (counter.Value % 64 == 0) progress?.Report(new(counter.Value, null, L.T("Uygulama konumları", "Application locations")));
                            if (Safety.Matches(System.IO.Path.GetFileName(child), target.Name)) Folder(child, L.T("Yayıncı altındaki klasör adı uygulama adıyla tam eşleşiyor.", "The folder name under the publisher exactly matches the application name."), true);
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add(L.T($"Okunamadı: {directory}", $"Could not read: {directory}")); }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add(L.T($"Okunamadı: {root}", $"Could not read: {root}")); }
        }
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            token.ThrowIfCancellationRequested();
            // HKCU\SOFTWARE is shared by both views; a second pass would list every key twice.
            if (hive == RegistryHive.CurrentUser && view == RegistryView.Registry32) continue;
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            string prefix = hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
            try
            {
                using var software = baseKey.OpenSubKey("SOFTWARE");
                if (software == null) continue;
                foreach (string name in software.GetSubKeyNames())
                {
                    token.ThrowIfCancellationRequested(); counter.Value++; if (counter.Value % 64 == 0) progress?.Report(new(counter.Value, null, L.T("Uygulama konumları", "Application locations")));
                    void Add(string sub, string label)
                    {
                        string path = $"{prefix}\\SOFTWARE\\{sub}";
                        found.Add(new() { Kind = "Registry", Path = path, View = view.ToString(), Reason = label + L.T(" Ad eşleşmesi aidiyet garantisi değildir; içeriği incele.", " A name match does not guarantee ownership; review its contents."), Confidence = "Güçlü eşleşme", CanDelete = true });
                        announce();
                    }
                    // In the 64-bit view WOW6432Node is the 32-bit hive, which the Registry32 pass already lists under its real path.
                    if (view == RegistryView.Registry64 && name.Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Safety.Matches(name, target.Name) && Safety.ValidName(name)) Add(name, L.T("Yazılım anahtarı adı tam eşleşiyor.", "The software key name matches exactly."));
                    else if (Safety.ValidName(name))
                    {
                        try
                        {
                            using var publisher = software.OpenSubKey(name);
                            foreach (string child in publisher?.GetSubKeyNames() ?? [])
                                if (Safety.Matches(child, target.Name) && Safety.ValidName(child)) Add($"{name}\\{child}", L.T("Yayıncı altındaki yazılım anahtarı tam eşleşiyor.", "The software key under the publisher matches exactly."));
                        }
                        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { warnings.Add(L.T($"Registry okunamadı: {prefix}\\SOFTWARE\\{name}", $"Registry could not be read: {prefix}\\SOFTWARE\\{name}")); }
                    }
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { warnings.Add(L.T($"Registry görünümü okunamadı: {prefix} {view}", $"Registry view could not be read: {prefix} {view}")); }
        }
        // A stale uninstall entry may be removed only when its exact identity was captured from inventory.
        if (!string.IsNullOrWhiteSpace(target.RegistryPath) && Cleaner.RegistryExists(target.RegistryPath, target.View))
            found.Add(new() { Kind = "Kaldırma kaydı", Path = target.RegistryPath, View = target.View, Reason = L.T("Seçilen uygulamanın kaldırma kaydı hâlâ mevcut. Silmek programı kaldırmaz; yalnızca liste kaydını siler.", "The selected application's uninstall entry still exists. Deleting it does not uninstall the program; it only removes the list entry."), Confidence = "Doğrudan kayıt", CanDelete = true });
        return RegistryReferences.EnrichFromFolders(target, found);
    }
    private static string? WorkingProgram(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Select(System.IO.Path.GetFileName).FirstOrDefault(name => !Regex.IsMatch(name!, "^(unins|uninst|uninstall)", RegexOptions.IgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
    // Sizes come with the enumeration itself; links and cloud-only files are skipped.
    private static long Size(string directory, CancellationToken token)
    {
        long size = 0;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", DiskTools.Local())) { token.ThrowIfCancellationRequested(); size += file.Length; }
        return size;
    }
    private static void InspectServices(Target target, List<Finding> findings, List<string> warnings, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(target.Location)) return;
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            foreach (string name in services?.GetSubKeyNames() ?? [])
            {
                token.ThrowIfCancellationRequested(); using var service = services!.OpenSubKey(name);
                if (Safety.References(service?.GetValue("ImagePath")?.ToString() ?? "", target.Location))
                    findings.Add(new() { Kind = "Servis / sürücü", Path = name, Reason = L.T("Çalıştırma yolu kayıtlı kurulum klasörünü gösteriyor. Sistem yönetiminden incelenmeli; otomatik silme kapalı.", "Its executable path points to the registered install folder. Review it in system management; automatic deletion is disabled."), Confidence = "Yol başvurusu", CanDelete = false });
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { warnings.Add(L.T("Servisler tam okunamadı: ", "Services could not be fully read: ") + e.Message); }
    }
    private static void InspectTasks(Target target, List<Finding> findings, List<string> warnings, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(target.Location)) return;
        string root = System.IO.Path.Combine(Environment.SystemDirectory, "Tasks");
        try
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                token.ThrowIfCancellationRequested();
                if (Safety.References(File.ReadAllText(file), target.Location)) findings.Add(new() { Kind = "Zamanlanmış görev", Path = System.IO.Path.GetRelativePath(root, file), Reason = L.T("Görev içeriği kurulum klasörüne başvuruyor. Görev Zamanlayıcı üzerinden incelenmeli; otomatik silme kapalı.", "The task refers to the install folder. Review it in Task Scheduler; automatic deletion is disabled."), Confidence = "Yol başvurusu", CanDelete = false });
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add(L.T("Zamanlanmış görevler tam okunamadı: ", "Scheduled tasks could not be fully read: ") + e.Message); }
    }
}

public static class Cleaner
{
    public static bool Shared(Finding finding, Target target, IEnumerable<InstalledApp> apps)
    {
        if (finding.Kind == "Registry başvurusu")
        {
            var locations = RegistryReferences.Directories(target).Where(path => RegistryReferences.Matches(finding.ExpectedValue ?? "", new Target(target.Name, path))).ToList();
            if (locations.Count == 0 && target.ExecutablePath.Length > 0) locations.Add(System.IO.Path.GetDirectoryName(target.ExecutablePath) ?? "");
            if (locations.Count == 0 || locations.Any(string.IsNullOrWhiteSpace)) return true;
            return locations.Any(location => Shared(new Finding { Kind = "Klasör", Path = location, Reason = "Başvuru hedefi", Confidence = "" }, target, apps));
        }
        foreach (var app in apps)
        {
            if (app.RegistryPath.Equals(target.RegistryPath, StringComparison.OrdinalIgnoreCase) && app.View == target.View) continue;
            if (Safety.ValidName(app.Publisher) && Safety.Matches(System.IO.Path.GetFileName(finding.Path), app.Publisher)) return true;
            if (finding.Kind != "Klasör" || string.IsNullOrWhiteSpace(app.Location)) continue;
            try
            {
                if (Safety.Canonical(app.Location).Equals(Safety.Canonical(finding.Path), StringComparison.OrdinalIgnoreCase) || Safety.IsWithin(app.Location, finding.Path) || Safety.IsWithin(finding.Path, app.Location)) return true;
            }
            catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException) { return true; }
        }
        return false;
    }
    internal static (RegistryKey Root, string Subkey) OpenRegistry(string path, string view)
    {
        int separator = path.IndexOf('\\');
        if (separator < 0 || (path[..separator] != "HKCU" && path[..separator] != "HKLM")) throw new InvalidOperationException(L.T("Geçersiz Registry yolu.", "Invalid Registry path."));
        return (RegistryKey.OpenBaseKey(path[..separator] == "HKCU" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine,
            view == "Registry32" ? RegistryView.Registry32 : RegistryView.Registry64), path[(separator + 1)..]);
    }
    public static bool RegistryExists(string path, string view)
    {
        var (root, sub) = OpenRegistry(path, view); using (root) { using var key = root.OpenSubKey(sub); return key != null; }
    }
    public static void Delete(Finding finding, Target target, List<string>? allowedRoots = null)
    {
        if (!finding.CanDelete) throw new InvalidOperationException(L.T("Bu öğe otomatik silmeye uygun değil.", "This item is not eligible for automatic deletion."));
        if (Shared(finding, target, Inventory.Read())) throw new InvalidOperationException(L.T("Konum başka bir uygulama veya yayıncıyla ortak; silme engellendi.", "The location is shared with another application or publisher; deletion blocked."));
        if (finding.Kind == "Klasör")
        {
            if (!Safety.SafeDirectory(finding.Path, allowedRoots ?? Safety.Roots())) throw new InvalidOperationException(L.T("Konum güvenlik denetimini geçemedi.", "The location failed the safety check."));
            bool exactPath = !string.IsNullOrWhiteSpace(target.Location) && Safety.Canonical(target.Location).Equals(Safety.Canonical(finding.Path), StringComparison.OrdinalIgnoreCase);
            if (!exactPath && !Safety.Matches(System.IO.Path.GetFileName(finding.Path), target.Name)) throw new InvalidOperationException(L.T("Uygulama eşleşmesi değişti.", "The application match has changed."));
            if (Directory.Exists(finding.Path))
            {
                ValidateTree(finding.Path);
                DeleteTree(finding.Path);
            }
            if (Directory.Exists(finding.Path)) throw new IOException(L.T("Klasör hâlâ mevcut.", "The folder still exists."));
        }
        else if (finding.Kind == "Registry başvurusu") RegistryReferences.Delete(finding, target);
        else if (finding.Kind is "Registry" or "Kaldırma kaydı")
        {
            var (root, sub) = OpenRegistry(finding.Path, finding.View); using (root)
            {
                string[] parts = sub.Split('\\');
                bool exactEntry = finding.Kind == "Kaldırma kaydı" && finding.Path == target.RegistryPath && finding.View == target.View && sub.StartsWith(Inventory.Uninstall + "\\", StringComparison.OrdinalIgnoreCase) && parts.Length == 6;
                bool appKey = finding.Kind == "Registry" && parts.Length is 2 or 3 && parts[0].Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase) && parts.Skip(1).All(Safety.ValidName) && Safety.Matches(parts[^1], target.Name);
                if (!exactEntry && !appKey) throw new InvalidOperationException(L.T("Registry kapsamı doğrulanamadı.", "The Registry scope could not be verified."));
                root.DeleteSubKeyTree(sub, false);
            }
            if (RegistryExists(finding.Path, finding.View)) throw new IOException(L.T("Registry girdisi hâlâ mevcut.", "The Registry entry still exists."));
        }
        else throw new InvalidOperationException(L.T("Desteklenmeyen öğe türü.", "Unsupported item type."));
    }
    private static void ValidateTree(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException(L.T("Klasör ağacında bağlantı var; silme engellendi.", "The folder tree contains a link; deletion blocked."));
        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException(L.T("Klasör ağacında bağlantı var; silme engellendi: ", "The folder tree contains a link; deletion blocked: ") + entry);
            if ((attributes & FileAttributes.Directory) != 0) ValidateTree(entry);
        }
    }
    // Windows refuses to delete read-only items ("Access … is denied"); only that attribute is cleared, permissions are never changed.
    private static void DeleteTree(string path)
    {
        var own = File.GetAttributes(path);
        if ((own & FileAttributes.ReparsePoint) != 0) throw new IOException(L.T("Konum bir bağlantıyla değiştirildi; durduruldu.", "The location was replaced by a link; stopped."));
        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attr = File.GetAttributes(entry);
            if ((attr & FileAttributes.ReparsePoint) != 0) throw new IOException(L.T("Bağlantı silinmez: ", "Links are not deleted: ") + entry);
            if ((attr & FileAttributes.Directory) != 0) DeleteTree(entry);
            else { if ((attr & FileAttributes.ReadOnly) != 0) File.SetAttributes(entry, attr & ~FileAttributes.ReadOnly); File.Delete(entry); }
        }
        if ((own & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, own & ~FileAttributes.ReadOnly);
        Directory.Delete(path, false);
    }
}

public static class Journal
{
    public static readonly string FilePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UClear", "history.jsonl");
    public static void Clear() => File.Delete(FilePath);
    public static void Add(string app, string action, string detail, string? itemPath = null)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
        File.AppendAllText(FilePath, JsonSerializer.Serialize(new HistoryEntry(DateTimeOffset.Now, app, action, detail, itemPath)) + Environment.NewLine);
    }
    public static List<HistoryEntry> Read()
    {
        if (!File.Exists(FilePath)) return [];
        return File.ReadLines(FilePath).Select(line => { try { return JsonSerializer.Deserialize<HistoryEntry>(line); } catch (JsonException) { return null; } }).OfType<HistoryEntry>().Reverse().Take(500).ToList();
    }
}

