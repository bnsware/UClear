using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;

namespace UClear;
public class DialogWindow : Window
{
    public ContentControl Body { get; } = new() { Margin = new Thickness(22, 18, 22, 22) };
    public DialogWindow(string title, double width, double height)
    {
        Title = title; Width = width; Height = height; MinWidth = 420; MinHeight = 240;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/UClear.ico"));
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize;
        Background = new SolidColorBrush(Color.FromRgb(16, 22, 30)); Foreground = Brushes.WhiteSmoke;
        FontFamily = new FontFamily("Segoe UI"); UseLayoutRounding = true;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 42, ResizeBorderThickness = new Thickness(5), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(12), UseAeroCaptionButtons = false });
        var layout = new Grid(); layout.RowDefinitions.Add(new() { Height = new GridLength(42) }); layout.RowDefinitions.Add(new());
        var header = new Grid { Background = new SolidColorBrush(Color.FromRgb(23, 31, 41)) };
        header.Children.Add(new TextBlock { Text = title, Margin = new Thickness(16, 0, 58, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var close = new Button { Content = "×", FontSize = 21, HorizontalAlignment = HorizontalAlignment.Right, Style = (Style)FindResource("CloseCaptionButton") };
        close.Click += (_, _) => Close(); header.Children.Add(close); layout.Children.Add(header);
        Grid.SetRow(Body, 1); layout.Children.Add(Body);
        Content = new Border { Child = layout, CornerRadius = new CornerRadius(12), BorderBrush = new SolidColorBrush(Color.FromRgb(51, 64, 79)), BorderThickness = new Thickness(1), Background = Background };
        WindowAppearance.Attach(this);
    }
}

// Pages show one short line; technical explanations open on demand.
public static class Ui
{
    static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(99, 216, 187));
    public static FrameworkElement Info(string summary, string details)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var more = new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSlateGray, FontSize = 11, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        var label = new System.Windows.Documents.Run(L.T("Ayrıntılar", "Details"));
        var link = new System.Windows.Documents.Hyperlink(label) { Foreground = Accent };
        link.Click += (_, _) => { bool show = more.Visibility != Visibility.Visible; more.Visibility = show ? Visibility.Visible : Visibility.Collapsed; label.Text = show ? L.T("Gizle", "Hide") : L.T("Ayrıntılar", "Details"); };
        var line = new TextBlock { TextWrapping = TextWrapping.Wrap };
        if (summary.Length > 0) line.Inlines.Add(new System.Windows.Documents.Run(summary + "  "));
        line.Inlines.Add(link);
        panel.Children.Add(line); panel.Children.Add(more); return panel;
    }
}
public sealed class Disclosure : StackPanel
{
    readonly System.Windows.Documents.Run label = new();
    readonly UIElement content;
    string title;
    public Disclosure(string title, UIElement content)
    {
        this.title = title; this.content = content; content.Visibility = Visibility.Collapsed;
        var link = new System.Windows.Documents.Hyperlink(label) { Foreground = new SolidColorBrush(Color.FromRgb(99, 216, 187)), TextDecorations = null };
        link.Click += (_, _) => { content.Visibility = content.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; Update(); };
        Children.Add(new TextBlock { Margin = new Thickness(0, 4, 0, 6), FontSize = 12, Inlines = { link } }); Children.Add(content); Update();
    }
    public string Title { get => title; set { title = value; Update(); } }
    public void Expand() { content.Visibility = Visibility.Visible; Update(); }
    void Update() => label.Text = (content.Visibility == Visibility.Visible ? "▾ " : "▸ ") + title;
}
public static class AppDialog
{
    public static MessageBoxResult Show(Window owner, string text, string title, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
    {
        var window = new DialogWindow(title, 580, 340) { Owner = owner };
        var panel = new DockPanel(); var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var result = buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel;
        if (buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel or MessageBoxButton.OKCancel)
        {
            var cancel = new Button { Content = L.T("Vazgeç", "Cancel"), IsCancel = true }; actions.Children.Add(cancel);
        }
        var accept = new Button { Content = buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel ? L.T("Devam et", "Continue") : L.T("Tamam", "OK"), Background = new SolidColorBrush(Color.FromRgb(97, 217, 182)), Foreground = new SolidColorBrush(Color.FromRgb(16, 40, 31)), Margin = new Thickness(0) };
        accept.Click += (_, _) => { result = buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel ? MessageBoxResult.Yes : MessageBoxResult.OK; window.DialogResult = true; };
        actions.Children.Add(accept); DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions);
        panel.Children.Add(new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        window.Body.Content = panel; window.ShowDialog(); return result;
    }
}
