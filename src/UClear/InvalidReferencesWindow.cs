using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
namespace UClear;
public sealed class InvalidReferencesWindow : DialogWindow
{
    bool busy, selecting;
    CancellationTokenSource? cancellation;
    List<InvalidReference> items = [];
    ObservableCollection<InvalidReference> view = [];
    static readonly SortDescription[] DefaultSort = [new(nameof(InvalidReference.Rank), ListSortDirection.Ascending), new(nameof(InvalidReference.Category), ListSortDirection.Ascending), new(nameof(InvalidReference.Location), ListSortDirection.Ascending)];
    readonly DataGrid grid = new() { IsReadOnly = true, RowHeight = 48, SelectionMode = DataGridSelectionMode.Single };
    readonly CheckBox all = new() { ToolTip = L.T("Görünen, silinebilir öğeleri seç", "Select the visible deletable items") };
    readonly TextBox search = new() { Width = 220, ToolTip = L.T("Ad, konum veya hedef ara", "Search name, location or target"), Height = 38, Margin = new Thickness(0, 0, 10, 0) };
    readonly CheckBox onlyMissing = new() { Content = L.T("Yalnızca silinebilenler", "Deletable only"), IsChecked = true, VerticalAlignment = VerticalAlignment.Center, ToolTip = L.T("Kapatınca Windows'a ait ve yalnızca bilgi amaçlı kayıtlar da görünür", "Untick to also show Windows-owned and information-only entries") };
    readonly TextBlock status = new() { Text = L.T("Başlamak için Tara'ya bas.", "Press Scan to start."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
    readonly ProgressBar progress = new() { Height = 4, Visibility = Visibility.Collapsed };
    readonly WrapPanel actions = new();
    readonly TextBox warnings = new() { IsReadOnly = true, Height = 65, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
    readonly TextBox detail = new() { IsReadOnly = true, Height = 100, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Text = L.T("Ayrıntısını görmek için listeden bir satır seç.", "Select a row in the list to see its details.") };
    readonly Disclosure more;
    public InvalidReferencesWindow() : base(L.T("Geçersiz başvurular", "Invalid references"), 1120, 750)
    {
        var panel = new DockPanel(); var top = new StackPanel();
        top.Children.Add(Ui.Info(L.T("Silinmiş programlardan kalan bozuk kısayolları ve kayıtları bulur. Emin değilsen \"Kesin olanları sil\" yeterli.", "Finds broken shortcuts and entries left by removed programs. If unsure, \"Delete safe ones\" is enough."),
            L.T("Taranan yerler: başlangıç kayıtları, kısayollar, uygulama yolları, COM/komut kayıtları, kaldırıcılar, servisler ve zamanlanmış görevler.\n", "Scanned places: startup entries, shortcuts, app paths, COM/command entries, uninstallers, services and scheduled tasks.\n") +
            L.T("Kesin silinebilir: programın klasörü tamamen kaldırılmış veya içinde program dosyası kalmamış. Seçerek silinebilir: dosya yok ama klasörde başka program dosyaları var; güncelleme sürüyor olabilir, kendin karar ver.\n", "Safe to delete: the program's folder is completely gone or holds no program files. Deletable by choice: the file is missing but the folder still has other program files; an update may be in progress, so decide yourself.\n") +
            L.T("Windows/Microsoft bileşenleri, sürücüler, \\Microsoft\\ görevleri ve kaldırma kayıtları asla silinmez, yalnızca gösterilir. Servis yalnızca durmuşsa ve başka bir servis ona bağlı değilse silinir. Registry'de yalnızca bozuk değer kaldırılır, anahtarlar silinmez. Her öğe silmeden hemen önce yeniden doğrulanır.", "Windows/Microsoft components, drivers, \\Microsoft\\ tasks and uninstall entries are never deleted, only shown. A service is deleted only if it is stopped and no other service depends on it. In the Registry only the broken value is removed; keys are not deleted. Every item is verified again right before deletion.")));
        var bar = new WrapPanel(); bar.Children.Add(search); bar.Children.Add(onlyMissing);
        var scan = new Button { Content = L.T("Tara", "Scan"), Margin = new Thickness(12, 0, 8, 0) }; var stop = new Button { Content = L.T("Durdur", "Stop"), IsEnabled = false };
        bar.Children.Add(scan); bar.Children.Add(stop); top.Children.Add(bar); top.Children.Add(status); top.Children.Add(progress); DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        var details = new StackPanel(); details.Children.Add(detail); details.Children.Add(warnings); more = new Disclosure(L.T("Ayrıntılar", "Details"), details);
        var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) }; footer.Children.Add(more); footer.Children.Add(actions); DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        var select = new FrameworkElementFactory(typeof(CheckBox)); select.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(InvalidReference.Selected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }); select.SetBinding(CheckBox.IsEnabledProperty, new Binding(nameof(InvalidReference.CanDelete)));
        grid.Columns.Add(new DataGridTemplateColumn { Header = all, CellTemplate = new DataTemplate { VisualTree = select }, Width = 42, CanUserSort = false });
        AddColumn(L.T("TÜR", "TYPE"), "Category", 120); AddColumn(L.T("NEREDE", "WHERE"), "Location", 1); AddColumn(L.T("GÖSTERDİĞİ HEDEF", "TARGET IT POINTS TO"), "Target", 1); AddColumn(L.T("KARAR", "DECISION"), "Policy", 135); grid.Columns[^1].SortMemberPath = nameof(InvalidReference.Rank);
        panel.Children.Add(grid); Body.Content = panel;
        grid.SelectionChanged += (_, _) => { if (grid.SelectedItem is InvalidReference item) detail.Text = Detail(item); };
        grid.MouseDoubleClick += (_, e) => { if (e.OriginalSource is FrameworkElement { DataContext: InvalidReference }) more.Expand(); };
        all.Click += (_, _) => { selecting = true; foreach (var item in grid.Items.Cast<InvalidReference>().Where(i => i.CanDelete)) item.Selected = all.IsChecked == true; selecting = false; UpdateAll(); };
        search.TextChanged += (_, _) => Filter(); onlyMissing.Click += (_, _) => Filter();
        var copy = new Button { Content = L.T("Konumu kopyala", "Copy location") }; copy.Click += (_, _) => { if (grid.SelectedItem is InvalidReference item) { try { Clipboard.SetText(item.Location); } catch (Exception e) { Error(e); } } }; actions.Children.Add(copy);
        var selectCertain = new Button { Content = L.T("Kesin olanları seç", "Select safe ones"), ToolTip = L.T("Program klasörü tamamen kaldırılmış bulguları seçer", "Selects findings whose program folder is completely gone") }; actions.Children.Add(selectCertain);
        var deleteCertain = new Button { Content = L.T("Kesin olanları sil", "Delete safe ones"), ToolTip = L.T("Onay sonrası yalnızca kesin silinebilir bulguları siler", "After confirmation, deletes only the findings that are safe to delete") }; actions.Children.Add(deleteCertain);
        var delete = new Button { Content = L.T("Seçilen kayıtları sil", "Delete selected entries") }; actions.Children.Add(delete);
        // While scanning, the list stays usable (scroll, search, inspect); only actions wait for the scan.
        async Task Work(Func<Task> work, bool scanning = false)
        {
            busy = true; cancellation = new(); scan.IsEnabled = actions.IsEnabled = false; search.IsEnabled = onlyMissing.IsEnabled = grid.IsEnabled = scanning; stop.IsEnabled = true; progress.Visibility = Visibility.Visible; progress.IsIndeterminate = true;
            try { await work(); } catch (OperationCanceledException) { status.Text = L.T($"Tarama durduruldu; o ana kadar bulunan {items.Count} kayıt listede.", $"Scan stopped; the {items.Count} entries found so far are listed."); } catch (Exception e) { Error(e); }
            finally { busy = false; cancellation.Dispose(); cancellation = null; scan.IsEnabled = search.IsEnabled = onlyMissing.IsEnabled = actions.IsEnabled = grid.IsEnabled = true; stop.IsEnabled = false; progress.IsIndeterminate = false; }
        }
        scan.Click += async (_, _) => await Work(async () =>
        {
            items = []; Filter(); detail.Text = L.T("Ayrıntısını görmek için listeden bir satır seç.", "Select a row in the list to see its details."); warnings.Visibility = Visibility.Collapsed; more.Title = L.T("Ayrıntılar", "Details");
            var live = new Progress<ScanProgress>(p => { if (!busy) return; status.Text = L.T($"{p.Stage} · {items.Count(i => i.CanDelete)} silinebilir kayıt bulundu…", $"{L.V(p.Stage)} · {items.Count(i => i.CanDelete)} deletable entries found…"); progress.IsIndeterminate = !p.Total.HasValue; progress.Value = p.Percent; });
            var feed = new LiveFeed<InvalidReference>(Add);
            InvalidReport report;
            try { report = await Task.Run(() => InvalidReferences.Scan(cancellation!.Token, live, feed.Post)); }
            finally { feed.Dispose(); feed.Drain(); } warnings.Text = L.T("Okunamayan konumlar:\n", "Locations that could not be read:\n") + string.Join("\n", report.Warnings); warnings.Visibility = report.Warnings.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            more.Title = report.Warnings.Count == 0 ? L.T("Ayrıntılar", "Details") : L.T($"Ayrıntılar · {report.Warnings.Count} okunamayan konum", $"Details · {report.Warnings.Count} unreadable locations");
            int certain = items.Count(i => i.Certain), manual = items.Count(i => i.CanDelete && !i.Certain);
            int hidden = items.Count(i => !i.CanDelete);
            status.Text = (certain + manual == 0 ? L.T("Temizlenecek bozuk kayıt bulunamadı.", "No broken entries to clean were found.") : L.T($"{certain} kesin silinebilir · {manual} kararı sana kalmış kayıt bulundu.", $"Found {certain} safe-to-delete entries · {manual} entries for you to decide."))
                + (hidden > 0 ? L.T($" Windows'a ait veya bilgi amaçlı {hidden} kayıt gizli.", $" {hidden} Windows-owned or information-only entries are hidden.") : "");
        }, scanning: true);
        stop.Click += (_, _) => cancellation?.Cancel();
        List<InvalidReference> Certain() => items.Where(i => i.Certain && i.CanDelete && i.Result != "Silindi").ToList();
        void SelectCertain()
        {
            selecting = true; foreach (var item in items) item.Selected = item.Certain && item.Result != "Silindi"; selecting = false;
            if (search.Text != "") search.Text = ""; else Filter();
        }
        selectCertain.Click += (_, _) =>
        {
            SelectCertain(); int count = Certain().Count;
            status.Text = count == 0 ? L.T("Kesin silinebilir bulgu yok. Diğer adayları tek tek incele.", "No safe-to-delete findings. Review the other candidates one by one.") : L.T($"{count} kesin silinebilir bulgu seçildi; diğer seçimler kaldırıldı.", $"{count} safe-to-delete findings selected; other selections were cleared.");
        };
        deleteCertain.Click += async (_, _) =>
        {
            var chosen = Certain();
            if (chosen.Count == 0) { status.Text = L.T("Kesin silinebilir bulgu yok. Diğer adayları tek tek incele.", "No safe-to-delete findings. Review the other candidates one by one."); return; }
            SelectCertain(); await Delete(chosen, L.T("Kesin olanları sil", "Delete safe ones"));
        };
        delete.Click += async (_, _) => await Delete(items.Where(i => i.Selected && i.CanDelete && i.Result != "Silindi").ToList(), L.T("Seçilenleri sil", "Delete selected"));
        async Task Delete(List<InvalidReference> chosen, string title)
        {
            if (chosen.Count == 0) return;
            string preview = string.Join("\n", chosen.Take(100).Select(i => $"[{L.V(i.Category)}] {i.Location}" + (i.Name == "" ? "" : " → " + i.Name)));
            if (AppDialog.Show(this, L.T($"{chosen.Count} kayıt/kısayol kalıcı silinecek. Registry'de yalnızca ilgili değer kaldırılır; üst anahtar korunur. Her öğe silmeden hemen önce yeniden doğrulanır.\n\n{preview}", $"{chosen.Count} entries/shortcuts will be deleted permanently. In the Registry only the affected value is removed; the parent key is kept. Every item is verified again right before deletion.\n\n{preview}") + (chosen.Count > 100 ? L.T("\n… Önizlemede ilk 100 öğe.", "\n… The preview shows the first 100 items.") : ""), title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            await Work(async () =>
            {
                int done = 0, failed = 0; selecting = true;
                try
                {
                    foreach (var item in chosen)
                    {
                        if (cancellation!.IsCancellationRequested) break;
                        try { await Task.Run(() => InvalidReferences.Delete(item)); item.Result = "Silindi"; item.Selected = false; done++; }
                        catch (Exception e) { item.Result = e.Message; failed++; }
                        try { Journal.Add("Geçersiz başvurular", item.Result, item.Location + " → " + item.Name, item.Location); } catch { }
                        progress.IsIndeterminate = false; progress.Value = (done + failed) * 100d / chosen.Count; status.Text = L.T($"Silme · %{progress.Value:0} · {done + failed}/{chosen.Count}", $"Deleting · {progress.Value:0}% · {done + failed}/{chosen.Count}");
                    }
                }
                finally { selecting = false; }
                Filter(); detail.Text = string.Join("\n", chosen.Select(i => i.Location + " → " + i.Result));
                status.Text = L.T($"{done} silindi", $"{done} deleted") + (failed > 0 ? L.T($" · {failed} silinemedi (nedeni Ayrıntılar'da)", $" · {failed} not deleted (reason under Details)") : "") + (chosen.Count - done - failed > 0 ? L.T($" · {chosen.Count - done - failed} işlenmedi", $" · {chosen.Count - done - failed} not processed") : "") + ".";
            });
        }
        Closing += (_, e) => { if (busy) { e.Cancel = true; cancellation?.Cancel(); } };
    }
    static string Detail(InvalidReference item)
    {
        string value = item.Name == "" ? L.T("(Varsayılan)", "(Default)") : item.Name, result = item.Result == "" ? "" : L.T("\nSonuç: ", "\nResult: ") + L.V(item.Result);
        return L.T($"Konum: {item.Location}\nDeğer: {value} · {item.View}\nKayıtlı komut: {item.Original}\nDurum: {L.V(item.State)}\nKarar: {item.Policy}",
            $"Location: {item.Location}\nValue: {value} · {item.View}\nRecorded command: {item.Original}\nState: {L.V(item.State)}\nDecision: {item.Policy}") + result;
    }
    void AddColumn(string title, string property, double width) => grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(property) { Converter = new DisplayText() }, SortMemberPath = property, Width = width == 1 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width) });
    bool Visible(InvalidReference i) => i.Result != "Silindi" && (onlyMissing.IsChecked != true || i.CanDelete) && (i.Location + " " + i.Name + " " + i.Target).Contains(search.Text, StringComparison.OrdinalIgnoreCase);
    void Filter() { view = new(items.Where(Visible)); GridSort.Bind(grid, view, DefaultSort); UpdateAll(); }
    void Add(List<InvalidReference> batch)
    {
        foreach (var item in batch) { items.Add(item); item.PropertyChanged += (_, _) => { if (!selecting) UpdateAll(); }; if (Visible(item)) view.Add(item); }
        UpdateAll();
    }
    void UpdateAll() { var visible = grid.Items.Cast<InvalidReference>().Where(i => i.CanDelete).ToList(); all.IsChecked = visible.Count == 0 || visible.All(i => !i.Selected) ? false : visible.All(i => i.Selected) ? true : null; }
    void Error(Exception error) => AppDialog.Show(this, error.Message, L.T("İşlem tamamlanamadı", "Operation could not be completed"));
}
