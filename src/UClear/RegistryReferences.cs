using Microsoft.Win32;
using System.IO;
using System.Text.RegularExpressions;

namespace UClear;

public static class RegistryReferences
{
    // Value deletion only: a path reference does not prove ownership of its parent tree.
    private static readonly string[] ExecutableScopes = [
        @"SOFTWARE\Classes", @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"
    ];
    public static string IconExecutable(string icon)
    {
        string path = Regex.Replace(Environment.ExpandEnvironmentVariables(icon.Trim()), @",\s*-?\d+$", "").Trim().Trim('"');
        return Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\") && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? path : "";
    }
    private static bool SpecificDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\")) return false;
        string full = Safety.Canonical(directory);
        if (!Safety.ValidName(Path.GetFileName(full))) return false;
        var broad = Safety.Roots().Concat(new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.Windows) });
        return !broad.Where(x => x.Length > 0).Any(x => Safety.Canonical(x).Equals(full, StringComparison.OrdinalIgnoreCase)) && !Safety.IsWithin(full, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    }
    public static IEnumerable<string> Directories(Target target) => new[] { target.Location }.Concat(target.ReferenceDirectories ?? []).Where(SpecificDirectory).Distinct(StringComparer.OrdinalIgnoreCase);
    public static Target EnrichFromFolders(Target target, IEnumerable<Finding> findings) => target with
    {
        ReferenceDirectories = Directories(target).Concat(findings.Where(f => f.Kind == "Klasör" && f.CanDelete && Safety.Matches(Path.GetFileName(f.Path), target.Name)).Select(f => f.Path))
            .Where(SpecificDirectory).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
    };
    public static bool Matches(string value, Target target) => new ReferenceMatcher(target).IsMatch(value);
    // Built once per target: a Registry walk checks hundreds of thousands of values against it.
    public sealed class ReferenceMatcher
    {
        readonly string[] needles;
        readonly Regex? pattern;
        public Target Target { get; }
        public bool Empty => pattern == null;
        public ReferenceMatcher(Target target)
        {
            Target = target;
            var directories = Directories(target).Select(Safety.Canonical).ToList();
            string executable = Path.IsPathFullyQualified(target.ExecutablePath) && !target.ExecutablePath.StartsWith(@"\\") ? Safety.Canonical(target.ExecutablePath) : "";
            var parts = directories.Select(d => Regex.Escape(d) + @"(?=\\|["";,\s]|$)").ToList();
            if (executable.Length > 0) parts.Add(Regex.Escape(executable) + @"(?=["";,\s]|$)");
            needles = directories.Append(executable).Where(x => x.Length > 0).ToArray();
            pattern = parts.Count == 0 ? null : new Regex(@"(?<![\w\\:])(?:" + string.Join("|", parts) + ")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
        public bool IsMatch(string value)
        {
            if (pattern == null) return false;
            // Cheap pre-check: a value can only match if it contains one of the paths, or could after expanding variables or slashes.
            if (!value.Contains('%') && !value.Contains('/') && !needles.Any(n => value.Contains(n, StringComparison.OrdinalIgnoreCase))) return false;
            string expanded = Environment.ExpandEnvironmentVariables(value).Replace('/', '\\');
            if (Regex.IsMatch(expanded, @"\\\.\.?(?=\\|["";\s]|$)")) return false;
            return pattern.IsMatch(expanded);
        }
    }
    public static bool DeletableValue(string subkey, string name, string value, Target target)
    {
        if (!Matches(value, target)) return false;
        // Multiple absolute targets can denote a shared list; leave those for review.
        if (Regex.Matches(Environment.ExpandEnvironmentVariables(value), @"[A-Za-z]:[\\/]").Count != 1) return false;
        bool command = subkey.Equals(ExecutableScopes[2], StringComparison.OrdinalIgnoreCase) || subkey.Equals(ExecutableScopes[3], StringComparison.OrdinalIgnoreCase);
        bool appPath = subkey.StartsWith(ExecutableScopes[1] + "\\", StringComparison.OrdinalIgnoreCase) && name.Length == 0;
        bool classes = subkey.StartsWith(ExecutableScopes[0] + "\\", StringComparison.OrdinalIgnoreCase) && name.Length == 0 &&
            (subkey.EndsWith("\\InprocServer32", StringComparison.OrdinalIgnoreCase) || subkey.EndsWith("\\LocalServer32", StringComparison.OrdinalIgnoreCase) ||
             subkey.EndsWith("\\command", StringComparison.OrdinalIgnoreCase) || subkey.EndsWith("\\DefaultIcon", StringComparison.OrdinalIgnoreCase) ||
             subkey.StartsWith(@"SOFTWARE\Classes\TypeLib\", StringComparison.OrdinalIgnoreCase) && (subkey.EndsWith("\\win32", StringComparison.OrdinalIgnoreCase) || subkey.EndsWith("\\win64", StringComparison.OrdinalIgnoreCase)));
        // Only a value starting with the application's own path is an executable registration.
        // Commands such as rundll32.exe <app.dll> remain visible but require manual review.
        string first = Environment.ExpandEnvironmentVariables(value).TrimStart().TrimStart('"');
        bool startsWithTarget = Directories(target).Any(path => first.StartsWith(Safety.Canonical(path) + "\\", StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrEmpty(target.ExecutablePath) && first.StartsWith(target.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        return (command || appPath || classes) && startsWithTarget;
    }
    public static int Scan(Target target, List<Finding> findings, List<string> warnings, CancellationToken token, IProgress<ScanProgress>? progress = null)
        => Scan([target], [findings], [warnings], token, progress);
    // One walk serves every target, so cleaning several programs costs about the same as cleaning one.
    public static int Scan(IReadOnlyList<Target> targets, IReadOnlyList<List<Finding>> findings, IReadOnlyList<List<string>> warnings, CancellationToken token, IProgress<ScanProgress>? progress = null)
    {
        var active = new List<int>();
        for (int i = 0; i < targets.Count; i++)
        {
            if (Directories(targets[i]).Any() || !string.IsNullOrWhiteSpace(targets[i].ExecutablePath)) active.Add(i);
            else warnings[i].Add(L.T("Kurulum yolu veya uygulama EXE yolu bilinmediğinden içerik temelli Registry taraması yapılamadı. Eski kurulum konumunu ekleyerek yeniden tara.", "The content-based Registry scan was skipped because neither the install path nor the application EXE path is known. Add the old install location and scan again."));
        }
        if (active.Count == 0) return 0;
        var matchers = active.Select(i => new ReferenceMatcher(targets[i])).ToList();
        var lists = active.Select(i => findings[i]).ToList();
        var shared = new List<string>();
        int checkedKeys = 0; int completedBranches = 0;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            // HKCU\SOFTWARE is the same key in both views; walking it twice only doubles time and findings.
            if (hive == RegistryHive.CurrentUser && view == RegistryView.Registry32) continue;
            using var root = RegistryKey.OpenBaseKey(hive, view);
            string prefix = hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
            checkedKeys += ScanBranch(root, "SOFTWARE", prefix, view.ToString(), matchers, lists, shared, token, 120000, progress);
            progress?.Report(new(++completedBranches, 3, L.T("Registry bölümleri", "Registry sections")));
        }
        foreach (int i in active) warnings[i].AddRange(shared);
        return checkedKeys;
    }
    public static int ScanBranch(RegistryKey root, string start, string prefix, string view, Target target, List<Finding> findings, List<string> warnings, CancellationToken token, int limit = 120000, IProgress<ScanProgress>? progress = null)
        => ScanBranch(root, start, prefix, view, [new ReferenceMatcher(target)], [findings], warnings, token, limit, progress);
    static int ScanBranch(RegistryKey root, string start, string prefix, string view, IReadOnlyList<ReferenceMatcher> matchers, IReadOnlyList<List<Finding>> findings, List<string> warnings, CancellationToken token, int limit, IProgress<ScanProgress>? progress)
    {
        bool skipWow = view == RegistryView.Registry64.ToString() && prefix == "HKLM";
        // Workers fill these queues; results are merged afterwards so callers never see a list being filled concurrently.
        var found = matchers.Select(_ => new System.Collections.Concurrent.ConcurrentQueue<Finding>()).ToArray();
        var notes = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var (count, limited) = RegistryWalker.Walk(root, start, token, (key, path, depth) =>
        {
            var names = key.GetValueNames();
            if (names.Contains("SymbolicLinkValue", StringComparer.OrdinalIgnoreCase)) { notes.Enqueue(L.T($"Registry bağlantısı atlandı: {prefix}\\{path}", $"Registry link skipped: {prefix}\\{path}")); return false; }
            foreach (string name in names)
            {
                token.ThrowIfCancellationRequested();
                object? raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                var strings = raw is string s ? new[] { s } : raw as string[] ?? [];
                if (strings.Length == 0) continue;
                for (int i = 0; i < matchers.Count; i++)
                {
                    var target = matchers[i].Target;
                    if (!strings.Any(matchers[i].IsMatch)) continue;
                    string value = string.Join("\n", strings);
                    bool deletable = raw is string && DeletableValue(path, name, value, target);
                    found[i].Enqueue(new() { Kind = "Registry başvurusu", Path = $"{prefix}\\{path}", View = view, ValueName = name, ExpectedValue = value, ExpectedValueKind = (int)key.GetValueKind(name),
                        Reason = L.T($"Değer içeriği uygulamanın kurulum/EXE yoluna başvuruyor: {value}. ", $"The value refers to the application's install/EXE path: {value}. ") + (deletable ? L.T("Yalnızca bu değer silinir; üst anahtar ve diğer değerler korunur.", "Only this value is deleted; the parent key and other values are kept.") : L.T("Paylaşılan veya genel amaçlı kayıt olabilir; inceleme için listelendi, silme kapalı.", "This may be a shared or general-purpose entry; listed for review, deletion disabled.")), Confidence = "İçerik başvurusu", CanDelete = deletable });
                }
            }
            if (depth >= 32) { if (key.SubKeyCount > 0) notes.Enqueue(L.T($"Registry derinlik sınırı: {prefix}\\{path}", $"Registry depth limit: {prefix}\\{path}")); return false; }
            return true;
        },
        (path, e) => notes.Enqueue(L.T($"Registry okunamadı: {prefix}\\{path} ({view}) — {e.Message}", $"Registry could not be read: {prefix}\\{path} ({view}) — {e.Message}")),
        // The 32-bit view walks Wow6432Node under its real paths; walking it here too would double the work.
        skipWow ? child => child.Equals("Wow6432Node", StringComparison.OrdinalIgnoreCase) : null,
        limit, seen => { if (seen % 512 == 0) progress?.Report(new(seen, null, $"Registry {prefix} {view}")); });
        for (int i = 0; i < found.Length; i++) findings[i].AddRange(found[i]);
        warnings.AddRange(notes);
        if (limited) warnings.Add(L.T($"Registry tarama sınırına ulaşıldı: {prefix} {view} ({limit} anahtar). Tarama kısmi.", $"Registry scan limit reached: {prefix} {view} ({limit} keys). The scan is partial."));
        return count;
    }
    public static void Delete(Finding finding, Target target)
    {
        if (finding.ValueName == null || finding.ExpectedValue == null) throw new InvalidOperationException(L.T("Registry değeri kanıtı eksik.", "The Registry value evidence is missing."));
        var (root, subkey) = Cleaner.OpenRegistry(finding.Path, finding.View);
        using (root)
        using (var key = root.OpenSubKey(subkey, true))
        {
            if (key == null || !key.GetValueNames().Contains(finding.ValueName, StringComparer.OrdinalIgnoreCase)) return;
            if (!DeletableValue(subkey, finding.ValueName, finding.ExpectedValue, target)) throw new InvalidOperationException(L.T("Registry başvurusu doğrulanamadı.", "The Registry reference could not be verified."));
            var live = key.GetValue(finding.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (live is not string text || !text.Equals(finding.ExpectedValue, StringComparison.Ordinal) || (int)key.GetValueKind(finding.ValueName) != finding.ExpectedValueKind)
                throw new InvalidOperationException(L.T("Registry değeri taramadan sonra değişti; yeniden tara.", "The Registry value changed after the scan; scan again."));
            key.DeleteValue(finding.ValueName, false);
            if (key.GetValueNames().Contains(finding.ValueName, StringComparer.OrdinalIgnoreCase)) throw new IOException(L.T("Registry değeri hâlâ mevcut.", "The Registry value still exists."));
        }
    }
}

// Walks a Registry tree with several workers sharing one stack, so a huge branch (SOFTWARE\Classes) is split evenly across them.
public static class RegistryWalker
{
    // visit returns whether to descend; it runs concurrently, so it must only touch thread-safe state.
    public static (int Count, bool Limited) Walk(RegistryKey root, string start, CancellationToken token, Func<RegistryKey, string, int, bool> visit, Action<string, Exception> failed, Func<string, bool>? skipChild = null, int limit = int.MaxValue, Action<int>? counted = null)
    {
        var pending = new System.Collections.Concurrent.ConcurrentStack<(string Path, int Depth)>(); pending.Push((start, 0));
        int outstanding = 1, count = 0, limited = 0;
        void Visit((string Path, int Depth) entry)
        {
            if (Volatile.Read(ref count) >= limit) { Interlocked.Exchange(ref limited, 1); return; }
            try
            {
                using var key = root.OpenSubKey(entry.Path); if (key == null) return;
                counted?.Invoke(Interlocked.Increment(ref count));
                if (!visit(key, entry.Path, entry.Depth)) return;
                foreach (string child in key.GetSubKeyNames())
                {
                    if (skipChild?.Invoke(child) == true) continue;
                    Interlocked.Increment(ref outstanding); pending.Push(($"{entry.Path}\\{child}", entry.Depth + 1));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or RegexMatchTimeoutException) { failed(entry.Path, e); }
        }
        void Worker()
        {
            var idle = new SpinWait();
            while (Volatile.Read(ref outstanding) > 0)
            {
                token.ThrowIfCancellationRequested();
                if (!pending.TryPop(out var entry)) { idle.SpinOnce(); continue; }
                idle.Reset();
                try { Visit(entry); } finally { Interlocked.Decrement(ref outstanding); }
            }
        }
        try { Parallel.For(0, Math.Clamp(Environment.ProcessorCount, 2, 8), new ParallelOptions { CancellationToken = token }, _ => Worker()); }
        catch (AggregateException e) when (e.InnerExceptions.All(x => x is OperationCanceledException)) { throw new OperationCanceledException(token); }
        return (count, limited == 1);
    }
}
