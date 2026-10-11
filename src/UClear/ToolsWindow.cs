using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace UClear;
public sealed class ToolsWindow : DialogWindow
{
    readonly DockPanel layout = new();
    readonly ContentControl page = new();
    readonly WrapPanel navigation = new();
    CancellationTokenSource? cancellation;
    bool busy;
    bool hostsDirty;
    public ToolsWindow() : base(L.T("UClear · Araçlar", "UClear · Tools"), 1050, 720)
    {
        foreach (var item in new[] { (L.T("Hosts düzenleyici", "Hosts editor"), "hosts"), (L.T("Geçici dosyalar", "Temporary files"), "temp"), (L.T("Büyük dosya / klasör", "Large files / folders"), "large"), (L.T("Boş klasörler", "Empty folders"), "empty"), (L.T("Başlangıç", "Startup"), "startup"), (L.T("Geçersiz başvurular", "Invalid references"), "invalid") })
        {
            var button = new Button { Content = item.Item1 };
            button.Click += (_, _) =>
            {
                if (busy || !DiscardHosts()) return;
                if (item.Item2 == "invalid") { new InvalidReferencesWindow { Owner = this }.ShowDialog(); return; }
                if (item.Item2 == "hosts") Hosts(); else if (item.Item2 == "startup") Startup(); else Disk(item.Item2);
            };
            navigation.Children.Add(button);
        }
        navigation.Margin = new Thickness(0, 0, 0, 15);
        DockPanel.SetDock(navigation, Dock.Top); layout.Children.Add(navigation); layout.Children.Add(page); Body.Content = layout;
        Loaded += (_, _) => Hosts();
        Closing += (_, e) => { if (busy) { cancellation?.Cancel(); e.Cancel = true; } else if (!DiscardHosts()) e.Cancel = true; };
    }
    bool DiscardHosts()
    {
        if (hostsDirty && AppDialog.Show(this, L.T("Kaydedilmemiş hosts değişiklikleri bırakılacak. Devam edilsin mi?", "Unsaved hosts changes will be discarded. Continue?"), L.T("Değişiklikleri bırak", "Discard changes"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return false;
        hostsDirty = false; return true;
    }
    void Error(Exception error) => AppDialog.Show(this, error.Message, L.T("İşlem tamamlanamadı", "Operation could not be completed"));
    static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
    static Button Action(Panel panel, string title, RoutedEventHandler callback)
    {
        var button = new Button { Content = title }; button.Click += callback; panel.Children.Add(button); return button;
    }
    static void Column(DataGrid grid, string title, string property, double width = 1)
    {
        grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(property) { Converter = new DisplayText() }, SortMemberPath = property, Width = width == 1 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width) });
    }
    void Hosts()
    {
        var panel = new DockPanel(); var header = new StackPanel(); var bar = new WrapPanel();
        header.Children.Add(Ui.Info(L.T("Web sitelerini engelle veya yönlendir. Değişiklikler Kaydet'e basınca uygulanır.", "Block or redirect websites. Changes are applied when you press Save."),
            L.T($"Satır biçimi: IP adresi ve ardından alan adı (ör. 0.0.0.0 ornek.com). # ile başlayan satırlar devre dışıdır. Kaydetmeden önce adresler doğrulanır; dosyayı başka bir program değiştirdiyse üzerine yazılmaz. Yedek oluşturulmaz.\nDosya: {HostsTools.Path}", $"Line format: an IP address followed by a host name (e.g. 0.0.0.0 example.com). Lines starting with # are disabled. Addresses are validated before saving; if another program changed the file, it is not overwritten. No backup is made.\nFile: {HostsTools.Path}")));
        var editor = new TextBox { AcceptsReturn = true, AcceptsTab = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Margin = new Thickness(0, 12, 0, 0) };
        string original = ""; bool loaded = false;
        void Reload() { original = File.ReadAllText(HostsTools.Path); editor.Text = original; hostsDirty = false; loaded = true; }
        editor.TextChanged += (_, _) => hostsDirty = loaded && editor.Text != original;
        var search = new TextBox { Width = 180, ToolTip = L.T("Alan adı veya IP ara", "Search host name or IP") }; bar.Children.Add(search);
        Action(bar, L.T("Bul", "Find"), (_, _) => { if (search.Text.Length == 0) return; int start = editor.SelectionStart + editor.SelectionLength; int at = editor.Text.IndexOf(search.Text, Math.Min(start, editor.Text.Length), StringComparison.OrdinalIgnoreCase); if (at < 0) at = editor.Text.IndexOf(search.Text, StringComparison.OrdinalIgnoreCase); if (at >= 0) { editor.Focus(); editor.Select(at, search.Text.Length); editor.ScrollToLine(editor.GetLineIndexFromCharacterIndex(at)); } });
        Action(bar, L.T("Satırı aç / kapat", "Toggle line on / off"), (_, _) => { int line = editor.GetLineIndexFromCharacterIndex(editor.CaretIndex); int start = editor.GetCharacterIndexFromLineIndex(line); string value = editor.GetLineText(line); string replacement = value.TrimStart().StartsWith('#') ? value.Remove(value.IndexOf('#'), 1) : "# " + value; editor.Select(start, value.Length); editor.SelectedText = replacement; });
        Action(bar, L.T("Varsayılana döndür", "Reset to default"), (_, _) => { if (AppDialog.Show(this, L.T("Mevcut özel kayıtlar editörden kaldırılacak. Sonucu inceleyip Kaydet ile uygulayabilirsin.", "Your custom entries will be removed from the editor. Review the result and apply it with Save."), L.T("Hosts sıfırlama", "Reset hosts"), MessageBoxButton.YesNo) == MessageBoxResult.Yes) editor.Text = HostsTools.Default; });
        Action(bar, L.T("Yenile", "Reload"), (_, _) => { if (!DiscardHosts()) return; try { Reload(); } catch (Exception e) { Error(e); } });
        Action(bar, L.T("Kaydet", "Save"), (_, _) =>
        {
            try
            {
                HostsTools.Validate(editor.Text);
                if (AppDialog.Show(this, L.T("Editördeki içerik hosts dosyasına yazılacak. Yedek oluşturulmaz. Devam edilsin mi?", "The editor contents will be written to the hosts file. No backup is made. Continue?"), L.T("Hosts kaydet", "Save hosts"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                HostsTools.Save(HostsTools.Path, original, editor.Text); original = editor.Text; hostsDirty = false;
                Journal.Add("Hosts", "Kaydedildi", L.T("Hosts içeriği güncellendi.", "Hosts content updated."), HostsTools.Path);
                AppDialog.Show(this, L.T("Hosts kaydedildi.", "Hosts saved."), L.T("Tamamlandı", "Done"));
            }
            catch (Exception e) { Error(e); }
        });
        header.Children.Add(bar); DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header); panel.Children.Add(editor); page.Content = panel;
        try { Reload(); } catch (Exception e) { Error(e); }
    }
    void Disk(string mode)
    {
        var panel = new DockPanel(); var header = new StackPanel(); var bar = new WrapPanel();
        var tempRoots = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"), Path.GetTempPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") }.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        header.Children.Add(mode == "temp" ? Ui.Info(L.T("Programların bıraktığı geçici dosyaları temizle. Kullanımdaki dosyalar atlanır.", "Clean temporary files left by programs. Files in use are skipped."), L.T("Taranan klasörler:\n", "Scanned folders:\n") + string.Join("\n", tempRoots) + L.T("\nİstersen yalnızca belirli günden eski dosyaları listeleyebilirsin. Seçilen dosyalar silinmeden önce yolu ve değişiklik zamanı yeniden kontrol edilir. İndirilenler, tarayıcı profilleri ve Windows Update önbelleği kapsam dışıdır.", "\nYou can list only files older than a number of days. Before deletion, each selected file's path and modification time are checked again. Downloads, browser profiles and the Windows Update cache are out of scope."))
            : mode == "large" ? Ui.Info(L.T("Diskte en çok yer kaplayan dosya ve klasörleri bul.", "Find the files and folders that take up the most disk space."), L.T("Klasör boyutları alt klasörleri de içerir; bu yüzden satırlar birbiriyle toplanmaz. Dosyalar seçilerek silinebilir; dolu klasörler için Konumu aç ile içine bakabilirsin.", "Folder sizes include their subfolders, so rows must not be added up. Files can be selected and deleted; for non-empty folders use Open location to look inside."))
            : Ui.Info(L.T("İçi tamamen boş klasörleri bul ve sil.", "Find and delete completely empty folders."), L.T("Yalnızca seçtiğin klasörün altındaki tamamen boş klasörler listelenir. Gizli dosya içeren klasörler boş sayılmaz. Silme anında içine dosya eklenmişse klasör silinmez.", "Only completely empty folders under the folder you choose are listed. Folders with hidden files are not considered empty. If a file was added at deletion time, the folder is not deleted.")));
        var path = new TextBox { IsReadOnly = true, Text = L.T("Önce taranacak klasörü seç", "Choose the folder to scan first") }; if (mode != "temp") header.Children.Add(path);
        string root = "";
        if (mode != "temp") Action(bar, L.T("Klasör seç", "Choose folder"), (_, _) => { var picker = new OpenFolderDialog(); if (picker.ShowDialog(this) == true) path.Text = root = picker.FolderName; });
        var sources = new WrapPanel { Margin = new Thickness(0, 10, 0, 8) };
        var crashes = new CheckBox { Content = L.T("Çökme raporları (CrashDumps)", "Crash reports (CrashDumps)"), ToolTip = L.T("Hata araştırmasında kullanılan döküm dosyaları; isteğe bağlı temizlenir.", "Dump files used for troubleshooting; cleaned only if you choose.") };
        if (mode == "temp") { sources.Children.Add(crashes); header.Children.Add(sources); }
        var age = new ComboBox { Width = 165, ItemsSource = new[] { L.T("Tüm tarihler", "All dates"), L.T("1 günden eski", "Older than 1 day"), L.T("7 günden eski", "Older than 7 days"), L.T("30 günden eski", "Older than 30 days") }, SelectedIndex = 0, Margin = new Thickness(0, 0, 10, 0) };
        if (mode == "temp") bar.Children.Add(age);
        int scannedAge = 0;
        var search = new TextBox { Width = 200, Height = 38, Margin = new Thickness(0, 0, 10, 0), ToolTip = L.T("Sonuçlarda ad veya yol ara", "Search results by name or path") }; bar.Children.Add(search);
        var kind = new ComboBox { Width = 125, ItemsSource = new[] { L.T("Tümü", "All"), L.T("Dosyalar", "Files"), L.T("Klasörler", "Folders") }, SelectedIndex = 0, Margin = new Thickness(0, 0, 10, 0) }; if (mode == "large") bar.Children.Add(kind);
                var grid = new DataGrid { IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow, RowHeight = 42 };
        var selectAll = new CheckBox { ToolTip = L.T("Görünenlerin tümünü seç / bırak", "Select / clear all visible"), VerticalAlignment = VerticalAlignment.Center };
        bool updatingSelection = false;
        var selector = new FrameworkElementFactory(typeof(CheckBox));
        selector.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(DiskEntry.Selected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        selector.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        grid.Columns.Add(new DataGridTemplateColumn { Header = selectAll, CellTemplate = new DataTemplate { VisualTree = selector }, Width = 48 });
        grid.Columns[0].CanUserSort = false;
        Column(grid, L.T("KONUM", "LOCATION"), "Path"); Column(grid, L.T("TÜR", "TYPE"), "Kind", 95);
        if (mode != "empty") { Column(grid, L.T("BOYUT", "SIZE"), "Size", 100); grid.Columns[^1].SortMemberPath = nameof(DiskEntry.Bytes); }
        grid.Columns.Add(new DataGridTextColumn { Header = L.T("DEĞİŞTİRİLME", "MODIFIED"), Binding = new Binding(nameof(DiskEntry.ModifiedLocal)) { StringFormat = "dd.MM.yyyy HH:mm" }, Width = 130 });
        Column(grid, L.T("SONUÇ", "RESULT"), "Result", 190);
        List<DiskEntry> entries = []; string[] scannedRoots = [];
        // During a scan only a preview is listed (largest files first in "large" mode); the full list replaces it at the end.
        ObservableCollection<DiskEntry> view = []; bool scanning = false, userSorted = false; int liveCap = mode == "large" ? 2000 : 20000;
        var subscribed = new HashSet<DiskEntry>(ReferenceEqualityComparer.Instance);
        grid.Sorting += (_, _) => userSorted = true;
        void Subscribe(DiskEntry item) { if (subscribed.Add(item)) item.PropertyChanged += (_, _) => { if (!updatingSelection) UpdateSelectAll(); }; }
        void UpdateSelectAll()
        {
            var visible = grid.Items.Cast<DiskEntry>().ToList();
            updatingSelection = true;
            selectAll.IsChecked = visible.Count == 0 || visible.All(e => !e.Selected) ? false : visible.All(e => e.Selected) ? true : null;
            updatingSelection = false;
        }
        selectAll.Click += (_, _) => { if (updatingSelection) return; bool value = selectAll.IsChecked == true; updatingSelection = true; foreach (var item in grid.Items.Cast<DiskEntry>().ToArray()) item.Selected = value; updatingSelection = false; UpdateSelectAll(); };
        bool Matches(DiskEntry e) => e.Path.Contains(search.Text, StringComparison.OrdinalIgnoreCase) && (kind.SelectedIndex == 0 || (kind.SelectedIndex == 1 ? e.Kind == "Dosya" : e.Kind != "Dosya"));
        void Filter()
        {
            var source = entries.Where(Matches);
            if (scanning) source = mode == "large" ? source.OrderByDescending(e => e.Bytes).Take(liveCap) : source.Take(liveCap);
            view = new(source);
            if (scanning && mode == "large" && !userSorted) GridSort.Bind(grid, view, new SortDescription(nameof(DiskEntry.Bytes), ListSortDirection.Descending)); else GridSort.Bind(grid, view);
            UpdateSelectAll();
        }
        void AddLive(List<DiskEntry> batch)
        {
            foreach (var item in batch) { entries.Add(item); Subscribe(item); }
            foreach (var item in (mode == "large" ? batch.OrderByDescending(e => e.Bytes) : batch.AsEnumerable()).Where(Matches))
            {
                if (view.Count < liveCap) { view.Add(item); continue; }
                if (mode != "large") break;
                var smallest = view.MinBy(e => e.Bytes)!;
                if (item.Bytes <= smallest.Bytes) break;
                view.Remove(smallest); view.Add(item);
            }
            UpdateSelectAll();
        }
        search.TextChanged += (_, _) => Filter(); kind.SelectionChanged += (_, _) => Filter();
        var footer = new StackPanel(); var status = Label(L.T("Başlamak için Tara'ya bas.", "Press Scan to start.")); var actions = new WrapPanel(); var warnings = new TextBox { IsReadOnly = true, Height = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap };
        var more = new Disclosure(L.T("Tarama ayrıntıları", "Scan details"), warnings) { Visibility = Visibility.Collapsed };
        var meter = new ProgressBar { Height = 4, Margin = new Thickness(0, 8, 0, 8), Visibility = Visibility.Collapsed };
        var live = new Progress<ScanProgress>(p => { if (!scanning) return; meter.IsIndeterminate = !p.Total.HasValue; meter.Value = p.Percent; status.Text = L.T($"{p.Stage} · {entries.Count:N0} öğe bulundu", $"{L.V(p.Stage)} · {entries.Count:N0} items found") + (entries.Count > liveCap ? (mode == "large" ? L.T($" · en büyük {liveCap:N0} dosya gösteriliyor", $" · showing the {liveCap:N0} largest files") : L.T($" · ilk {liveCap:N0} gösteriliyor", $" · showing the first {liveCap:N0}")) : "") + "…"; });
        footer.Children.Add(meter); footer.Children.Add(status); footer.Children.Add(more); footer.Children.Add(actions); DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        var cancel = new Button { Content = L.T("Durdur", "Stop"), IsEnabled = false }; cancel.Click += (_, _) => cancellation?.Cancel();
        // While scanning, the list stays usable (scroll, search, sort); only actions wait for the scan.
        async Task Work(Func<Task> operation, bool scan = false)
        {
            busy = true; cancellation = new(); meter.Visibility = Visibility.Visible; meter.IsIndeterminate = true; navigation.IsEnabled = sources.IsEnabled = actions.IsEnabled = false; grid.IsEnabled = scan;
            foreach (UIElement control in bar.Children) control.IsEnabled = control == cancel || (scan && (control == search || control == kind)); cancel.IsEnabled = true;
            try { await operation(); } catch (OperationCanceledException) { status.Text = L.T($"Tarama durduruldu; o ana kadar bulunan {entries.Count:N0} öğe listede.", $"Scan stopped; the {entries.Count:N0} items found so far are listed."); } catch (Exception e) { Error(e); }
            finally { meter.IsIndeterminate = false; cancellation.Dispose(); cancellation = null; busy = false; navigation.IsEnabled = sources.IsEnabled = actions.IsEnabled = grid.IsEnabled = true; foreach (UIElement control in bar.Children) control.IsEnabled = true; cancel.IsEnabled = false; }
        }
        Action(bar, L.T("Tara", "Scan"), async (_, _) =>
        {
            if (mode != "temp" && root.Length == 0) { AppDialog.Show(this, L.T("Önce bir klasör seç.", "Choose a folder first."), L.T("Tarama", "Scan")); return; }
            await Work(async () =>
            {
                status.Text = L.T("Taranıyor…", "Scanning…"); entries = []; subscribed.Clear(); scanning = true; userSorted = false; grid.Items.SortDescriptions.Clear(); Filter(); var notes = new List<string>(); var locations = new List<string>();
                scannedAge = new[] { 0, 1, 7, 30 }[age.SelectedIndex];
                scannedRoots = mode == "temp" ? tempRoots.Concat(crashes.IsChecked == true ? new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps") } : Array.Empty<string>()).ToArray() : [root];
                var reported = new List<DiskEntry>(); var feed = new LiveFeed<DiskEntry>(AddLive);
                try
                {
                    foreach (string location in scannedRoots)
                    {
                        try { var report = await Task.Run(() => DiskTools.Scan(location, mode, cancellation!.Token, scannedAge, live, feed.Post)); reported.AddRange(report.Entries); notes.AddRange(report.Warnings); locations.Add(location + L.T($" — {report.Entries.Count:N0} öğe", $" — {report.Entries.Count:N0} items")); }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { notes.Add(location + ": " + e.Message); }
                    }
                }
                finally
                {
                    // Also runs on cancel, so whatever was found so far stays listed.
                    feed.Dispose(); feed.Drain(); scanning = false;
                    var found = entries.Concat(reported).ToList();
                    entries = await Task.Run(() => found.DistinctBy(e => e.Path, StringComparer.OrdinalIgnoreCase).OrderByDescending(e => e.Bytes).ToList());
                    foreach (var item in entries) Subscribe(item);
                    if (!userSorted) grid.Items.SortDescriptions.Clear();
                    Filter();
                }
                string fileBytes = Finding.FormatBytes(entries.Where(e => e.Kind == "Dosya").Sum(e => e.Bytes));
                status.Text = mode == "empty" ? L.T($"{entries.Count:N0} boş klasör bulundu.", $"{entries.Count:N0} empty folders found.") : L.T($"{entries.Count:N0} öğe bulundu · toplam {fileBytes}", $"{entries.Count:N0} items found · {fileBytes} in total");
                warnings.Text = string.Join("\n", locations.Concat(notes.Count == 0 ? Enumerable.Empty<string>() : notes.Take(500).Prepend(L.T("Okunamayan veya atlanan konumlar:", "Locations that could not be read or were skipped:")))); more.Visibility = Visibility.Visible;
                more.Title = notes.Count == 0 ? L.T("Tarama ayrıntıları", "Scan details") : L.T($"Tarama ayrıntıları · {notes.Count} atlanan konum", $"Scan details · {notes.Count} skipped locations");
            }, scan: true);
        });

        Action(actions, L.T("Konumu aç", "Open location"), (_, _) => { if (grid.SelectedItem is DiskEntry item) { try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + item.Path + "\"") { UseShellExecute = true }); } catch (Exception e) { Error(e); } } });
        Action(actions, L.T("Yolu kopyala", "Copy path"), (_, _) => { if (grid.SelectedItem is DiskEntry item) { try { Clipboard.SetText(item.Path); } catch (Exception e) { Error(e); } } });
        Action(actions, L.T("Seçilenleri sil", "Delete selected"), async (_, _) =>
        {
            var selected = entries.Where(e => e.Selected).Where(e => e.Kind != "Klasör" && e.Result != "Silindi").ToList();
            if (selected.Count == 0) { AppDialog.Show(this, L.T("Silmek için dosya veya boş klasör seç. Dolu klasörlerin konumunu açabilirsin.", "Select files or empty folders to delete. You can open the location of non-empty folders."), L.T("Seçim gerekli", "Selection required")); return; }
            if (AppDialog.Show(this, L.T($"{selected.Count} seçili öğe kalıcı silinecek; yedek oluşturulmaz.\n\n", $"{selected.Count} selected items will be deleted permanently; no backup is made.\n\n") + string.Join("\n", selected.Select(e => e.Path)), L.T("Seçilenleri sil", "Delete selected"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            await Work(async () =>
            {
                int done = 0, failed = 0;
                foreach (var item in selected)
                {
                    if (cancellation!.IsCancellationRequested) break;
                    try { string boundary = scannedRoots.First(r => Safety.IsWithin(item.Path, r)); await Task.Run(() => DiskTools.Delete(item, boundary, mode, scannedAge)); item.Result = "Silindi"; item.Selected = false; done++; }
                    catch (Exception e) { item.Result = "Atlandı: " + e.Message; failed++; }
                    meter.IsIndeterminate = false; meter.Value = (done + failed) * 100d / selected.Count; status.Text = L.T($"Silme · %{meter.Value:0} · {done + failed}/{selected.Count}", $"Deleting · {meter.Value:0}% · {done + failed}/{selected.Count}");
                    try { Journal.Add("Araçlar", item.Result, item.Path, item.Path); } catch { }
                }
                grid.Items.Refresh(); status.Text = L.T($"{done} silindi", $"{done} deleted") + (failed > 0 ? L.T($" · {failed} atlandı (nedeni SONUÇ sütununda)", $" · {failed} skipped (reason in the RESULT column)") : "") + (selected.Count - done - failed > 0 ? L.T($" · {selected.Count - done - failed} işlenmedi", $" · {selected.Count - done - failed} not processed") : "") + ".";
            });
        });
        bar.Margin = new Thickness(0, 10, 0, 12); bar.Children.Add(cancel); foreach (FrameworkElement control in bar.Children) { control.Height = 38; control.VerticalAlignment = VerticalAlignment.Center; } header.Children.Add(bar); DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header); panel.Children.Add(grid); page.Content = panel;
    }
    void Startup()
    {
        var panel = new DockPanel(); var header = new StackPanel(); var bar = new WrapPanel();
        header.Children.Add(Ui.Info(L.T("Bilgisayar açılınca kendiliğinden çalışan programlar. Birini seçip aç veya kapat.", "Programs that start automatically with the computer. Select one to turn it on or off."),
            L.T("Kullanıcıya ve bilgisayara ait Run kayıtları (32/64 bit) listelenir. Açık/kapalı durumu Windows başlangıç izinlerinden okunur; değişiklik Windows Ayarlar'daki anahtarla aynı yere yazılır. Görev Zamanlayıcı, servisler ve başlangıç klasörü bu listede değildir.", "Run entries of the user and the computer (32/64-bit) are listed. The on/off state is read from Windows startup approvals; changes are written to the same place as the switch in Windows Settings. Task Scheduler, services and the Startup folder are not in this list.")));
        var grid = new DataGrid { IsReadOnly = true, RowHeight = 46, SelectionMode = DataGridSelectionMode.Single };
        Column(grid, L.T("AD", "NAME"), "Name", 190); Column(grid, L.T("DURUM", "STATE"), "State", 130); Column(grid, L.T("KİMİN İÇİN", "FOR WHOM"), "Scope", 150); Column(grid, L.T("KOMUT", "COMMAND"), "Command");
        var search = new TextBox { Width = 230, ToolTip = L.T("Başlangıç girdilerinde ara", "Search startup entries") }; bar.Children.Add(search); List<StartupEntry> entries = [];
        void Filter() => grid.ItemsSource = entries.Where(e => (e.Name + " " + e.Command).Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToList();
        void Reload() { try { entries = StartupTools.Read(); Filter(); } catch (Exception e) { Error(e); } }
        search.TextChanged += (_, _) => Filter();
        Action(bar, L.T("Yenile", "Reload"), (_, _) => Reload());
        var toggle = Action(bar, L.T("Başlangıçta aç", "Turn on at startup"), (_, _) =>
        {
            if (grid.SelectedItem is not StartupEntry entry) return;
            if (AppDialog.Show(this, entry.Name + (entry.Enabled == false ? L.T(" başlangıçta çalışacak şekilde açılacak.", " will be turned on to run at startup.") : L.T(" başlangıçta çalışması kapatılacak.", " will be turned off at startup.")) + "\n\n" + entry.Command, L.T("Başlangıç kaydı", "Startup entry"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            try { StartupTools.Toggle(entry); Journal.Add("Başlangıç", entry.Enabled == false ? "Etkinleştirildi" : "Devre dışı", entry.Name); Reload(); } catch (Exception e) { Error(e); }
        });
        toggle.IsEnabled = false;
        grid.SelectionChanged += (_, _) => { var entry = grid.SelectedItem as StartupEntry; toggle.IsEnabled = entry?.Enabled != null; toggle.Content = entry?.Enabled == false ? L.T("Başlangıçta aç", "Turn on at startup") : L.T("Başlangıçta kapat", "Turn off at startup"); };
        Action(bar, L.T("Windows başlangıç ayarları", "Windows startup settings"), (_, _) => { try { Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true }); } catch (Exception e) { Error(e); } });
        Action(bar, L.T("Başlangıç klasörü", "Startup folder"), (_, _) => { try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Environment.GetFolderPath(Environment.SpecialFolder.Startup) + "\"") { UseShellExecute = true }); } catch (Exception e) { Error(e); } });
        header.Children.Add(bar); DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header); panel.Children.Add(grid); page.Content = panel; Reload();
    }
}






