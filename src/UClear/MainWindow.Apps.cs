using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace UClear;

// App list actions: right-click menu, Store removal, force removal and bulk removal.
public partial class MainWindow
{
    // Texts are read when a window is built, so a language change rebuilds the main window in place.
    private void Language_Click(object sender, RoutedEventArgs e)
    {
        if (busy) { Status.Text = L.T("Dili değiştirmek için devam eden işlemin bitmesini bekle.", "Wait for the current operation to finish before changing the language."); return; }
        L.Language = L.En ? "tr" : "en"; L.Save();
        var next = new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = Left, Top = Top, Width = Width, Height = Height };
        Application.Current.MainWindow = next; next.Show();
        if (WindowState == WindowState.Maximized) next.WindowState = WindowState.Maximized;
        Close();
    }
    private void AppList_RightClick(object sender, MouseButtonEventArgs e)
    {
        // Right-click selects the row under the pointer so the menu acts on it.
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(AppList, source) is ListBoxItem item) item.IsSelected = true;
    }
    private void AppMenu_Opened(object sender, RoutedEventArgs e)
    {
        var app = AppList.SelectedItem as InstalledApp;
        if (app == null) { AppMenu.IsOpen = false; return; }
        MenuUninstall.IsEnabled = !busy;
        MenuScan.IsEnabled = MenuForce.IsEnabled = !busy && !app.IsStore;
        MenuFolder.IsEnabled = Directory.Exists(app.Location);
        MenuRegistry.IsEnabled = !app.IsStore && app.RegistryPath.Length > 0;
        MenuCopyCommand.IsEnabled = app.UninstallCommand.Length > 0;
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (AppList.SelectedItem is not InstalledApp app || !Directory.Exists(app.Location)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + app.Location + "\"") { UseShellExecute = true }); } catch (Exception error) { Error(error); }
    }
    // Regedit opens at the key it last showed; that preference is set to the program's uninstall entry first.
    private void OpenRegistry_Click(object sender, RoutedEventArgs e)
    {
        if (AppList.SelectedItem is not InstalledApp { RegistryPath.Length: > 0 } app) return;
        try
        {
            string path = app.RegistryPath;
            bool machine = path.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase);
            string rest = path[5..];
            if (machine && app.View == "Registry32" && rest.StartsWith("SOFTWARE\\", StringComparison.OrdinalIgnoreCase)) rest = "SOFTWARE\\WOW6432Node\\" + rest[9..];
            using (var applet = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
            {
                // The root node is localized ("Computer", "Bilgisayar"…); regedit ignores a path with the wrong name, so its own saved name is reused.
                string saved = applet.GetValue("LastKey") as string ?? "";
                string computer = saved.Length > 0 ? saved.Split('\\')[0] : System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "Bilgisayar" : "Computer";
                applet.SetValue("LastKey", $@"{computer}\{(machine ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER")}\{rest}");
            }
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "regedit.exe"), "/m") { UseShellExecute = true });
        }
        catch (Exception error) { Error(error); }
    }
    private void SearchWeb_Click(object sender, RoutedEventArgs e)
    {
        if (AppList.SelectedItem is not InstalledApp app) return;
        try { Process.Start(new ProcessStartInfo("https://www.google.com/search?q=" + Uri.EscapeDataString(app.Name)) { UseShellExecute = true }); } catch (Exception error) { Error(error); }
    }
    private void CopyName_Click(object sender, RoutedEventArgs e) { if (AppList.SelectedItem is InstalledApp app) Copy(app.Name); }
    private void CopyCommand_Click(object sender, RoutedEventArgs e) { if (AppList.SelectedItem is InstalledApp app) Copy(app.UninstallCommand); }
    private void Copy(string text)
    {
        try { Clipboard.SetText(text); Status.Text = L.T("Kopyalandı: ", "Copied: ") + text; }
        catch (System.Runtime.InteropServices.ExternalException) { Status.Text = L.T("Panoya erişilemedi. Tekrar deneyebilirsin.", "The clipboard is not available. Try again."); }
    }

    private async Task UninstallStore(InstalledApp app)
    {
        if (app.SystemPackage) { AppDialog.Show(this, L.T($"{app.Name} Windows'a ait bir bileşen; kaldırılmaz.", $"{app.Name} is part of Windows; it is not removed."), L.T("Kaldırılamaz", "Cannot be removed")); return; }
        if (AppDialog.Show(this, L.T($"{app.Name} (Microsoft Store uygulaması) kaldırılacak. Windows, uygulamanın bu kullanıcıya ait verilerini de siler. İstersen Store'dan yeniden yükleyebilirsin.\n\nDevam edilsin mi?", $"{app.Name} (Microsoft Store app) will be removed. Windows also deletes the app's data for this user. You can reinstall it from the Store at any time.\n\nContinue?"), L.T("Store uygulamasını kaldır", "Remove Store app"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        SetBusy(true); Status.Text = L.T($"{app.Name} kaldırılıyor…", $"Removing {app.Name}…");
        try
        {
            await StoreApps.Remove(app, CancellationToken.None);
            Log(app.Name, "Store kaldırma", L.T("Kaldırıldı", "Removed"), app.PackageFullName);
            Status.Text = L.T($"{app.Name} kaldırıldı.", $"{app.Name} was removed.");
        }
        catch (Exception error) { Log(app.Name, "Store kaldırma", L.T("Kaldırılamadı: ", "Could not remove: ") + error.Message); Error(error); }
        finally { SetBusy(false); }
        await RefreshApps();
    }

    // For programs whose uninstaller is broken or missing: the uninstaller is skipped and every safe trace is deleted directly.
    private async void ForceUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (busy || AppList.SelectedItem is not InstalledApp app || app.IsStore) return;
        var running = RunningFrom(app);
        if (running.Count > 0) { AppDialog.Show(this, L.T($"{app.Name} şu anda çalışıyor ({string.Join(", ", running)}). Önce programı kapat, sonra tekrar dene.", $"{app.Name} is running right now ({string.Join(", ", running)}). Close the program first, then try again."), L.T("Zorla kaldır", "Force uninstall")); return; }
        if (AppDialog.Show(this, L.T($"{app.Name} kaldırıcısı çalıştırılmadan kaldırılacak: kurulum klasörü, Registry kayıtları, kaldırma kaydı ve bulunan diğer izler doğrudan silinir.\n\nBunu yalnızca programın kendi kaldırıcısı bozuksa veya çalışmıyorsa kullan. Servis, sürücü ve zamanlanmış görevler silinmez, sadece gösterilir. Başka programlarla ortak olan veya emin olunamayan öğeler atlanır.\nSilme kalıcıdır, yedek alınmaz.\n\nDevam edilsin mi?", $"{app.Name} will be removed without running its uninstaller: the install folder, Registry entries, the uninstall entry and other traces found are deleted directly.\n\nUse this only if the program's own uninstaller is broken or does not work. Services, drivers and scheduled tasks are not deleted, only shown. Items shared with other programs or not certain are skipped.\nDeletion is permanent; no backup is made.\n\nContinue?"), L.T("Zorla kaldır", "Force uninstall"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        Log(app.Name, "Zorla kaldırma", "Başlatıldı");
        var target = From(app);
        if (!await Scan(target)) return;
        target = activeTarget ?? target;
        var eligible = CleanupPolicy.Eligible(findings);
        if (eligible.Count == 0) { Status.Text = L.T("Güvenle silinebilecek iz bulunamadı; bulunanları listeden inceleyebilirsin.", "No traces that can be deleted safely were found; you can review the findings in the list."); return; }
        await ExecuteCleanup(target, eligible);
        await RefreshApps();
    }
    // Processes started from the program's install folder; deleting under a running program fails half-way.
    private static List<string> RunningFrom(InstalledApp app)
    {
        if (!Path.IsPathFullyQualified(app.Location) || !Directory.Exists(app.Location)) return [];
        try { return LeftoverFinder.RunningPrograms().Where(p => Safety.IsWithin(p, app.Location)).Select(Path.GetFileName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (Exception e) when (e is ArgumentException or IOException) { return []; }
    }

    private async void BulkUninstall_Click(object sender, RoutedEventArgs e) => await BulkUninstall();
    // One confirmation, then each program is uninstalled in turn (silently when it supports that); leftovers of all
    // successfully removed programs are found in one shared scan and the safe ones deleted — no stops in between.
    private async Task BulkUninstall()
    {
        if (busy) return;
        var chosen = apps.Where(a => a.Selected && a.CanRemove).ToList();
        if (chosen.Count == 0) return;
        var running = chosen.Select(a => (App: a, Names: RunningFrom(a))).Where(x => x.Names.Count > 0).ToList();
        if (running.Count > 0) { AppDialog.Show(this, L.T("Şu programlar çalışıyor; önce kapat:\n\n", "These programs are running; close them first:\n\n") + string.Join("\n", running.Select(x => $"• {x.App.Name} ({string.Join(", ", x.Names)})")), L.T("Toplu kaldırma", "Bulk uninstall")); return; }
        int silent = chosen.Count(a => a.IsStore || Inventory.QuietUninstaller(a) != null);
        string list = string.Join("\n", chosen.Take(40).Select(a => $"• {a.Name}" + (a.IsStore ? " (Store)" : Inventory.QuietUninstaller(a) != null ? L.T(" (sessiz)", " (silent)") : ""))) + (chosen.Count > 40 ? L.T($"\n… ve {chosen.Count - 40} uygulama daha", $"\n… and {chosen.Count - 40} more apps") : "");
        if (AppDialog.Show(this, L.T($"{chosen.Count} uygulama sırayla kaldırılacak. {silent} tanesi pencere açmadan kaldırılır; diğerlerinde programın kendi kaldırma penceresini tamamlaman gerekir.\n\nKaldırma başarılı olursa kalıntılar (klasörler, Registry kayıtları) otomatik bulunup silinir. Başka programla ortak olan veya emin olunamayan öğeler atlanır.\nSilme kalıcıdır, yedek alınmaz.\n\n{list}", $"{chosen.Count} apps will be uninstalled one after another. {silent} of them are removed without a window; for the others you need to complete the program's own uninstall window.\n\nIf an uninstall succeeds, its leftovers (folders, Registry entries) are found and deleted automatically. Items shared with another program or not certain are skipped.\nDeletion is permanent; no backup is made.\n\n{list}"), L.T("Toplu kaldırma", "Bulk uninstall"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

        ClearScan(); SetBusy(true); cancellation = new(); CancelButton.Visibility = Visibility.Visible;
        FindingsGrid.IsEnabled = true; EmptyState.Visibility = Visibility.Collapsed;
        var outcomes = new List<string>(); var removedTargets = new List<Target>(); int uninstalled = 0;
        int removed = 0, skipped = 0, failed = 0;
        var token = cancellation.Token;
        try
        {
            for (int i = 0; i < chosen.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var app = chosen[i];
                ResultsTitle.Text = L.T($"Toplu kaldırma · {i + 1}/{chosen.Count}", $"Bulk uninstall · {i + 1}/{chosen.Count}");
                Status.Text = L.T($"{i + 1}/{chosen.Count} · {app.Name} kaldırılıyor…", $"{i + 1}/{chosen.Count} · Uninstalling {app.Name}…");
                string outcome = app.IsStore ? await RemoveStoreQuietly(app, token) : await RunUninstaller(app, token);
                // "Kaldırıldı" is the engine's success value; only its display is translated.
                Log(app.Name, "Toplu kaldırma", outcome);
                if (outcome == "Kaldırıldı" && !app.IsStore) removedTargets.Add(From(app));
            }
            if (removedTargets.Count > 0)
            {
                ResultsTitle.Text = L.T("Toplu kaldırma · kalıntılar", "Bulk uninstall · leftovers");
                Status.Text = L.T($"{removedTargets.Count} programın kalıntıları aranıyor…", $"Searching for leftovers of {removedTargets.Count} programs…");
                var live = LiveProgress();
                var results = await Task.Run(() => new Scanner().ScanMany(removedTargets, token, live));
                var current = await Task.Run(Inventory.Read);
                var handled = new HashSet<(string, string, string, string?)>();
                for (int i = 0; i < results.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var target = results[i].ResolvedTarget ?? removedTargets[i];
                    var found = Protect(results[i].Findings, target, current).Where(f => handled.Add((f.Kind, f.Path.ToUpperInvariant(), f.View, f.ValueName))).ToList();
                    var eligible = CleanupPolicy.Eligible(found);
                    foreach (var item in found.Where(f => !eligible.Contains(f))) { item.Result = "Atlandı · inceleme gerekli"; skipped++; findings.Add(item); }
                    Status.Text = L.T($"{target.Name}: {eligible.Count} kalıntı siliniyor…", $"{target.Name}: deleting {eligible.Count} leftovers…");
                    foreach (var item in eligible)
                    {
                        findings.Add(item);
                        try
                        {
                            await Task.Run(() => Cleaner.Delete(item, target));
                            item.Result = await Task.Run(() => CleanupVerification.Check(item));
                            if (item.Result == "Silindi · doğrulandı") removed++; else failed++;
                            Log(target.Name, "Silindi", item.DisplayPath + " [" + item.View + "]", item.DisplayPath);
                        }
                        catch (Exception error) { failed++; item.Result = "Silinemedi: " + error.Message; Log(target.Name, "Silinemedi", item.DisplayPath + " — " + error.Message, item.DisplayPath); }
                    }
                }
            }
            string cleanup = removedTargets.Count == 0 ? "" : L.T($"\n\nKalıntılar: {removed} öğe silindi", $"\n\nLeftovers: {removed} items deleted") + (skipped > 0 ? L.T($", {skipped} öğe emin olunamadığı için bırakıldı", $", {skipped} items kept because they were not certain") : "") + (failed > 0 ? L.T($", {failed} öğe silinemedi (kullanımda olabilir)", $", {failed} items could not be deleted (may be in use)") : "") + ".";
            ResultsTitle.Text = L.T("Toplu kaldırma tamamlandı", "Bulk uninstall complete");
            ResultsSubtitle.Text = L.T($"{uninstalled}/{chosen.Count} uygulama kaldırıldı · {removed} kalıntı silindi", $"{uninstalled}/{chosen.Count} apps uninstalled · {removed} leftovers deleted");
            Status.Text = ResultsSubtitle.Text + ".";
            AppDialog.Show(this, string.Join("\n", outcomes) + cleanup, L.T("Toplu kaldırma sonucu", "Bulk uninstall result"));
        }
        catch (OperationCanceledException) { Status.Text = L.T($"Durduruldu: {outcomes.Count} uygulama işlendi. Açık bir kaldırma penceresi varsa kendi içinde devam edebilir.", $"Stopped: {outcomes.Count} apps processed. An uninstall window that is still open may continue on its own."); }
        catch (Exception error) { Error(error); }
        finally
        {
            cancellation.Dispose(); cancellation = null; CancelButton.Visibility = Visibility.Collapsed; SetBusy(false);
            BindFindings(); EmptyState.Visibility = findings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        await RefreshApps();
    }
    private static async Task<string> RemoveStoreQuietly(InstalledApp app, CancellationToken token)
    {
        try { return await StoreApps.Remove(app, token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { return "Kaldırılamadı: " + error.Message; }
    }
    // Runs the program's uninstaller (silent variant when available) and reports whether the program is really gone.
    private async Task<string> RunUninstaller(InstalledApp app, CancellationToken token)
    {
        ProcessStartInfo start;
        var quiet = Inventory.QuietUninstaller(app);
        try { start = quiet ?? Inventory.Uninstaller(app.UninstallCommand); }
        catch (InvalidOperationException) { return L.T("Kaldırıcı bulunamadı (sağ tık → Zorla kaldır kullanılabilir)", "Uninstaller not found (right-click → Force uninstall can be used)"); }
        if (quiet == null) Status.Text = L.T($"{app.Name}: açılan kaldırma penceresini tamamla…", $"{app.Name}: complete the uninstall window that opened…");
        bool isMsi = Path.GetFileName(start.FileName).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase);
        int code;
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException(L.T("Kaldırıcı başlatılamadı.", "The uninstaller could not be started."));
            await process.WaitForExitAsync(token); code = process.ExitCode;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { return L.T("Kaldırıcı başlatılamadı: ", "The uninstaller could not be started: ") + error.Message; }
        if (UninstallOutcome.NeedsRestart(isMsi, code)) return L.T("Yeniden başlatma gerekiyor; kalıntılar sonra taranabilir", "Restart required; leftovers can be scanned afterwards");
        if (code != 0) return L.T($"Kaldırıcı {code} koduyla bitti; kalıntılara dokunulmadı", $"The uninstaller exited with code {code}; leftovers were not touched");
        // Some uninstallers hand over to a copy of themselves and exit at once; the entry disappears a little later.
        for (int wait = 0; wait < 90 && Cleaner.RegistryExists(app.RegistryPath, app.View); wait++) await Task.Delay(1000, token);
        return Cleaner.RegistryExists(app.RegistryPath, app.View) ? L.T("Kaldırma tamamlanmadı (kaydı hâlâ duruyor); kalıntılara dokunulmadı", "The uninstall did not complete (its entry is still there); leftovers were not touched") : "Kaldırıldı";
    }
}
