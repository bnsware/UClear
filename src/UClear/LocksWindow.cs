using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace UClear;
public sealed class LocksWindow : DialogWindow
{
    private string folder;
    private LockReport? report;
    private bool busy;
    private CancellationTokenSource? cancellation;
    private string lastAction = "";
    public LocksWindow(string path, LockReport? initialReport = null) : base(L.T("Dosya / klasör kilitleri ve silme engelleri", "File / folder locks and deletion blockers"), 880, 620)
    {
        folder = path; report = initialReport;
        Loaded += async (_, _) => { if (report == null) await Refresh(); else Render(); };
        Closing += (_, e) => { if (busy) { e.Cancel = true; cancellation?.Cancel(); } };
    }
    private async Task Refresh()
    {
        if (busy) return;
        busy = true; cancellation = new();
        var loading = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var counter = new TextBlock { Text = L.T("Klasör erişimi ve dosyaları kullanan süreçler inceleniyor…", "Inspecting folder access and processes using the files…"), TextWrapping = TextWrapping.Wrap }; loading.Children.Add(counter);
        var meter = new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 16, 0, 16) }; loading.Children.Add(meter);
        var live = new Progress<ScanProgress>(p => { counter.Text = p.Text; meter.IsIndeterminate = !p.Total.HasValue; meter.Value = p.Percent; });
        var cancel = new Button { Content = L.T("Durdur", "Stop"), HorizontalAlignment = HorizontalAlignment.Right }; cancel.Click += (_, _) => cancellation?.Cancel(); loading.Children.Add(cancel); Body.Content = loading;
        try { report = await Task.Run(() => FileLocks.Inspect(folder, cancellation.Token, progress: live)); }
        catch (OperationCanceledException) { lastAction = L.T("İnceleme iptal edildi; önceki sonuçlar güncel olmayabilir.", "Inspection cancelled; earlier results may be out of date."); }
        catch (Exception error) { lastAction = L.T("İnceleme tamamlanamadı: ", "Inspection could not be completed: ") + error.Message; }
        finally { busy = false; cancellation.Dispose(); cancellation = null; Render(); }
    }
    private void Render()
    {
        var current = report ?? new LockReport([], 0, [], []);
        var panel = new DockPanel();
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var path = new TextBox { Text = folder, IsReadOnly = true }; header.Children.Add(path);
        var toolbar = new WrapPanel { Margin = new Thickness(0, 10, 0, 10) };
        var choose = new Button { Content = L.T("Başka klasör seç", "Choose another folder") }; choose.Click += async (_, _) =>
        {
            if (busy) return;
            var picker = new OpenFolderDialog { Title = L.T("İncelenecek klasörü seç", "Choose the folder to inspect") };
            if (picker.ShowDialog(this) == true) { folder = picker.FolderName; report = null; lastAction = ""; await Refresh(); }
        };
        var refresh = new Button { Content = L.T("Yeniden incele", "Inspect again") }; refresh.Click += async (_, _) => await Refresh();
        toolbar.Children.Add(choose); toolbar.Children.Add(refresh); header.Children.Add(toolbar);
        header.Children.Add(Ui.Info(current.Owners.Count == 0 ? L.T("Bu konumu kullanan program bulunamadı.", "No program is using this location.") : L.T($"{current.Owners.Count} program bu konumu kullanıyor. Silmeden önce onları kapat.", $"{current.Owners.Count} programs are using this location. Close them before deleting."),
            L.T($"{current.FilesChecked} dosya incelendi. Kullanımda görünen her dosya kilitli olmayabilir; erişim reddi veya paylaşım ihlali saptanırsa aşağıdaki ayrıntılarda gösterilir. Bu pencere klasörü silmez ve izinlerini değiştirmez. Zorla sonlandırma kaydedilmemiş çalışmaları kaybettirebilir.", $"{current.FilesChecked} files inspected. Not every file in use is necessarily locked; access denials or sharing violations found are shown in the details below. This window does not delete the folder or change its permissions. Force ending a process can lose unsaved work.")));
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var message = new TextBlock { Text = lastAction, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSlateGray, Visibility = lastAction.Length > 0 ? Visibility.Visible : Visibility.Collapsed };
        footer.Children.Add(message);
        if (current.Warnings.Count > 0) footer.Children.Add(new Disclosure(L.T($"Silme engelleri ve uyarılar ({current.Warnings.Count})", $"Deletion blockers and warnings ({current.Warnings.Count})"), new TextBox { Text = string.Join("\n", current.Warnings), IsReadOnly = true, Height = 80, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 8) }));
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var copy = new Button { Content = L.T("Raporu kopyala", "Copy report") };
        copy.Click += (_, _) => { try { Clipboard.SetText(folder + "\n" + string.Join("\n", current.Owners.Select(p => $"{p.Program} · PID {p.Pid} · {p.Service}").Concat(current.Uses.Select(u => $"{u.File} · {u.Program} · PID {u.Pid}")).Concat(current.Warnings))); message.Text = L.T("Rapor kopyalandı.", "Report copied."); } catch (System.Runtime.InteropServices.ExternalException) { message.Text = L.T("Panoya erişilemedi; yeniden dene.", "The clipboard is not available; try again."); } message.Visibility = Visibility.Visible; };
        var kill = new Button { Content = L.T("Zorla sonlandır", "Force end"), IsEnabled = false, Background = new SolidColorBrush(Color.FromRgb(164, 71, 86)), Margin = new Thickness(0) };
        actions.Children.Add(copy); actions.Children.Add(kill); footer.Children.Add(actions); DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        var grid = new DataGrid { ItemsSource = current.Owners, IsReadOnly = true, RowHeight = 40, SelectionMode = DataGridSelectionMode.Single };
        grid.Columns.Add(new DataGridTextColumn { Header = L.T("PROGRAM", "PROGRAM"), Binding = new Binding(nameof(LockOwner.Program)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = L.T("TÜR", "TYPE"), Binding = new Binding(nameof(LockOwner.Kind)) { Converter = new DisplayText() }, Width = 120 });
        grid.Columns.Add(new DataGridTextColumn { Header = "PID", Binding = new Binding(nameof(LockOwner.Pid)), Width = 85 });
        grid.SelectionChanged += (_, _) => { kill.IsEnabled = grid.SelectedItem is LockOwner owner && ProcessControl.Allowed(owner); };
        async Task Stop(bool force)
        {
            if (busy || grid.SelectedItem is not LockOwner owner) return;
            string prompt = force ? L.T($"{owner.Program} (PID {owner.Pid}) ve alt süreçleri zorla sonlandırılacak. Kaydedilmemiş çalışmalar kaybolabilir.\n\nDevam edilsin mi?", $"{owner.Program} (PID {owner.Pid}) and its child processes will be force-ended. Unsaved work may be lost.\n\nContinue?") : L.T($"{owner.Program} (PID {owner.Pid}) için normal kapatma isteği gönderilsin mi?", $"Send a normal close request to {owner.Program} (PID {owner.Pid})?");
            if (AppDialog.Show(this, prompt, force ? L.T("Süreci sonlandır", "End process") : L.T("Süreci kapat", "Close process"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            busy = true; panel.IsEnabled = false;
            try { lastAction = await ProcessControl.Stop(owner, force); }
            catch (Exception error) { lastAction = L.T("Süreç kapatılamadı: ", "The process could not be closed: ") + error.GetBaseException().Message; AppDialog.Show(this, lastAction, L.T("Sonlandırma başarısız", "End failed")); }
            finally { busy = false; panel.IsEnabled = true; }
            await Refresh();
        }
        kill.Click += async (_, _) => await Stop(true);
        var details = new DataGrid { ItemsSource = current.Uses, IsReadOnly = true, RowHeight = 40, Height = 200 };
        details.Columns.Add(new DataGridTextColumn { Header = L.T("DOSYA", "FILE"), Binding = new Binding(nameof(FileUse.File)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        details.Columns.Add(new DataGridTextColumn { Header = L.T("SÜREÇ", "PROCESS"), Binding = new Binding(nameof(FileUse.Program)), Width = 160 });
        details.Columns.Add(new DataGridTextColumn { Header = "PID", Binding = new Binding(nameof(FileUse.Pid)), Width = 85 });
        details.SelectionChanged += (_, _) => { if (details.SelectedItem is FileUse use) grid.SelectedItem = current.Owners.FirstOrDefault(p => p.Pid == use.Pid); };
        // Per-file usage is detail; the program list answers "what do I close?".
        if (current.Uses.Count > 0) footer.Children.Insert(0, new Disclosure(L.T($"Hangi dosyayı hangi program kullanıyor ({current.Uses.Count})", $"Which program uses which file ({current.Uses.Count})"), details));
        panel.Children.Add(grid); Body.Content = panel;
    }
}



