using Microsoft.Win32;
using UClear;

// Some checks read Turkish engine messages; keep them independent of the machine's language.
L.Language = "tr";

if (args.Length == 2 && args[0] == "--tree-fixture")
{
    var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
    start.ArgumentList.Add("--lock-fixture"); start.ArgumentList.Add(args[1]);
    using var nestedProcess = System.Diagnostics.Process.Start(start)!;
    if (nestedProcess.StandardOutput.ReadLine() != "ready") return;
    Console.WriteLine(nestedProcess.Id); Console.Out.Flush();
    Thread.Sleep(30000); return;
}

if (args.Length == 2 && args[0] == "--lock-fixture")
{
    using var heldFixture = new FileStream(args[1], FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
    Console.WriteLine("ready"); Console.Out.Flush();
    Thread.Sleep(30000); return;
}

if (args.Length == 1 && args[0] == "--reference-scan")
{
    string refStage = ""; var refWatch = System.Diagnostics.Stopwatch.StartNew(); var refTotal = System.Diagnostics.Stopwatch.StartNew();
    var report = InvalidReferences.Scan(CancellationToken.None, new CallbackProgress<ScanProgress>(p => { if (p.Stage == refStage) return; Console.WriteLine($"    {refStage} {refWatch.Elapsed.TotalSeconds:0.0}s"); refStage = p.Stage; refWatch.Restart(); }));
    Console.WriteLine($"    {refStage} {refWatch.Elapsed.TotalSeconds:0.0}s · toplam {refTotal.Elapsed.TotalSeconds:0.0}s");
    Console.WriteLine($"Read-only reference scan: {report.Items.Count} findings, {report.Warnings.Count} warnings, {report.Items.Count(i => i.CanDelete)} selectable, {report.Items.Count(i => i.Certain)} certain.");
    foreach (var item in report.Items.Where(i => i.CanDelete)) Console.WriteLine($"  {(item.Certain ? "certain" : "manual")} [{item.Category}] {item.Location} {item.Name} -> {item.Target} ({item.State})");
    foreach (var group in report.Items.GroupBy(i => i.Category)) Console.WriteLine($"  {group.Key}: {group.Count()} findings, {group.Count(i => i.CanDelete)} selectable");
    var registry = report.Items.Where(i => i.Category == "Registry").ToList();
    Console.WriteLine($"  Registry: {registry.Count(i => i.Location.Contains(@"\Wow6432Node\", StringComparison.OrdinalIgnoreCase))} under Wow6432Node, {registry.Count(i => i.View == "Registry32")} in 32-bit view, {registry.Count - registry.DistinctBy(i => (i.Location.Replace(@"\Wow6432Node", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant(), i.Original)).Count()} duplicates ignoring Wow6432Node");
    var startup = StartupTools.Read();
    foreach (string name in new[] { "EpicGamesLauncher", "RiotClient", "SecShot", "LGHUB" })
        Console.WriteLine(name + ": " + string.Join(", ", startup.Where(e => e.Name == name).Select(e => e.State)));
    return;
}
if (args.Length == 1 && args[0] == "--store")
{
    var notes = new List<string>(); var watch = System.Diagnostics.Stopwatch.StartNew();
    var store = StoreApps.Read(notes);
    Console.WriteLine($"Read-only Store listing: {store.Count} apps ({store.Count(a => a.SystemPackage)} Windows-owned), {notes.Count} warnings, {watch.Elapsed.TotalSeconds:0.0}s");
    foreach (var app in store.OrderBy(a => a.SystemPackage).ThenBy(a => a.Name)) Console.WriteLine($"  {(app.SystemPackage ? "windows " : "user    ")} {app.Name} | {app.Publisher} | {app.Version} | logo:{(app.DisplayIcon.Length > 0 ? "var" : "yok")}");
    return;
}
if (args.Length == 1 && args[0] == "--leftovers")
{
    var notes = new List<string>(); var found = new List<LeftoverCandidate>();
    var watch = System.Diagnostics.Stopwatch.StartNew();
    LeftoverFinder.Find(CancellationToken.None, found.Add, notes);
    Console.WriteLine($"Read-only leftover search: {found.Count} candidates, {notes.Count} warnings, {watch.Elapsed.TotalSeconds:0.0}s");
    foreach (var c in found.OrderBy(c => c.Rank).ThenByDescending(c => c.Bytes)) Console.WriteLine($"  [{c.Confidence}] {c.Name} — {c.Path} ({c.SizeText})");
    return;
}
if (args.Length == 3 && args[0] == "--scan-target")
{
    var watch = System.Diagnostics.Stopwatch.StartNew();
    string stage = ""; var stageWatch = System.Diagnostics.Stopwatch.StartNew();
    var stages = new CallbackProgress<ScanProgress>(p => { string s = p.Stage.Split(' ')[0] + (p.Stage.Contains("HK") ? " " + p.Stage : ""); if (s == stage) return; Console.WriteLine($"    {stage} {stageWatch.Elapsed.TotalSeconds:0.0}s"); stage = s; stageWatch.Restart(); });
    var targetScan = new Scanner().Scan(new Target(args[1], args[2]), CancellationToken.None, stages);
    Console.WriteLine($"    {stage} {stageWatch.Elapsed.TotalSeconds:0.0}s");
    var eligible = CleanupPolicy.Eligible(targetScan.Findings);
    Console.WriteLine($"Read-only scan of {args[1]}: {targetScan.Findings.Count} findings, {eligible.Count} would be removed, {watch.Elapsed.TotalSeconds:0.0}s");
    foreach (var f in targetScan.Findings) Console.WriteLine($"  {(eligible.Contains(f) ? "remove" : "skip  ")} [{f.Kind}/{f.Confidence}] {f.DisplayPath}");
    return;
}
int count = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; Console.WriteLine("PASS " + message); }
void Reject(Action action, string message) { try { action(); } catch { Check(true, message); return; } throw new Exception("Expected rejection: " + message); }
string workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
string root = Path.Combine(workspace, "test-artifacts", Guid.NewGuid().ToString("N"));
string appName = "UClearFixture" + Guid.NewGuid().ToString("N");
Directory.CreateDirectory(root);
string exact = Path.Combine(root, appName), similar = Path.Combine(root, appName + "Other");
Directory.CreateDirectory(exact); Directory.CreateDirectory(similar);
var verificationFolder = new Finding { Kind = "Klasör", Path = exact, Reason = "test", Confidence = "test" };
Check(CleanupVerification.Check(verificationFolder) == "Hâlâ mevcut", "Verification detects surviving folder");
Check(CleanupVerification.Check(new Finding { Kind = "Klasör", Path = Path.Combine(root, "missing"), Reason = "test", Confidence = "test" }) == "Silindi · doğrulandı", "Verification confirms absent folder");
File.WriteAllText(Path.Combine(exact, "fixture.txt"), "test only");
File.WriteAllText(Path.Combine(similar, "keep.txt"), "keep");
Check(!Safety.SafeDirectory(root, [root]), "Root itself cannot be deleted");
Check(!Safety.SafeDirectory(Path.Combine(root, "..", "outside"), [root]), "Traversal outside scan root rejected");
Check(!Safety.SafeDirectory(root + "Sibling", [root]), "Path prefix sibling rejected");
Check(!Safety.ValidName("Microsoft") && !Safety.ValidName("ab"), "Broad and short names rejected");
Check(Safety.SafeDirectory(exact, [root]), "Specific child allowed");
Check(!Safety.References(@"C:\AppOther\run.exe", @"C:\App"), "Reference matching requires directory boundary");
Check(Safety.References(@"\""C:\App\run.exe\"" --arg", @"C:\App"), "Quoted executable reference detected");
string readOnlyDir = Path.Combine(exact, "Locked Settings"); Directory.CreateDirectory(readOnlyDir);
string readOnlyFile = Path.Combine(readOnlyDir, "settings.ini"); File.WriteAllText(readOnlyFile, "fixture");
File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly); File.SetAttributes(readOnlyDir, FileAttributes.Directory | FileAttributes.ReadOnly);
var result = new Scanner([root]).Scan(new(appName), CancellationToken.None);
var matched =result.Findings.Single(f => f.Kind == "Klasör" && f.Path == exact);
var ambiguous = result.Findings.Single(f => f.Kind == "Klasör" && f.Path == similar);
Check(matched.CanDelete && !matched.Selected, "Exact result not auto-selected");
Check(!ambiguous.CanDelete && !ambiguous.Selected, "Ambiguous result cannot be deleted");
Reject(() => Cleaner.Delete(ambiguous, new(appName), [root]), "Ambiguous deletion blocked in engine");
Reject(() => Cleaner.Delete(matched, new("Unrelated"), [root]), "Target identity mismatch blocked");
Cleaner.Delete(matched, new(appName), [root]);
Check(!Directory.Exists(exact) && File.Exists(Path.Combine(similar, "keep.txt")), "Exact deletion leaves similar sibling intact");
Check(!Directory.Exists(readOnlyDir), "Read-only files and folders inside a removed folder are deleted");
using (var cancelled = new CancellationTokenSource()) { cancelled.Cancel(); Reject(() => new Scanner([root]).Scan(new(appName), cancelled.Token), "Scan cancellation supported"); }
string registryPath = @"SOFTWARE\" + appName;
using (var key = Registry.CurrentUser.CreateSubKey(registryPath)) key.SetValue("Fixture", "test only");
var registryFinding = new Finding { Kind = "Registry", Path = @"HKCU\" + registryPath, View = "Registry64", Reason = "test", Confidence = "test", CanDelete = true };
Cleaner.Delete(registryFinding, new(appName));
Check(!Cleaner.RegistryExists(registryFinding.Path, registryFinding.View), "Isolated test Registry key removed and verified");
Reject(() => Cleaner.Delete(new Finding { Kind = "Registry", Path = @"HKCU\SOFTWARE\Microsoft", Reason = "test", Confidence = "test", CanDelete = true }, new("Microsoft")), "Shared Registry vendor key protected");
Reject(() => Cleaner.Delete(new Finding { Kind = "Registry", Path = @"HKCU\Environment", Reason = "test", Confidence = "test", CanDelete = true }, new("Environment")), "Registry deletion stays within Software scope");
Reject(() => Inventory.Uninstaller("untrusted.exe /remove"), "Relative uninstall executable rejected");
var msi = Inventory.Uninstaller("MsiExec.exe /I{12345678-1234-1234-1234-123456789012}");
Check(msi.Arguments.StartsWith("/X{") && Path.IsPathFullyQualified(msi.FileName), "MSI maintenance command translated to uninstall");
// Delete only files created by this run, after validating the absolute fixture directory.
if (!Safety.IsWithin(root, Path.Combine(workspace, "test-artifacts"))) throw new Exception("Fixture cleanup out of scope");
File.Delete(Path.Combine(similar, "keep.txt")); Directory.Delete(similar); Directory.Delete(root);
var eligibleRegistry = new Finding { Kind = "Registry", Path = "fixture", Reason = "test", Confidence = "Güçlü eşleşme", CanDelete = true };
var protectedFolder = new Finding { Kind = "Klasör", Path = "fixture", Reason = "shared", Confidence = "Paylaşılan", CanDelete = true };
var serviceOnly = new Finding { Kind = "Servis / sürücü", Path = "fixture", Reason = "report only", Confidence = "Yol başvurusu", CanDelete = true };
Check(CleanupPolicy.Eligible([eligibleRegistry, protectedFolder, serviceOnly, ambiguous]).SequenceEqual([eligibleRegistry]), "Post-uninstall plan includes Registry but excludes protected, ambiguous and service findings");
Check(!eligibleRegistry.Selected, "Post-uninstall proposal does not alter manual selections before consent");
eligibleRegistry.Result = "Silindi · doğrulandı";
Check(CleanupPolicy.Eligible([eligibleRegistry]).Count == 0, "Already-deleted findings excluded from cleanup proposal");
Console.WriteLine($"{count} checks passed. No installed application was removed.");
Check(UninstallOutcome.NeedsConfirmation(4, true) && UninstallOutcome.NeedsConfirmation(4, false), "Unknown exit code 4 requires completion confirmation regardless of Registry state");
Check(!UninstallOutcome.KnownFailure(false, 4), "Vendor exit code 4 is not interpreted as a Windows error");
Check(UninstallOutcome.NeedsRestart(true, 3010) && !UninstallOutcome.NeedsRestart(false, 3010), "Reboot code interpretation is scoped to Windows Installer");
Check(UninstallOutcome.KnownFailure(true, 1602), "MSI cancellation cannot be confirmed as successful completion");
Check(UninstallOutcome.NeedsConfirmation(0, true) && !UninstallOutcome.NeedsConfirmation(0, false), "Successful launcher with surviving registration needs user verification");
Console.WriteLine($"Total: {count} checks passed.");
var referenceTarget = new Target(appName, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appName));
string serverKey = @"SOFTWARE\Classes\CLSID\" + Guid.NewGuid().ToString("B") + @"\InprocServer32";
string clsidKey = serverKey[..serverKey.LastIndexOf('\\')];
using var testHive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
try
{
    using (var key = testHive.CreateSubKey(serverKey))
    {
        key.SetValue("", referenceTarget.Location + @"\component.dll", RegistryValueKind.String);
        key.SetValue("ThreadingModel", "Both");
        key.SetValue("OtherComponent", @"C:\OtherVendor\shared.dll");
    }
    var refs = new List<Finding>(); var notes = new List<string>();
    RegistryReferences.ScanBranch(testHive, clsidKey, "HKCU", "Registry64", referenceTarget, refs, notes, CancellationToken.None);
    var reference = refs.Single();
    Check(CleanupVerification.Check(reference) == "Hâlâ mevcut", "Verification detects existing unnamed Registry value");
    var formerTarget = RegistryReferences.EnrichFromFolders(new Target(appName), [new Finding { Kind = "Klasör", Path = referenceTarget.Location, Reason = "Tam ad", Confidence = "Güçlü eşleşme", CanDelete = true }]);
    var formerRefs = new List<Finding>();
    RegistryReferences.ScanBranch(testHive, clsidKey, "HKCU", "Registry64", formerTarget, formerRefs, notes, CancellationToken.None);
    Check(formerRefs.Any(f => f.ValueName == "" && f.CanDelete), "Previously uninstalled app discovers GUID registration from remaining folder evidence");
    Check(RegistryReferences.Matches(referenceTarget.Location + @"\deleted.exe", new Target(appName, referenceTarget.Location)), "Manually supplied former path works even when the file no longer exists");
    var uncertainTarget = RegistryReferences.EnrichFromFolders(new Target(appName), [new Finding { Kind = "Klasör", Path = referenceTarget.Location + "Other", Reason = "Benzer ad", Confidence = "Belirsiz", CanDelete = false }]);
    Check(!RegistryReferences.Directories(uncertainTarget).Any(), "Ambiguous old folder is not promoted to Registry ownership evidence");
    Check(reference.CanDelete && reference.Path.EndsWith("InprocServer32") && reference.ValueName == "", "GUID-named COM registration found by DLL path, independent of application name");
    Check(CleanupPolicy.Eligible(refs).Count == 1, "Content-based Registry finding included in cleanup proposal");
    using (var key = testHive.OpenSubKey(serverKey, true)) key!.SetValue("", @"C:\OtherVendor\replacement.dll");
    Reject(() => Cleaner.Delete(reference, referenceTarget), "Changed Registry value blocked by live evidence revalidation");
    using (var key = testHive.OpenSubKey(serverKey, true)) key!.SetValue("", referenceTarget.Location + @"\component.dll");
    Cleaner.Delete(reference, referenceTarget);
    Check(CleanupVerification.Check(reference) == "Silindi · doğrulandı", "Verification confirms removed value even when its parent remains");
    using (var key = testHive.OpenSubKey(serverKey))
        Check(key!.GetValue("") == null && (string?)key.GetValue("ThreadingModel") == "Both" && key.GetValue("OtherComponent") != null, "Only matching Registry value deleted; unrelated values and parent preserved");
    Check(!RegistryReferences.Matches(referenceTarget.Location + @"Other\component.dll", referenceTarget), "Similar directory prefix not matched");
    Check(!RegistryReferences.Matches(referenceTarget.Location + @"\..\Other\component.dll", referenceTarget), "Dot-segment path escape not treated as application ownership");
    Check(RegistryReferences.Matches('"' + referenceTarget.Location + "\\program.exe\" --startup", referenceTarget), "Quoted launch command matched by install path");
    string run = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    Check(RegistryReferences.DeletableValue(run, "UnrelatedRandomName", '"' + referenceTarget.Location + "\\program.exe\" --startup", referenceTarget), "Differently named startup value can be cleaned by executable evidence");
    Check(!RegistryReferences.DeletableValue(run, "Shared", referenceTarget.Location + @"\program.exe;C:\Other\other.exe", referenceTarget), "Shared multi-target registry value not eligible for deletion");
    using (var key = testHive.CreateSubKey(clsidKey)) key.SetValue("InstallFolder", referenceTarget.Location);
    refs.Clear();
    RegistryReferences.ScanBranch(testHive, clsidKey, "HKCU", "Registry64", referenceTarget, refs, notes, CancellationToken.None);
    Check(refs.Any(f => f.ValueName == "InstallFolder" && !f.CanDelete), "Generic path reference remains visible for review without deleting shared metadata");
    notes.Clear();
    RegistryReferences.ScanBranch(testHive, clsidKey, "HKCU", "Registry64", referenceTarget, [], notes, CancellationToken.None, 1);
    Check(notes.Any(n => n.Contains("kısmi")), "Bounded scan explicitly reports incomplete coverage");
}
finally
{
    if (!clsidKey.StartsWith(@"SOFTWARE\Classes\CLSID\") || !Guid.TryParse(clsidKey.Split('\\')[^1], out _)) throw new Exception("Fixture cleanup scope invalid");
    testHive.DeleteSubKeyTree(clsidKey, false);
}
Console.WriteLine($"Total: {count} checks passed, including Registry reference detection and deletion.");
var candidateA = new Finding { Kind = "Klasör", Path = @"C:\UClearFixture\App", Reason = "test", Confidence = "Güçlü eşleşme", CanDelete = true };
var candidateB = new Finding { Kind = "Registry", Path = @"HKCU\Software\UClearFixture", Reason = "test", Confidence = "Güçlü eşleşme", CanDelete = true };
var choices = new CleanupSelection([candidateA, candidateB, ambiguous]);
Check(choices.Selected.Count == 2, "Cleanup dialog starts with all eligible findings selected");
choices.Choices[1].Selected = false;
Check(choices.Selected.SequenceEqual([candidateA]), "Deselected item absent from deletion payload");
Check(!candidateA.Selected && !candidateB.Selected, "Dialog selection is isolated from main list until confirmation");
choices.SelectAll(false); Check(choices.Selected.Count == 0, "Clear selection produces empty payload");
choices.SelectAll(true); Check(choices.Selected.Count == 2, "Select all restores eligible candidates only");
var nested = new Finding { Kind = "Klasör", Path = @"C:\UClearFixture\App\Child", Reason = "test", Confidence = "Güçlü eşleşme", CanDelete = true };
var nestedChoices = new CleanupSelection([candidateA, nested]); nestedChoices.Choices[1].Selected = false;
Check(nestedChoices.Conflict != null, "Unselected nested folder cannot be silently deleted through selected parent");
string lockFolder = Path.Combine(workspace, "test-artifacts", "locks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(lockFolder); string lockFile = Path.Combine(lockFolder, "held.txt");
try
{
    using (var held = new FileStream(lockFile, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
    {
        var lockReport = FileLocks.Inspect(lockFolder, CancellationToken.None);
        Check(lockReport.Owners.Any(p => p.Pid == Environment.ProcessId), "Restart Manager identifies process holding fixture file");
        Check(lockReport.Uses.Any(p => p.Pid == Environment.ProcessId && p.File == lockFile), "File-level usage links exact fixture path to PID");
        var singleFileReport = FileLocks.Inspect(lockFile, CancellationToken.None);
        Check(singleFileReport.FilesChecked == 1 && singleFileReport.Owners.Any(p => p.Pid == Environment.ProcessId), "Single-file inspection identifies lock owner without scanning siblings");
    }
    using var cancelledLock = new CancellationTokenSource(); cancelledLock.Cancel();
    Reject(() => FileLocks.Inspect(lockFolder, cancelledLock.Token), "File lock inspection can be cancelled");
}
finally
{
    if (!Safety.IsWithin(lockFolder, Path.Combine(workspace, "test-artifacts"))) throw new Exception("Invalid lock fixture scope");
    File.Delete(lockFile); Directory.Delete(lockFolder);
}
Console.WriteLine($"Total: {count} checks passed, including selection and native file-use detection.");
Check(!ProcessControl.Allowed(new LockOwner(Environment.ProcessId, "Self", "", "Uygulama", 1)), "UClear/current process protected from termination");
Check(!ProcessControl.Allowed(new LockOwner(12345, "Service", "svc", "Servis", 1)), "Service termination blocked");
Check(!ProcessControl.Allowed(new LockOwner(12345, "Critical", "", "Kritik", 1)), "Critical process termination blocked");
Check(!ProcessControl.Allowed(new LockOwner(12345, "NoIdentity", "", "Uygulama", 0)), "Process without creation timestamp blocked");
string processFolder = Path.Combine(workspace, "test-artifacts", "process-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(processFolder); string processFile = Path.Combine(processFolder, "held.txt");
var info = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
info.ArgumentList.Add("--lock-fixture"); info.ArgumentList.Add(processFile);
using var childProcess = System.Diagnostics.Process.Start(info)!;
try
{
    Check(childProcess.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult() == "ready", "Isolated child fixture acquired file lock");
    Check(FileLocks.DeleteAccessIssue(processFile)?.Contains("Paylaşım") == true, "Read-only deletion probe identifies sharing violation without deleting file");
    var owner = FileLocks.Query([processFile], CancellationToken.None).Single(p => p.Pid == childProcess.Id);
    Check(owner.StartTime > 0, "Restart Manager process creation timestamp captured");
    Reject(() => ProcessControl.Stop(owner with { StartTime = owner.StartTime + 1 }, true).GetAwaiter().GetResult(), "Stale process identity rejected before termination");
    Check(!childProcess.HasExited, "Stale-identity check leaves process running");
    ProcessControl.Stop(owner, true).GetAwaiter().GetResult();
    Check(childProcess.WaitForExit(3000), "Explicit termination stops only isolated child fixture");
    Check(FileLocks.DeleteAccessIssue(processFile) == null && File.Exists(processFile), "Termination releases lock and preserves file");
}
finally
{
    if (!childProcess.HasExited) { childProcess.Kill(); childProcess.WaitForExit(); }
    if (!Safety.IsWithin(processFolder, Path.Combine(workspace, "test-artifacts"))) throw new Exception("Invalid fixture path");
    File.Delete(processFile); Directory.Delete(processFolder);
}
Console.WriteLine($"Total: {count} checks passed, including folder diagnosis and guarded process termination.");
string treeFile = Path.Combine(workspace, "test-artifacts", "tree-" + Guid.NewGuid().ToString("N") + ".txt");
var treeInfo = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
treeInfo.ArgumentList.Add("--tree-fixture"); treeInfo.ArgumentList.Add(treeFile);
using var treeRoot = System.Diagnostics.Process.Start(treeInfo)!;
System.Diagnostics.Process? treeChild = null;
try
{
    int childId = int.Parse(treeRoot.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult()!);
    treeChild = System.Diagnostics.Process.GetProcessById(childId);
    var treeOwner = new LockOwner(treeRoot.Id, "Fixture", "", "Uygulama", treeRoot.StartTime.ToUniversalTime().ToFileTimeUtc());
    ProcessControl.Stop(treeOwner, true).GetAwaiter().GetResult();
    Check(treeRoot.WaitForExit(3000) && treeChild.WaitForExit(3000), "Force termination closes both selected process and its child");
    Check(FileLocks.DeleteAccessIssue(treeFile) == null && File.Exists(treeFile), "Descendant file lock released without deleting the file");
}
finally
{
    if (!treeRoot.HasExited) { treeRoot.Kill(true); treeRoot.WaitForExit(); }
    if (treeChild != null) { if (!treeChild.HasExited) { treeChild.Kill(); treeChild.WaitForExit(); } treeChild.Dispose(); }
    File.Delete(treeFile);
}
Console.WriteLine($"Total: {count} checks passed, including process-tree termination.");

string utilityRoot = Path.Combine(workspace, "test-artifacts", "utilities-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(utilityRoot);
string fixtureKey = @"Software\UClearTests\" + Guid.NewGuid().ToString("N");
try
{
    string nestedDir = Path.Combine(utilityRoot, "nested"); Directory.CreateDirectory(nestedDir);
    string emptyDir = Path.Combine(utilityRoot, "empty"); Directory.CreateDirectory(emptyDir);
    string old = Path.Combine(nestedDir, "old.tmp"), recent = Path.Combine(nestedDir, "recent.tmp");
    File.WriteAllBytes(old, new byte[100]); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-10));
    File.WriteAllBytes(recent, new byte[20]);
    var largeReport = DiskTools.Scan(utilityRoot, "large", CancellationToken.None);
    Check(largeReport.Entries.Single(e => e.Path == utilityRoot).Bytes == 120, "Folder sizes aggregate descendants exactly once");
    var tempReport = DiskTools.Scan(utilityRoot, "temp", CancellationToken.None);
        Check(tempReport.Entries.Count == 1 && tempReport.Entries[0].Path == old, "Temp scanner excludes recently modified files");
    var allDates = DiskTools.Scan(utilityRoot, "temp", CancellationToken.None, 0);
    Check(allDates.Entries.Count == 2 && allDates.Entries.Any(e => e.Path == recent), "All-dates Temp mode includes recent files");
    int selectionChanges = 0;
    allDates.Entries[0].PropertyChanged += (_, _) => selectionChanges++;
    allDates.Entries[0].Selected = true;
    Check(selectionChanges == 1 && allDates.Entries[0].Selected, "Disk selection checkbox model notifies changes");
    Check(new ScanProgress(3, 4, "test").Percent == 75 && new ScanProgress(4, 4, "test").Text.Contains("100"), "Known-total progress reports accurate percentages");
    var emptyReport = DiskTools.Scan(utilityRoot, "empty", CancellationToken.None);
    Check(emptyReport.Entries.Count == 1 && emptyReport.Entries[0].Path == emptyDir, "Only truly empty child folders are listed");
    File.WriteAllText(Path.Combine(emptyDir, "new.txt"), "keep");
    Reject(() => DiskTools.Delete(emptyReport.Entries[0], utilityRoot, "empty"), "Folder populated after scan is not recursively deleted");
    File.AppendAllText(old, "changed");
    Reject(() => DiskTools.Delete(tempReport.Entries[0], utilityRoot, "temp"), "Changed temp file rejected at deletion");
    Reject(() => DiskTools.Delete(largeReport.Entries.First(e => e.Path == recent), nestedDir + "Other", "large"), "Deletion outside scan boundary rejected");
    DiskTools.Delete(largeReport.Entries.First(e => e.Path == recent), utilityRoot, "large");
    Check(!File.Exists(recent), "Selected unchanged file removed");
    using var cancelledUtilities = new CancellationTokenSource(); cancelledUtilities.Cancel();
    Reject(() => DiskTools.Scan(utilityRoot, "large", cancelledUtilities.Token), "Utility scan cancellation supported");
    HostsTools.Validate("127.0.0.1 example.test # comment\n::1 localhost\n# disabled");
    Check(true, "Hosts supports IPv4 IPv6 and comments");
    Reject(() => HostsTools.Validate("bad-ip example.test"), "Invalid hosts mapping rejected");
    string hostsFixture = Path.Combine(utilityRoot, "hosts"); File.WriteAllText(hostsFixture, "# original\n");
    Reject(() => HostsTools.Save(hostsFixture, "# stale", HostsTools.Default), "Concurrent hosts edits are preserved");
    HostsTools.Save(hostsFixture, "# original\n", "127.0.0.1 example.test\n");
    Check(File.ReadAllText(hostsFixture) == "127.0.0.1 example.test\n", "Hosts save writes validated content");
    string runFixture = fixtureKey + @"\Run", disabledFixture = fixtureKey + @"\Disabled";
    using (var key = testHive.CreateSubKey(runFixture)) key.SetValue("Fixture", @"%TEMP%\fixture.exe", RegistryValueKind.ExpandString);
    var startupFixture = new StartupEntry("Fixture", @"%TEMP%\fixture.exe", "CurrentUser", "Registry64", false, RegistryValueKind.ExpandString);
    StartupTools.ToggleAt(startupFixture, runFixture, disabledFixture);
    using (var key = testHive.OpenSubKey(runFixture)) Check(key!.GetValue("Fixture") == null, "Disable removes startup command from Run");
    StartupTools.ToggleAt(startupFixture with { Disabled = true }, runFixture, disabledFixture);
    using (var key = testHive.OpenSubKey(runFixture)) Check(key!.GetValueKind("Fixture") == RegistryValueKind.ExpandString && (string?)key.GetValue("Fixture", null, RegistryValueOptions.DoNotExpandEnvironmentNames) == startupFixture.Command, "Re-enable preserves raw command and Registry type");
    using (var key = testHive.CreateSubKey(disabledFixture)) key.SetValue("Fixture", "other");
    Reject(() => StartupTools.ToggleAt(startupFixture, runFixture, disabledFixture), "Startup destination collision cannot overwrite another command");
}
finally
{
    if (!Safety.IsWithin(utilityRoot, Path.Combine(workspace, "test-artifacts"))) throw new Exception("Invalid utility fixture path");
    Directory.Delete(utilityRoot, true);
    testHive.DeleteSubKeyTree(fixtureKey, false);
}
Console.WriteLine($"Total: {count} checks passed, including utilities.");

byte[] enabledApproval = new byte[12]; enabledApproval[0] = 2;
byte[] disabledApproval = new byte[12]; disabledApproval[0] = 3;
Check(StartupTools.DecodeApproval(enabledApproval) == true && StartupTools.DecodeApproval(disabledApproval) == false, "StartupApproved differentiates Windows enabled and disabled states");
Check(StartupTools.DecodeApproval(new byte[] { 3 }) == null && StartupTools.DecodeApproval(new byte[12]) == null, "Unknown and truncated approval values are not reported enabled");
Check(StartupTools.ApprovalGroup(RegistryHive.LocalMachine, RegistryView.Registry32) == "Run32" && StartupTools.ApprovalGroup(RegistryHive.CurrentUser, RegistryView.Registry32) == "Run", "Startup approval mapping respects HKCU shared and HKLM 32-bit views");
string invalidRoot = Path.Combine(workspace, "test-artifacts", "invalid-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(invalidRoot);
string missingExe = Path.Combine(invalidRoot, "missing.exe");
string commandKey = @"Software\Classes\UClearFixture" + Guid.NewGuid().ToString("N") + @"\shell\open\command";
string fixtureBase = commandKey[..commandKey.IndexOf(@"\shell", StringComparison.Ordinal)];
try
{
    Check(InvalidReferences.Probe('"' + missingExe + "\" --test").Missing, "Quoted missing local executable detected despite spaces in path");
    Check(!InvalidReferences.Probe(missingExe + " --test").Missing, "Ambiguous unquoted path with spaces is not deletable");
    Check(!InvalidReferences.Probe("rundll32.exe missing.dll,Start").Missing, "Indirect launcher not mistaken for a missing executable");
    Check(!InvalidReferences.Probe(@"\\offline-server\share\missing.exe").Missing, "Unreachable network target cannot become deletion evidence");
    Check(!InvalidReferences.Probe("%UCLEAR_UNKNOWN_VARIABLE%\\missing.exe").Missing, "Unresolved environment variable cannot become deletion evidence");
    Check(!InvalidReferences.Probe("").Missing, "Empty Registry command is not automatically considered safe to delete");
    File.WriteAllText(Path.Combine(invalidRoot, "keep.dll"), "fixture");
    string leftover = Path.Combine(invalidRoot, "leftover"); Directory.CreateDirectory(Path.Combine(leftover, "data")); File.WriteAllText(Path.Combine(leftover, "data", "settings.json"), "{}");
    var leftoverProbe = InvalidReferences.Probe('"' + Path.Combine(leftover, "app.exe") + '"');
    Check(leftoverProbe.Missing && leftoverProbe.ProgramGone, "Folder left with only data files counts as uninstalled");
    File.WriteAllText(Path.Combine(leftover, "data", "helper.dll"), "fixture");
    Check(!InvalidReferences.Probe('"' + Path.Combine(leftover, "app.exe") + '"').ProgramGone, "Remaining program files keep a missing target out of certain cleanup");
    string fixtureTasks = Path.Combine(invalidRoot, "Tasks"); Directory.CreateDirectory(Path.Combine(fixtureTasks, "Microsoft", "Windows"));
    string goneTarget = Path.Combine(invalidRoot, "gone", "task.exe");
    string TaskXml(params string[] actions) => "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Actions Context=\"Author\">" + string.Concat(actions) + "</Actions></Task>";
    string Exec(string commandPath) => "<Exec><Command>" + System.Security.SecurityElement.Escape(commandPath) + "</Command></Exec>";
    File.WriteAllText(Path.Combine(fixtureTasks, "Vendor Task"), TaskXml(Exec(goneTarget)));
    File.WriteAllText(Path.Combine(fixtureTasks, "Microsoft", "Windows", "Builtin"), TaskXml(Exec(goneTarget)));
    File.WriteAllText(Path.Combine(fixtureTasks, "Two Actions"), TaskXml(Exec(goneTarget), Exec(Path.Combine(Environment.SystemDirectory, "cmd.exe"))));
    File.WriteAllText(Path.Combine(fixtureTasks, "Profile Variable"), TaskXml(Exec("%LOCALAPPDATA%\\uclear-gone-" + Guid.NewGuid().ToString("N") + "\\x\\task.exe")));
    var vendorTask = InvalidReferences.TaskItems(fixtureTasks, Path.Combine(fixtureTasks, "Vendor Task")).Single();
    Check(vendorTask.CanDelete && vendorTask.Certain && vendorTask.Name == @"\Vendor Task", "Third-party task with a single missing action is certain");
    Check(!InvalidReferences.TaskItems(fixtureTasks, Path.Combine(fixtureTasks, "Microsoft", "Windows", "Builtin")).Single().CanDelete, "Windows \\Microsoft\\ task is never deletable");
    Check(!InvalidReferences.TaskItems(fixtureTasks, Path.Combine(fixtureTasks, "Two Actions")).Single().CanDelete, "Task with another action is review-only");
    Check(InvalidReferences.TaskItems(fixtureTasks, Path.Combine(fixtureTasks, "Profile Variable")).All(i => !i.CanDelete), "Task using profile variables is review-only");
    Reject(() => InvalidReferences.Delete(vendorTask), "Task outside the Windows task store is never deleted");
    var service = InvalidReferences.RegistryItem("Servis", @"HKLM\SYSTEM\CurrentControlSet\Services\UClearMissing\Parameters", "ImagePath", "Registry64", '"' + goneTarget + '"', RegistryValueKind.String, true)!;
    Reject(() => InvalidReferences.Delete(service), "Service delete requires a direct service key");
    var fileOnly = InvalidReferences.Probe('"' + missingExe + '"');
    var folderGone = InvalidReferences.Probe('"' + Path.Combine(invalidRoot, "gone", "missing.exe") + '"');
    Check(fileOnly.Missing && !fileOnly.ProgramGone && folderGone.Missing && folderGone.ProgramGone, "Only a vanished program folder qualifies as certain absence");
    string defenderPath = '"' + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "uclear-missing-" + Guid.NewGuid().ToString("N"), "x.exe") + '"';
    var windowsComponent = InvalidReferences.RegistryItem("Registry", "HKCU\\" + commandKey, "", "Registry64", defenderPath, RegistryValueKind.String, true);
    Check(windowsComponent != null && !windowsComponent.CanDelete && !windowsComponent.Certain, "Windows component outside the Windows folder is review-only");
    foreach (string path in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "x.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "x.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "x.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Common Files", "x.dll") })
        Check(InvalidReferences.Protected(path), "Protected Windows/Microsoft location: " + path);
    Check(!InvalidReferences.Protected(missingExe), "Ordinary program location is not protected");
    string native = Environment.GetEnvironmentVariable("ProgramW6432")!, x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    Check(InvalidReferences.Counterparts(Path.Combine(native, "Vendor", "a.dll")).Single().Equals(Path.Combine(x86, "Vendor", "a.dll"), StringComparison.OrdinalIgnoreCase) && InvalidReferences.Counterparts(Path.Combine(x86, "Vendor", "a.dll")).Single().Equals(Path.Combine(native, "Vendor", "a.dll"), StringComparison.OrdinalIgnoreCase), "32/64 bit Program Files counterpart is checked before declaring a target missing");
    string command = '"' + missingExe + '"';
    using (var key = testHive.CreateSubKey(commandKey)) { key.SetValue("", command); key.SetValue("Keep", "untouched"); }
    var finding = InvalidReferences.RegistryItem("Registry", "HKCU\\" + commandKey, "", "Registry64", command, RegistryValueKind.String, true)!;
    Check(finding.CanDelete && finding.Probe.Missing && !finding.Certain, "Missing target in an existing folder is selectable but not certain");
    string goneCommand = '"' + Path.Combine(invalidRoot, "gone", "missing.exe") + '"';
    Check(InvalidReferences.RegistryItem("Registry", "HKCU\\" + commandKey, "", "Registry64", goneCommand, RegistryValueKind.String, true)!.Certain, "Missing target with vanished program folder is certain");
    File.WriteAllText(missingExe, "fixture");
    Reject(() => InvalidReferences.Delete(finding), "Reappearing target blocks stale finding deletion"); File.Delete(missingExe);
    using (var key = testHive.OpenSubKey(commandKey, true)) key!.SetValue("", command + " --changed");
    Reject(() => InvalidReferences.Delete(finding), "Changed Registry command blocks deletion");
    using (var key = testHive.OpenSubKey(commandKey, true)) key!.SetValue("", command);
    InvalidReferences.Delete(finding);
    using (var key = testHive.OpenSubKey(commandKey)) Check(key!.GetValue("") == null && (string?)key.GetValue("Keep") == "untouched", "Only invalid default value removed; sibling data and parent survive");
    var review = InvalidReferences.RegistryItem("Servis", "HKLM\\SYSTEM\\fixture", "ImagePath", "Registry64", command, RegistryValueKind.String, false)!;
    Reject(() => InvalidReferences.Delete(review), "Service review finding cannot be deleted by value cleanup");
    using var cancelledReferences = new CancellationTokenSource(); cancelledReferences.Cancel();
    Reject(() => InvalidReferences.Scan(cancelledReferences.Token), "Invalid-reference scan can be cancelled");
}
finally
{
    if (!Safety.IsWithin(invalidRoot, Path.Combine(workspace, "test-artifacts"))) throw new Exception("Fixture out of scope");
    Directory.Delete(invalidRoot, true); testHive.DeleteSubKeyTree(fixtureBase, false);
}
var vsCode = new InstalledApp("Microsoft Visual Studio Code (User)", "Microsoft Corporation", "1.0", "", "\"C:\\Users\\u\\AppData\\Local\\Programs\\Microsoft VS Code\\unins000.exe\"", "", "Registry64", 0, "", "C:\\Users\\u\\AppData\\Local\\Programs\\Microsoft VS Code\\Code.exe");
var chrome = new InstalledApp("Google Chrome", "Google LLC", "1.0", "", "", "", "Registry64", 0, "");
var firefox = new InstalledApp("Mozilla Firefox", "Mozilla", "1.0", "", "", "", "Registry64", 0, "");
string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
Check(LeftoverFinder.Owner("Code", Path.Combine(appData, "Code"), [vsCode]) == "app", "Data folder named after an installed program's executable is owned");
Check(LeftoverFinder.Owner("Cursor", Path.Combine(appData, "Cursor"), [vsCode, chrome]) == "", "Folder of an uninstalled program is a leftover candidate");
Check(LeftoverFinder.Owner("Google", Path.Combine(appData, "Google"), [chrome]) == "publisher", "Pure vendor folder is opened to look for removed products");
Check(LeftoverFinder.Owner("Mozilla Maintenance Service", Path.Combine(appData, "Mozilla Maintenance Service"), [firefox]) == "app", "Vendor-prefixed product folder of an installed vendor is owned");
Check(LeftoverFinder.Owner("Intel", Path.Combine(appData, "Intel"), [], ["Intel Corporation"]) == "app", "Hardware driver vendor folders are never proposed");
string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
foreach (string identity in new[] { "Microsoft.WindowsStore", "Microsoft.DesktopAppInstaller", "Microsoft.SecHealthUI", "MicrosoftWindows.Client.Core", "Microsoft.LanguageExperiencePacktr-TR", "Microsoft.HEIFImageExtension", "Microsoft.WebMediaExtensions", "MicrosoftCorporationII.WinAppRuntime.Main.1.8", "Microsoft.Winget.Source" })
    Check(StoreApps.IsEssential(identity, Path.Combine(programFiles, "WindowsApps", identity)), "Windows-owned Store package protected: " + identity);
Check(StoreApps.IsEssential("SomeVendor.App", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps", "x")), "Package installed inside the Windows folder is protected");
foreach (string identity in new[] { "SpotifyAB.SpotifyMusic", "Microsoft.WindowsCalculator", "Microsoft.ZuneVideo" })
    Check(!StoreApps.IsEssential(identity, Path.Combine(programFiles, "WindowsApps", identity)), "Ordinary Store app removable: " + identity);
var msiApp = new InstalledApp("Fixture MSI", "", "", "", "MsiExec.exe /I{11111111-2222-3333-4444-555555555555}", "", "Registry64", 0, "");
var msiQuiet = Inventory.QuietUninstaller(msiApp)!;
Check(Path.GetFileName(msiQuiet.FileName).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) && msiQuiet.Arguments.Contains("/X{", StringComparison.OrdinalIgnoreCase) && msiQuiet.Arguments.Contains("/qn") && !msiQuiet.Arguments.Contains("/I{", StringComparison.OrdinalIgnoreCase), "Windows Installer programs are removed silently with /X, never reinstalled with /I");
Check(Inventory.QuietUninstaller(new InstalledApp("Fixture", "", "", "", "\"C:\\uclear-missing\\uninstall.exe\"", "", "Registry64", 0, "")) == null, "Programs without a silent command fall back to their normal uninstaller window");
Check(Inventory.QuietUninstaller(new InstalledApp("Fixture", "", "", "", "MsiExec.exe /X{11111111-2222-3333-4444-555555555555}", "", "Registry64", 0, "", "", "\"C:\\uclear-missing\\quiet.exe\" /S"))!.Arguments.Contains("/qn"), "An unusable quiet command does not block the silent MSI route");
var protectedApp = new InstalledApp("Store", "Microsoft", "1", Path.Combine(programFiles, "WindowsApps", "x"), "", "", "Store", 0, "", "", "", "Microsoft.WindowsStore_1.0.0.0_x64__8wekyb3d8bbwe", true);
Reject(() => StoreApps.Remove(protectedApp, CancellationToken.None).GetAwaiter().GetResult(), "Windows-owned Store package removal blocked in engine");
L.Language = "en";
Check(L.T("Tara", "Scan") == "Scan" && L.V("Silindi · doğrulandı") == "Deleted · verified" && L.V("Kısayol") == "Shortcut", "English mode shows English texts and translated engine values");
Check(L.V("Silinemedi: Access denied") == "Not deleted: Access denied" && L.V(@"C:\Some\Path") == @"C:\Some\Path", "Result prefixes are translated; ordinary values pass through unchanged");
Check(new ScanProgress(5, null, "Kısayol").Text.StartsWith("Shortcut") && new ScanProgress(1, 4, "Registry").Text.Contains("25%"), "Progress text follows the interface language");
string englishProbe = Path.Combine(workspace, "test-artifacts");
Check(CleanupVerification.Check(new Finding { Kind = "Klasör", Path = englishProbe, Reason = "test", Confidence = "test" }) == "Hâlâ mevcut", "Engine result values stay language-independent, so cleanup logic is unaffected by the interface language");
Check(!InvalidReferences.Probe("").Missing && InvalidReferences.Probe('"' + Path.Combine(englishProbe, "uclear-missing-" + Guid.NewGuid().ToString("N"), "x.exe") + '"').State == "Hedef yolu bulunamadı", "Reference states stay identifiers in English mode");
L.Language = "tr";
Console.WriteLine($"Total: {count} checks passed, including invalid references and startup state.");

