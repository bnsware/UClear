using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace UClear;
public sealed class ConfirmWindow : DialogWindow
{
    private readonly CleanupSelection selection;
    public List<Finding> SelectedFindings => selection.Selected;
    public ConfirmWindow(string name, List<Finding> findings, bool afterUninstall = false, int excluded = 0, int warnings = 0)
        : base(L.T("Kalıntıları seç — ", "Select leftovers — ") + name, 820, 560)
    {
        selection = new(findings);
        var panel = new DockPanel();
        var heading = new TextBlock { Text = afterUninstall ? L.T($"{name} kalıntılarını temizle", $"Clean leftovers of {name}") : L.T("Silinecek öğeleri seç", "Select items to delete"), FontSize = 21, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var explanation = Ui.Info(L.T("Saklamak istediklerinin işaretini kaldır. Seçilenler kalıcı silinir.", "Untick what you want to keep. Selected items are deleted permanently."),
            L.T($"Listede yalnızca temizlenmesi uygun görülen öğeler var. {excluded} öğe korunan veya belirsiz olduğu için listeye alınmadı; {warnings} konum taranırken uyarı verdi. Yedek oluşturulmaz. Bir öğenin neden bulunduğunu görmek için fareyi üzerinde beklet.", $"The list only contains items considered fit for cleanup. {excluded} items were left out because they are protected or uncertain; {warnings} locations gave warnings during the scan. No backup is made. Hover over an item to see why it was found."));
        explanation.Margin = new Thickness(0, 0, 0, 14);
        DockPanel.SetDock(explanation, Dock.Top); panel.Children.Add(explanation);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var all = new CheckBox { Content = L.T("Tümünü seç", "Select all"), IsChecked = true, VerticalAlignment = VerticalAlignment.Center }; all.Click += (_, _) => selection.SelectAll(all.IsChecked == true);
        var none = new Button { Content = L.T("Seçimi kaldır", "Clear selection") }; none.Click += (_, _) => selection.SelectAll(false);
        controls.Children.Add(all); controls.Children.Add(none); DockPanel.SetDock(controls, Dock.Top); panel.Children.Add(controls);
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var count = new TextBlock { Margin = new Thickness(0, 0, 0, 8) };
        var conflict = new TextBlock { Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = afterUninstall ? L.T("Hayır, kalsın", "No, keep them") : L.T("Vazgeç", "Cancel"), IsCancel = true };
        var delete = new Button { Content = L.T("Seçilenleri temizle", "Clean selected"), Background = new SolidColorBrush(Color.FromRgb(164, 71, 86)), Margin = new Thickness(0) };
        delete.Click += (_, _) => { if (selection.Selected.Count > 0 && selection.Conflict == null) DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(delete); footer.Children.Add(count); footer.Children.Add(conflict); footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        var list = new ListBox { ItemsSource = selection.Choices, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var row = new FrameworkElementFactory(typeof(Grid));
        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        check.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        check.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(CleanupChoice.Selected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        check.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, new Binding("Finding.DisplayPath"));
        row.AppendChild(check);
        var text = new FrameworkElementFactory(typeof(StackPanel)); text.SetValue(FrameworkElement.MarginProperty, new Thickness(34, 0, 0, 0));
        var kind = new FrameworkElementFactory(typeof(TextBlock)); kind.SetBinding(TextBlock.TextProperty, new Binding("Finding.Kind") { Converter = new DisplayText() }); kind.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(97, 217, 182))); kind.SetValue(TextBlock.FontSizeProperty, 11d); text.AppendChild(kind);
        var path = new FrameworkElementFactory(typeof(TextBlock)); path.SetBinding(TextBlock.TextProperty, new Binding("Finding.DisplayPath")); path.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); path.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 4)); text.AppendChild(path);
        text.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Finding.Reason"));
        row.AppendChild(text); list.ItemTemplate = new DataTemplate { VisualTree = row }; panel.Children.Add(list);
        void Update()
        {
            all.IsChecked = selection.Selected.Count == 0 ? false : selection.Selected.Count == selection.Choices.Count ? true : null;
            count.Text = L.T($"{selection.Selected.Count} / {selection.Choices.Count} öğe seçili", $"{selection.Selected.Count} / {selection.Choices.Count} items selected");
            conflict.Text = selection.Conflict ?? ""; conflict.Visibility = conflict.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            delete.IsEnabled = selection.Selected.Count > 0 && selection.Conflict == null;
        }
        foreach (var choice in selection.Choices) choice.PropertyChanged += (_, _) => Update();
        Update(); Body.Content = panel;
    }
}

