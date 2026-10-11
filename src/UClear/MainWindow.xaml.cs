using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace UClear;

public partial class MainWindow : Window
{
    private List<InstalledApp> apps = [];
    private ObservableCollection<Finding> findings = [];
    private Target? activeTarget;
    private ScanResult? lastScan;
    private CancellationTokenSource? cancellation;
    private bool busy;
    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeGlyph == null) return;
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Data = System.Windows.Media.Geometry.Parse(maximized ? "M 0,3 L 7,3 7,10 0,10 Z M 3,3 L 3,0 10,0 10,7 7,7" : "M 0,0 L 10,0 10,10 0,10 Z");
        MaximizeButton.ToolTip = maximized ? L.T("Geri yükle", "Restore") : L.T("Büyüt", "Maximize");
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, maximized ? L.T("Geri yükle", "Restore") : L.T("Büyüt", "Maximize"));
        WindowFrame.BorderThickness = new Thickness(maximized ? 0 : 1);
    }
    public MainWindow()
    {
        InitializeComponent();
        WindowAppearance.Attach(this);
        FindingsGrid.MouseDoubleClick += (sender, e) => { if (FindingsGrid.SelectedItem is Finding && e.OriginalSource is FrameworkElement { DataContext: Finding }) Details_Click(sender, e); };
        FileDrop.Attach(this, paths =>
        {
            if (busy || !IsEnabled) { Status.Text = L.T("İşlem bitince dosya veya klasörü yeniden bırak.", "Drop the file or folder again when the current operation finishes."); return; }
            foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Path.IsPathFullyQualified(path)) continue;
                new LocksWindow(path) { Owner = this }.ShowDialog();
            }
        });
        FolderLocksNav.ToolTip = L.T("Dosya veya klasörü bu pencereye sürükleyip bırakarak da inceleyebilirsin.", "You can also inspect a file or folder by dragging it onto this window.");
        SelectedIcon.Source = AppIcons.Fallback;
        Loaded += async (_, _) => await RefreshApps();
        Closing += (_, e) => { if (busy) { e.Cancel = true; AppDialog.Show(this, L.T("Devam eden işlem bitince kapatabilirsin. Taramayı durdur düğmesiyle taramayı iptal edebilirsin.", "You can close UClear when the current operation finishes. Use the Stop button to cancel a scan."), L.T("İşlem sürüyor", "Operation in progress")); } };
    }
    private IProgress<ScanProgress> LiveProgress() => new Progress<ScanProgress>(p => { if (!busy) return; Progress.IsIndeterminate = !p.Total.HasValue; Progress.Value = p.Percent; Status.Text = p.Text; });
    private void SetBusy(bool value)
    {
        busy = value; Progress.IsIndeterminate = true; Progress.Value = 0; Progress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        AppsNav.IsEnabled = FormerNav.IsEnabled = HistoryNav.IsEnabled = !value;
        AppList.IsEnabled = SearchBox.IsEnabled = FormerName.IsEnabled = FormerPath.IsEnabled = !value;
        RefreshButton.IsEnabled = !value;
        LocksButton.IsEnabled = !value;
        ToolsNav.IsEnabled = FolderLocksNav.IsEnabled = !value;
        FormerScanButton.IsEnabled = ReportButton.IsEnabled = DetailsButton.IsEnabled = RemoveLeftoversButton.IsEnabled = !value;
        UninstallButton.IsEnabled = !value && AppList.SelectedItem is InstalledApp;
        ScanButton.IsEnabled = !value && AppList.SelectedItem is InstalledApp { IsStore: false };
        FindingsGrid.IsEnabled = !value; UpdateSelection(); UpdateBulkBar();
    }
    private async Task RefreshApps()
    {
        if (busy) return; SetBusy(true);
        try
        {
            apps = await Task.Run(Inventory.ReadAll); Filter();
            foreach (var app in apps) app.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(InstalledApp.Selected)) UpdateBulkBar(); };
            UpdateBulkBar();
            Status.Text = L.T($"{apps.Count(a => !a.IsStore)} masaüstü programı ve {apps.Count(a => a.IsStore)} Store uygulaması yüklü.", $"{apps.Count(a => !a.IsStore)} desktop programs and {apps.Count(a => a.IsStore)} Store apps installed.");
            var batch = apps.ToArray();
            _ = LoadIcons(batch);
        }
        catch (Exception e) { Error(e); }
        finally { SetBusy(false); }
    }
    private void Filter()
    {
        if (AppList == null || AppFilterBox == null || AppSortBox == null) return; string search = SearchBox.Text.Trim();
        IEnumerable<InstalledApp> view = apps.Where(a => a.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) || a.Publisher.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        view = AppFilterBox.SelectedIndex switch
        {
            1 => view.Where(a => !a.IsStore),
            2 => view.Where(a => a.IsStore),
            3 => view.Where(a => a.InstalledOn >= DateTime.Today.AddDays(-30)),
            4 => view.Where(a => a.EstimatedBytes >= 1L << 30),
            _ => view
        };
        view = AppSortBox.SelectedIndex switch
        {
            1 => view.OrderByDescending(a => a.EstimatedBytes).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            2 => view.OrderByDescending(a => a.InstalledOn ?? DateTime.MinValue).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            3 => view.OrderBy(a => a.Publisher.Length == 0).ThenBy(a => a.Publisher, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => view.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
        };
        AppList.ItemsSource = view.ToList();
        AppCount.Text = L.T($"{AppList.Items.Count} uygulama", $"{AppList.Items.Count} apps");
        NoApps.Visibility = AppList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void AppView_Changed(object sender, SelectionChangedEventArgs e) => Filter();
    private void UpdateBulkBar()
    {
        if (BulkBar == null) return;
        int count = apps.Count(a => a.Selected);
        BulkBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BulkCount.Text = L.T($"{count} uygulama seçili", $"{count} apps selected");
        BulkUninstallButton.IsEnabled = !busy && count > 0;
    }
    private void ClearAppSelection_Click(object sender, RoutedEventArgs e) { foreach (var app in apps) app.Selected = false; }
    private void Search_Changed(object sender, TextChangedEventArgs e) => Filter();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshApps();
    private void App_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedName == null) return; var app = AppList.SelectedItem as InstalledApp;
        SelectedName.Text = app?.Name ?? L.T("Listeden bir uygulama seç", "Pick an app from the list");
        SelectedIcon.SetBinding(System.Windows.Controls.Image.SourceProperty, new System.Windows.Data.Binding(nameof(InstalledApp.Icon)) { Source = app ?? new InstalledApp("", "", "", "", "", "", "", 0, "") });
        SelectedDetail.Text = app == null ? "" : app.Detail + "\n" + (app.IsStore ? L.T("Microsoft Store uygulaması", "Microsoft Store app") : string.IsNullOrWhiteSpace(app.Location) ? L.T("Kurulum konumu kayıtlı değil", "No install location registered") : app.Location);
        UninstallButton.IsEnabled = !busy && app != null;
        // Windows removes a Store app's data with the app itself; there are no leftovers to scan for.
        ScanButton.IsEnabled = !busy && app is { IsStore: false };
        if (!busy) ClearScan();
    }
    private void ClearScan()
    {
        activeTarget = null; lastScan = null; findings = []; BindFindings();
        EmptyState.Visibility = Visibility.Visible; EmptyTitle.Text = L.T("İzleri birlikte inceleyelim.", "Let's review the traces together."); EmptyDescription.Text = L.T("Bir uygulama seçerek veya eski uygulamanın adını yazarak başla.", "Start by picking an app or typing the name of a removed one.");
        ResultsTitle.Text = L.T("Kalıntı incelemesi", "Leftover review"); ResultsSubtitle.Text = L.T("Tarama sonuçları burada görünecek. Hiçbir öğe otomatik seçilmez.", "Scan results will appear here. Nothing is selected automatically."); UpdateSelection();
    }
    private void Navigate(bool former, bool history)
    {
        if (busy) return;
        Workspace.Visibility = history ? Visibility.Collapsed : Visibility.Visible; HistoryPanel.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        InstalledHeader.Visibility = InstalledPanel.Visibility = former ? Visibility.Collapsed : Visibility.Visible; FormerPanel.Visibility = former ? Visibility.Visible : Visibility.Collapsed;
        SelectedPanel.Visibility = former ? Visibility.Collapsed : Visibility.Visible;
        AppCount.Visibility = former || history ? Visibility.Collapsed : Visibility.Visible;
        PageTitle.Text = history ? L.T("İşlem geçmişi", "History") : former ? L.T("Önceden kaldırılanlar", "Previously removed") : L.T("Uygulamalar", "Apps");
        var inactive = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#272E3A")!;
        var active = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#25443D")!;
        AppsNav.Background = !former && !history ? active : inactive;
        FormerNav.Background = former ? active : inactive; HistoryNav.Background = history ? active : inactive;
        PageSubtitle.Text = history ? L.T("Her adımın sonucu kayıt altında.", "The result of every step is recorded.") : former ? L.T("Ad yazmadan Kalıntıları bul'a basarsan kaldırılmış programlardan kalan klasörler aranır.", "Press Find leftovers without a name to search for folders left behind by removed programs.") : L.T("Bir uygulama seç. Kaldır, kalıntılarını incele, sonucu doğrula.", "Pick an app. Uninstall it, review its leftovers, verify the result.");
        if (history) { try { RefreshHistory(); } catch (Exception e) { Error(e); } } else ClearScan();
    }
    private void Apps_Click(object sender, RoutedEventArgs e) => Navigate(false, false);
    private void Former_Click(object sender, RoutedEventArgs e) => Navigate(true, false);
    private void History_Click(object sender, RoutedEventArgs e) => Navigate(false, true);
    private void CopyHistory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HistoryEntry entry }) return;
        try { Clipboard.SetText(entry.CopyText); Status.Text = L.T("Kopyalandı: ", "Copied: ") + entry.CopyText; }
        catch (System.Runtime.InteropServices.ExternalException) { Status.Text = L.T("Panoya erişilemedi. Tekrar deneyebilirsin.", "The clipboard is not available. Try again."); }
        e.Handled = true;
    }
    private void RefreshHistory()
    {
        var entries = Journal.Read();
        HistoryGrid.ItemsSource = entries;
        HistorySummary.Text = entries.Count == 0 ? L.T("Yeni işlemler burada görünür.", "New activity appears here.") : L.T($"Son {entries.Count} kayıt gösteriliyor · Bu bilgisayarda saklanır", $"Showing the last {entries.Count} entries · Stored on this computer");
        EmptyHistory.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearHistoryButton.IsEnabled = System.IO.File.Exists(Journal.FilePath);
    }
    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (AppDialog.Show(this, L.T("UClear'ın tüm işlem geçmişi kalıcı olarak temizlenecek. Bu işlem önceki kaldırma ve temizleme işlemlerini geri almaz.\n\nDevam edilsin mi?", "UClear's entire activity history will be cleared permanently. This does not undo any earlier uninstall or cleanup.\n\nContinue?"), L.T("İşlem geçmişini temizle", "Clear history"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try
        {
            Journal.Clear(); RefreshHistory(); Status.Text = L.T("İşlem geçmişi temizlendi.", "History cleared.");
        }
        catch (Exception error) { Error(error); }
    }
    private static Target From(InstalledApp app) => new(app.Name, app.Location, app.RegistryPath, app.View, RegistryReferences.IconExecutable(app.DisplayIcon));
    private async void Scan_Click(object sender, RoutedEventArgs e) { if (AppList.SelectedItem is InstalledApp app) await Scan(From(app)); }
    private async void FormerScan_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return; string name = FormerName.Text.Trim(), path = FormerPath.Text.Trim().Trim('"');
        if (name.Length == 0 && path.Length == 0) { await FindLeftovers(); return; }
        if (name.Length == 0) name = Path.GetFileName(path.TrimEnd('\\'));
        if (!Safety.ValidName(name)) { AppDialog.Show(this, L.T("En az 3 harf/rakam içeren belirli bir uygulama adı gir.", "Enter a specific application name with at least 3 letters/digits."), L.T("Uygulama adı gerekli", "Application name required")); return; }
        if (path.Length > 0 && !Path.IsPathFullyQualified(path)) { AppDialog.Show(this, L.T("Klasörün tam yolunu gir veya konumu boş bırak.", "Enter the folder's full path or leave the location empty."), L.T("Konumu kontrol et", "Check the location")); return; }
        await Scan(new(name, path));
    }
    private readonly ObservableCollection<LeftoverCandidate> leftovers = [];
    private async void FindLeftovers_Click(object sender, RoutedEventArgs e) => await FindLeftovers();
    // Lists folders that no installed program, service, startup entry or running process accounts for; nothing is selected or deleted here.
    private async Task FindLeftovers()
    {
        if (busy) return;
        leftovers.Clear(); LeftoverList.ItemsSource = leftovers;
        if (LeftoverList.Items.SortDescriptions.Count == 0)
        {
            LeftoverList.Items.SortDescriptions.Add(new(nameof(LeftoverCandidate.Rank), System.ComponentModel.ListSortDirection.Ascending));
            LeftoverList.Items.SortDescriptions.Add(new(nameof(LeftoverCandidate.Bytes), System.ComponentModel.ListSortDirection.Descending));
        }
        void Show() { LeftoverHeader.Text = L.T($"Kalıntı adayları ({leftovers.Count})", $"Leftover candidates ({leftovers.Count})"); NoLeftovers.Visibility = leftovers.Count == 0 ? Visibility.Visible : Visibility.Collapsed; }
        NoLeftovers.Text = L.T("Aranıyor…", "Searching…"); Show();
        SetBusy(true); cancellation = new(); CancelButton.Visibility = Visibility.Visible;
        var feed = new LiveFeed<LeftoverCandidate>(batch => { foreach (var c in batch) leftovers.Add(c); Show(); });
        var notes = new List<string>();
        try
        {
            // Progress must be created on the UI thread so its callbacks are marshalled back here.
            var live = LiveProgress(); var token = cancellation.Token;
            try { await Task.Run(() => LeftoverFinder.Find(token, feed.Post, notes, live)); }
            finally { feed.Dispose(); feed.Drain(); }
            Status.Text = leftovers.Count == 0 ? L.T("Kaldırılmış programlardan kalan klasör bulunamadı.", "No folders left behind by removed programs were found.") : L.T($"{leftovers.Count} kalıntı adayı bulundu. İşaretleyip Seçilenleri kaldır diyebilir veya çift tıklayıp tek tek inceleyebilirsin; hiçbir şey otomatik silinmez.", $"{leftovers.Count} leftover candidates found. Tick them and choose Remove selected, or double-click one to review it; nothing is deleted automatically.");
            Log("Kalıntı araması", "Tarama", L.T($"{leftovers.Count} aday, {notes.Count} uyarı", $"{leftovers.Count} candidates, {notes.Count} warnings"));
        }
        catch (OperationCanceledException) { Status.Text = L.T($"Arama durduruldu; o ana kadar bulunan {leftovers.Count} aday listede.", $"Search stopped; the {leftovers.Count} candidates found so far are listed."); }
        catch (Exception error) { Error(error); }
        finally { cancellation.Dispose(); cancellation = null; CancelButton.Visibility = Visibility.Collapsed; SetBusy(false); NoLeftovers.Text = L.T("Kalıntı adayı bulunamadı.", "No leftover candidates found."); Show(); }
    }
    // A click only picks the candidate; the detailed scan (other folders, Registry, services, safety checks) runs on request.
    private void Leftover_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (LeftoverList.SelectedItem is not LeftoverCandidate candidate) return;
        FormerName.Text = candidate.Name; FormerPath.Text = candidate.Path;
        Status.Text = L.T($"{candidate.Name}: {candidate.Reason} Tüm izlerini bulmak için çift tıkla veya Kalıntıları bul'a bas.", $"{candidate.Name}: {candidate.Reason} Double-click or press Find leftovers to find all of its traces.");
    }
    private async void Leftover_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: LeftoverCandidate candidate }) return;
        if (busy) { Status.Text = L.T("Önce devam eden işlem bitsin.", "Wait for the current operation to finish first."); return; }
        FormerName.Text = candidate.Name; FormerPath.Text = candidate.Path;
        await Scan(new(candidate.Name, candidate.Path));
    }
    private async Task<bool> Scan(Target target)
    {
        if (busy) return false; ClearScan(); activeTarget = target; SetBusy(true); cancellation = new(); CancelButton.Visibility = Visibility.Visible;
        // Findings appear while the scan runs so the list can be browsed; deletion waits for the final, shared-location-checked list.
        FindingsGrid.IsEnabled = true;
        Status.Text = L.T($"{target.Name} için uygulama klasörleri ve Registry taranıyor…", $"Scanning application folders and the Registry for {target.Name}…");
        var feed = new LiveFeed<Finding>(batch =>
        {
            foreach (var f in batch) findings.Add(f);
            EmptyState.Visibility = findings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ResultsTitle.Text = L.T($"{target.Name} · {findings.Count} öğe bulundu", $"{target.Name} · {findings.Count} items found"); ResultsSubtitle.Text = L.T("Tarama sürüyor; bulunanlar geldikçe listeye ekleniyor.", "Scan in progress; findings are added to the list as they appear.");
        });
        try
        {
            var live = LiveProgress();
            ScanResult result;
            try { result = await Task.Run(() => new Scanner().Scan(target, cancellation.Token, live, feed.Post)); }
            finally { feed.Dispose(); }
            target = result.ResolvedTarget ?? target;
            activeTarget = target;
            var currentApps = await Task.Run(Inventory.Read);
            var protectedFindings = Protect(result.Findings, target, currentApps);
            lastScan = result with { Findings = protectedFindings }; findings = new(protectedFindings);
            foreach (var f in findings) f.PropertyChanged += (_, _) => UpdateSelection();
            BindFindings(); EmptyState.Visibility = findings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyTitle.Text = L.T("Eşleşen kalıntı bulunamadı.", "No matching leftovers found."); EmptyDescription.Text = L.T("Bu sonuç yalnızca incelenebilen konumları kapsar.", "This result only covers the locations that could be inspected.");
            ResultsTitle.Text = L.T($"{target.Name} · {findings.Count} aday öğe", $"{target.Name} · {findings.Count} candidate items");
            ResultsSubtitle.Text = findings.Count == 0 ? "" : L.T("Silmek istediklerini işaretle. Nedenini görmek için satıra çift tıkla.", "Tick what you want to delete. Double-click a row to see why it was found.");
            Status.Text = findings.Count == 0 ? L.T("Kalıntı bulunamadı.", "No leftovers found.") : L.T("Tarama tamamlandı.", "Scan complete.");
            if (!RegistryReferences.Directories(target).Any() && string.IsNullOrWhiteSpace(target.ExecutablePath))
                Status.Text = L.T("Kurulum klasörü bilinmiyor; bazı izler bulunamamış olabilir. Biliyorsan Önceden kaldırılanlar ekranında eski klasörü ekle.", "The install folder is unknown; some traces may have been missed. If you know it, add the old folder on the Previously removed page.");
            Log(target.Name, "Tarama", L.T($"{findings.Count} aday, {result.Warnings.Count} uyarı", $"{findings.Count} candidates, {result.Warnings.Count} warnings"));
            return true;
        }
        // A partial list has not passed the shared-location check, so it is not left actionable.
        catch (OperationCanceledException) { ClearScan(); Status.Text = L.T("Tarama iptal edildi. Hiçbir öğe silinmedi.", "Scan cancelled. Nothing was deleted."); }
        catch (Exception e) { ClearScan(); Error(e); }
        finally { cancellation.Dispose(); cancellation = null; CancelButton.Visibility = Visibility.Collapsed; SetBusy(false); }
        return false;
    }
    // Anything overlapping another registered program is kept and shown as review-only.
    private static List<Finding> Protect(List<Finding> found, Target target, List<InstalledApp> apps) => found.Select(f =>
        Cleaner.Shared(f, target, apps) ? new Finding { Kind = f.Kind, Path = f.Path, View = f.View, ValueName = f.ValueName, ExpectedValue = f.ExpectedValue, ExpectedValueKind = f.ExpectedValueKind, Bytes = f.Bytes, Reason = L.T("Başka bir kayıtlı uygulamanın konumuyla örtüşüyor. Paylaşılan konum korunuyor. ", "Overlaps the location of another registered application. The shared location is kept. ") + f.Reason, Confidence = "Paylaşılan", CanDelete = false } : f).ToList();
    private async void RemoveLeftovers_Click(object sender, RoutedEventArgs e) => await RemoveLeftovers();
    // One confirmation, then for each selected candidate: find every trace, delete what is safe, check it is gone, move on — no stops in between.
    private async Task RemoveLeftovers()
    {
        if (busy) return;
        var chosen = leftovers.Where(c => c.Selected && c.CanRemove).ToList();
        if (chosen.Count == 0) { Status.Text = L.T("Önce soldan kaldırılacak adayları işaretle.", "First tick the candidates to remove on the left."); return; }
        string preview = string.Join("\n", chosen.Take(40).Select(c => $"• {c.Name} — {c.Path}")) + (chosen.Count > 40 ? L.T($"\n… ve {chosen.Count - 40} aday daha", $"\n… and {chosen.Count - 40} more candidates") : "");
        if (AppDialog.Show(this, L.T($"{chosen.Count} adayın kalıntıları kaldırılacak: klasörleri, Registry kayıtları ve bulunan diğer izleri.\nBaşka bir programla ortak olan veya emin olunamayan öğeler silinmez; sağdaki listede gösterilir.\nSilme kalıcıdır, yedek alınmaz.\n\n{preview}", $"The leftovers of {chosen.Count} candidates will be removed: their folders, Registry entries and other traces found.\nItems shared with another program or not certain are not deleted; they are shown in the list on the right.\nDeletion is permanent; no backup is made.\n\n{preview}"), L.T("Seçilen kalıntıları kaldır", "Remove selected leftovers"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        ClearScan(); SetBusy(true); cancellation = new(); CancelButton.Visibility = Visibility.Visible;
        FindingsGrid.IsEnabled = true; EmptyState.Visibility = Visibility.Collapsed;
        int removed = 0, failed = 0, skipped = 0, done = 0;
        // Candidates with the same name (Program Files\Adobe, AppData\Adobe…) are one program: one scan already finds all of them.
        var programs = chosen.GroupBy(c => Safety.Normalize(c.Name)).Select(g => g.OrderBy(c => c.Rank).ToList()).ToList();
        var handled = new HashSet<(string, string, string, string?)>();
        void Summary() { ResultsTitle.Text = L.T($"Kalıntı temizliği · {done}/{programs.Count} program", $"Leftover cleanup · {done}/{programs.Count} programs"); ResultsSubtitle.Text = L.T($"{removed} öğe silindi · {skipped} öğe atlandı (inceleme gerekli) · {failed} öğe silinemedi", $"{removed} items deleted · {skipped} items skipped (need review) · {failed} items not deleted"); }
        try
        {
            var live = LiveProgress(); var token = cancellation.Token;
            // All selected programs are scanned in one pass (the Registry walk is shared), then cleaned one by one.
            var targets = programs.Select(g => new Target(g[0].Name, g[0].Path) { ReferenceDirectories = g.Skip(1).Select(c => c.Path).ToArray() }).ToList();
            Status.Text = L.T($"{programs.Count} programın izleri aranıyor…", $"Searching for traces of {programs.Count} programs…");
            var results = await Task.Run(() => new Scanner().ScanMany(targets, token, live));
            var apps = await Task.Run(Inventory.Read);
            for (int index = 0; index < programs.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var group = programs[index]; var candidate = group[0];
                var result = results[index];
                var target = result.ResolvedTarget ?? targets[index];
                var found = Protect(result.Findings, target, apps).Where(f => handled.Add((f.Kind, f.Path.ToUpperInvariant(), f.View, f.ValueName))).ToList();
                var eligible = CleanupPolicy.Eligible(found);
                foreach (var item in found.Where(f => !eligible.Contains(f))) { item.Result = "Atlandı · inceleme gerekli"; skipped++; findings.Add(item); }
                Status.Text = L.T($"{done + 1}/{programs.Count} · {candidate.Name}: {eligible.Count} öğe siliniyor…", $"{done + 1}/{programs.Count} · {candidate.Name}: deleting {eligible.Count} items…");
                foreach (var item in eligible)
                {
                    findings.Add(item);
                    try
                    {
                        var current = target;
                        await Task.Run(() => Cleaner.Delete(item, current));
                        item.Result = await Task.Run(() => CleanupVerification.Check(item));
                        if (item.Result == "Silindi · doğrulandı") removed++; else failed++;
                        Log(target.Name, "Silindi", item.DisplayPath + " [" + item.View + "]", item.DisplayPath);
                    }
                    catch (Exception error) { failed++; item.Result = "Silinemedi: " + error.Message; Log(target.Name, "Silinemedi", item.DisplayPath + " — " + error.Message, item.DisplayPath); }
                }
                done++; Summary();
                foreach (var gone in group.Where(c => !Directory.Exists(c.Path))) leftovers.Remove(gone);
            }
            Status.Text = L.T($"Bitti: {programs.Count} programdan {removed} öğe silindi", $"Done: {removed} items deleted from {programs.Count} programs") + (skipped > 0 ? L.T($", {skipped} öğe emin olunamadığı için bırakıldı", $", {skipped} items kept because they were not certain") : "") + (failed > 0 ? L.T($", {failed} öğe silinemedi (kullanımda olabilir)", $", {failed} items could not be deleted (may be in use)") : "") + ".";
        }
        catch (OperationCanceledException) { Status.Text = L.T($"Durduruldu: {done} program işlendi, {removed} öğe silindi. Kalanlara dokunulmadı.", $"Stopped: {done} programs processed, {removed} items deleted. The rest were left untouched."); }
        catch (Exception error) { Error(error); }
        finally
        {
            cancellation.Dispose(); cancellation = null; CancelButton.Visibility = Visibility.Collapsed; SetBusy(false);
            Summary(); BindFindings(); EmptyState.Visibility = findings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LeftoverHeader.Text = L.T($"Kalıntı adayları ({leftovers.Count})", $"Leftover candidates ({leftovers.Count})");
        }
    }
    private static readonly System.ComponentModel.SortDescription[] FindingsSort = [new(nameof(Finding.Rank), System.ComponentModel.ListSortDirection.Ascending), new(nameof(Finding.Kind), System.ComponentModel.ListSortDirection.Ascending), new(nameof(Finding.Path), System.ComponentModel.ListSortDirection.Ascending)];
    private void BindFindings() => GridSort.Bind(FindingsGrid, findings, FindingsSort);
    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (busy || AppList.SelectedItem is not InstalledApp app) return;
        if (app.IsStore) { await UninstallStore(app); return; }
        ProcessStartInfo start;
        try { start = Inventory.Uninstaller(app.UninstallCommand); } catch (Exception error) { Error(error); return; }
        string argumentsLine = string.IsNullOrWhiteSpace(start.Arguments) ? "" : L.T($"\nParametreler: {start.Arguments}", $"\nArguments: {start.Arguments}");
        if (AppDialog.Show(this, L.T($"{app.Name} kaldırma aracı çalıştırılacak.\n\nDosya: {start.FileName}{argumentsLine}\n\nDevam edilsin mi?", $"The uninstaller of {app.Name} will be run.\n\nFile: {start.FileName}{argumentsLine}\n\nContinue?"), L.T("Kaldırıcıyı çalıştır", "Run uninstaller"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ClearScan();
        bool elevatedRetry = false;
        bool isMsi = Path.GetFileName(start.FileName).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase);
        while (true)
        {
            SetBusy(true); Status.Text = L.T("Kaldırıcı çalışıyor. Açılan kaldırma penceresini tamamla.", "The uninstaller is running. Complete the uninstall window that opened.");
            int exitCode; bool registered;
            try
            {
                using var process = Process.Start(start) ?? throw new InvalidOperationException(L.T("Kaldırıcı başlatılamadı.", "The uninstaller could not be started."));
                await process.WaitForExitAsync(); exitCode = process.ExitCode;
                registered = Cleaner.RegistryExists(app.RegistryPath, app.View);
                Log(app.Name, "Kaldırıcı", L.T($"Dosya: {start.FileName}. Çıkış kodu: {exitCode}. Kaldırma kaydı mevcut: {registered}. Yönetici tekrarı: {elevatedRetry}.", $"File: {start.FileName}. Exit code: {exitCode}. Uninstall entry present: {registered}. Admin retry: {elevatedRetry}."));
            }
            catch (Exception error) { Error(error); return; }
            finally { SetBusy(false); }
            if (UninstallOutcome.NeedsRestart(isMsi, exitCode))
            {
                Status.Text = L.T("Windows Installer yeniden başlatma gerektiriyor. Yeniden başlatmadan sonra kalıntıları tarayabilirsin.", "Windows Installer requires a restart. You can scan for leftovers after restarting.");
                AppDialog.Show(this, Status.Text, L.T("Kaldırma sonucu", "Uninstall result")); return;
            }
            if (exitCode != 0)
            {
                var issue = new UninstallIssueWindow(app.Name, exitCode, registered, !elevatedRetry && registered, UninstallOutcome.KnownFailure(isMsi, exitCode)) { Owner = this };
                issue.ShowDialog();
                if (issue.Choice == UninstallChoice.RetryAsAdmin)
                {
                    elevatedRetry = true; start.Verb = "runas"; continue;
                }
                if (issue.Choice != UninstallChoice.ConfirmCompleted)
                {
                    Status.Text = L.T("Kaldırma doğrulanmadı. Hiçbir kalıntı silinmedi.", "The uninstall was not confirmed. No leftovers were deleted."); return;
                }
            }
            else if (UninstallOutcome.NeedsConfirmation(exitCode, registered) &&
                AppDialog.Show(this, L.T("Uygulamanın kaldırma kaydı hâlâ mevcut. Kaldırıcı başka bir pencerede devam ediyor veya işlem iptal edilmiş olabilir.\n\nKaldırma başarıyla tamamlandıysa devam et; ardından kalıntılar taranacak.", "The application's uninstall entry still exists. The uninstaller may still be running in another window, or it was cancelled.\n\nContinue if the uninstall completed successfully; leftovers will be scanned next."), L.T("Kaldırma tamamlandı mı?", "Did the uninstall complete?"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            break;
        }
        await RefreshApps();
        var target = From(app);
        if (!await Scan(target)) return;
        target = activeTarget ?? target;
        var eligible = CleanupPolicy.Eligible(findings);
        int excluded = findings.Count - eligible.Count;
        if (eligible.Count == 0)
        {
            AppDialog.Show(this, findings.Count == 0
                ? L.T($"{app.Name} için taranabilen konumlarda kalıntı bulunamadı.\nTarama uyarısı: {lastScan?.Warnings.Count ?? 0}.", $"No leftovers of {app.Name} were found in the locations that could be scanned.\nScan warnings: {lastScan?.Warnings.Count ?? 0}.")
                : L.T($"{findings.Count} aday bulundu; hiçbiri otomatik temizlemeye uygun değil. Ayrıntıları listeden inceleyebilirsin.", $"{findings.Count} candidates found; none is eligible for automatic cleanup. You can review them in the list."), L.T("Kalıntı taraması tamamlandı", "Leftover scan complete"));
            return;
        }
        var cleanupDialog = new ConfirmWindow(app.Name, eligible, true, excluded, lastScan?.Warnings.Count ?? 0) { Owner = this };
        if (cleanupDialog.ShowDialog() == true)
            await ExecuteCleanup(target, cleanupDialog.SelectedFindings);
        else Status.Text = L.T("Kalıntılar temizlenmedi. Bulunan öğeleri listeden inceleyebilirsin.", "Leftovers were not cleaned. You can review the found items in the list.");
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => cancellation?.Cancel();
    private bool changingSelection;
    private void AllFindings_Click(object sender, RoutedEventArgs e)
    {
        changingSelection = true;
        foreach (var item in findings.Where(f => f.CanDelete && f.Result != "Silindi · doğrulandı")) item.Selected = AllFindings.IsChecked == true;
        changingSelection = false; UpdateSelection();
    }
    private void UpdateSelection()
    {
        if (SelectionLabel == null || changingSelection) return; var chosen = findings.Where(f => f.Selected && f.CanDelete && f.Result != "Silindi · doğrulandı").ToList();
        var selectable = findings.Where(f => f.CanDelete && f.Result != "Silindi · doğrulandı").ToList();
        if (AllFindings != null) { AllFindings.IsEnabled = !busy && selectable.Count > 0; AllFindings.IsChecked = chosen.Count == 0 ? false : chosen.Count == selectable.Count ? true : null; }
        SelectionLabel.Text = L.T($"{chosen.Count} öğe seçili · {Finding.FormatBytes(chosen.Sum(f => f.Bytes))} dosya verisi", $"{chosen.Count} items selected · {Finding.FormatBytes(chosen.Sum(f => f.Bytes))} of file data"); DeleteButton.IsEnabled = !busy && chosen.Count > 0;
    }
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        // The bulk leftover list mixes several programs; each item's safety checks need its own program, so review goes through that program's scan.
        if (activeTarget == null) { Status.Text = L.T("Bu listedeki atlanan öğeleri tek tek silmek için soldaki adaya çift tıkla.", "To delete skipped items one by one, double-click the candidate on the left."); return; }
        var chosen = findings.Where(f => f.Selected && f.CanDelete && f.Result != "Silindi · doğrulandı").ToList(); if (chosen.Count == 0) return;
        var cleanupDialog = new ConfirmWindow(activeTarget.Name, chosen) { Owner = this };
        if (cleanupDialog.ShowDialog() != true) return;
        await ExecuteCleanup(activeTarget, cleanupDialog.SelectedFindings);
    }
    private async Task ExecuteCleanup(Target target, List<Finding> chosen)
    {
        SetBusy(true); int ok = 0, failed = 0;
        try
        {
            Journal.Add(target.Name, "Silme başladı", string.Join(" | ", chosen.Select(f => f.DisplayPath)));
            foreach (var item in chosen)
            {
                Status.Text = L.T("Siliniyor ve varlığı denetleniyor: ", "Deleting and checking: ") + item.DisplayPath;
                try { await Task.Run(() => Cleaner.Delete(item, target)); item.Result = "Silindi · doğrulandı"; item.Selected = false; ok++; Log(target.Name, "Silindi", item.DisplayPath + " [" + item.View + "]", item.DisplayPath); }
                catch (Exception error) { failed++; item.Result = "Silinemedi: " + error.Message; Log(target.Name, "Silinemedi", item.DisplayPath + " — " + error.Message, item.DisplayPath); }
            }
            Status.Text = failed == 0 ? L.T($"{ok} öğe silindi.", $"{ok} items deleted.") : L.T($"{ok} öğe silindi · {failed} öğe silinemedi (kullanımda olabilir; programı kapatıp tekrar dene).", $"{ok} items deleted · {failed} items could not be deleted (they may be in use; close the program and try again).");
            ResultsSubtitle.Text = L.T("Silinen öğeler doğrulanıyor…", "Verifying deleted items…");
        }
        catch (Exception error) { Error(error); }
        finally { SetBusy(false); }
        await VerifyCleanup(target, chosen);
    }
    // Each deleted item is checked directly; a full rescan only made the user wait again. "Yalnızca tara" is there for a fresh look.
    private async Task VerifyCleanup(Target target, List<Finding> chosen)
    {
        SetBusy(true);
        try
        {
            foreach (var item in chosen)
            {
                item.Result = await Task.Run(() => CleanupVerification.Check(item));
                item.Selected = false;
                Log(target.Name, "Doğrulama", item.Result, item.DisplayPath);
            }
            int removed = chosen.Count(f => f.Result == "Silindi · doğrulandı");
            int remaining = chosen.Count(f => f.Result == "Hâlâ mevcut");
            int unknown = chosen.Count - removed - remaining;
            string summary = L.T($"{removed} silindi · {remaining} hâlâ mevcut · {unknown} doğrulanamadı", $"{removed} deleted · {remaining} still present · {unknown} could not be verified");
            ResultsTitle.Text = target.Name + L.T(" · Temizlik tamamlandı", " · Cleanup complete");
            ResultsSubtitle.Text = summary;
            Status.Text = remaining + unknown == 0 ? L.T($"Temizlik tamamlandı: {removed} öğe silindi ve yok oldukları doğrulandı.", $"Cleanup complete: {removed} items deleted and verified as gone.") : L.T($"{summary}. Kalanlar kullanımda olabilir; programı kapatıp tekrar dene.", $"{summary}. The remaining items may be in use; close the program and try again.");
            if (lastScan != null) lastScan = lastScan with { Findings = findings.ToList() };
            foreach (var gone in leftovers.Where(c => !Directory.Exists(c.Path)).ToList()) leftovers.Remove(gone);
            Log(target.Name, "Doğrulama özeti", summary);
        }
        catch (Exception error) { Error(error); }
        finally { SetBusy(false); }
    }
    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (FindingsGrid.SelectedItem is not Finding item) { AppDialog.Show(this, L.T("İncelemek için bir sonuç satırını seç.", "Select a result row to inspect."), L.T("Öğe incelemesi", "Item details")); return; }
        var window = new DialogWindow(L.T("Öğe incelemesi", "Item details"), 700, 420) { Owner = this };
        var panel = new DockPanel { Margin = new Thickness(22) }; var copy = new Button { Content = L.T("Konumu kopyala", "Copy location"), Margin = new Thickness(0, 14, 0, 0) }; copy.Click += (_, _) => Clipboard.SetText(item.Path); DockPanel.SetDock(copy, Dock.Bottom); panel.Children.Add(copy);
        string result = item.Result == "" ? "-" : L.V(item.Result);
        string details = L.T($"Tür: {L.V(item.Kind)}\nKonum: {item.DisplayPath}\nBoyut: {item.SizeText}\n\nNeden bulundu: {item.Reason}\nEşleşme: {L.V(item.Confidence)}\n\nSonuç: {result}\nRegistry görünümü: {item.View}",
            $"Type: {L.V(item.Kind)}\nLocation: {item.DisplayPath}\nSize: {item.SizeText}\n\nWhy it was found: {item.Reason}\nMatch: {L.V(item.Confidence)}\n\nResult: {result}\nRegistry view: {item.View}");
        panel.Children.Add(new TextBox { Text = details, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); window.Body.Content = panel; window.ShowDialog();
    }
    private async void Locks_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (FindingsGrid.SelectedItem is not Finding { Kind: "Klasör" } item)
        {
            AppDialog.Show(this, L.T("Kalıntı listesinden bir klasör satırını seç. O klasördeki dosyaları kullanan süreçler sorgulanacak.", "Select a folder row in the leftover list. Processes using files in that folder will be queried."), L.T("Klasör seç", "Select a folder")); return;
        }
        SetBusy(true); cancellation = new(); CancelButton.Visibility = Visibility.Visible;
        Status.Text = L.T("Dosyaları kullanan süreçler sorgulanıyor…", "Querying processes that use the files…");
        LockReport? report = null;
        var live = LiveProgress();
        try { report = await Task.Run(() => FileLocks.Inspect(item.Path, cancellation.Token, progress: live)); Status.Text = L.T($"{report.FilesChecked} dosya sorgulandı; {report.Owners.Count} süreç bulundu.", $"{report.FilesChecked} files queried; {report.Owners.Count} processes found."); }
        catch (OperationCanceledException) { Status.Text = L.T("Dosya kullanım sorgusu durduruldu.", "File usage query stopped."); }
        catch (Exception error) { Error(error); }
        finally { cancellation.Dispose(); cancellation = null; CancelButton.Visibility = Visibility.Collapsed; SetBusy(false); }
        if (report != null) new LocksWindow(item.Path, report) { Owner = this }.ShowDialog();
    }
    private void Tools_Click(object sender, RoutedEventArgs e) { if (!busy) new ToolsWindow { Owner = this }.ShowDialog(); }
    private void FolderLocks_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var picker = new OpenFolderDialog { Title = L.T("Silinemeyen veya kullanımda olan klasörü seç", "Select a folder that cannot be deleted or is in use"), Multiselect = false };
        if (picker.ShowDialog(this) == true) new LocksWindow(picker.FolderName) { Owner = this }.ShowDialog();
    }
    private void Report_Click(object sender, RoutedEventArgs e)
    {
        if (lastScan == null || activeTarget == null) { AppDialog.Show(this, L.T("Önce bir tarama yap.", "Run a scan first."), L.T("Rapor", "Report")); return; }
        var dialog = new SaveFileDialog { Filter = L.T("JSON raporu|*.json", "JSON report|*.json"), FileName = "UClear-rapor.json" };
        if (dialog.ShowDialog(this) == true)
        { try { File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(new { Application = activeTarget, Time = DateTimeOffset.Now, lastScan.LocationsChecked, lastScan.Warnings, Findings = findings }, new JsonSerializerOptions { WriteIndented = true })); Status.Text = L.T("Rapor kaydedildi: ", "Report saved: ") + dialog.FileName; } catch (Exception error) { Error(error); } }
    }
    private void Log(string app, string action, string detail, string? itemPath = null) { try { Journal.Add(app, action, detail, itemPath); } catch (Exception e) { AppDialog.Show(this, L.T("İşlem günlüğü yazılamadı: ", "The activity log could not be written: ") + e.Message, L.T("Günlük hatası", "Log error")); } }
    private void Error(Exception e) { Status.Text = e.Message; AppDialog.Show(this, e.Message, L.T("İşlem tamamlanamadı", "Operation could not be completed"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    private async Task LoadIcons(InstalledApp[] batch)
    {
        foreach (var app in batch)
        {
            if (!apps.Contains(app)) break;
            try { var icon = await Task.Run(() => AppIcons.Read(app)); app.SetIcon(icon); }
            catch (Exception e) when (e is IOException or ArgumentException or System.Runtime.InteropServices.ExternalException) { app.SetIcon(AppIcons.Fallback); }
        }
    }
}




