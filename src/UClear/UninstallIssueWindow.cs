using System.Windows;
using System.Windows.Controls;

namespace UClear;

public enum UninstallChoice { Cancel, RetryAsAdmin, ConfirmCompleted }

public sealed class UninstallIssueWindow : DialogWindow
{
    public UninstallChoice Choice { get; private set; }
    public UninstallIssueWindow(string name, int code, bool registered, bool canRetry, bool knownFailure)
        : base(L.T("Kaldırma sonucu — ", "Uninstall result — ") + name, 660, 340)
    {
        var panel = new DockPanel();
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        void Button(string label, UninstallChoice choice)
        {
            var button = new Button { Content = label };
            button.Click += (_, _) => { Choice = choice; DialogResult = choice != UninstallChoice.Cancel; };
            buttons.Children.Add(button);
        }
        Button(L.T("Vazgeç", "Cancel"), UninstallChoice.Cancel);
        if (!knownFailure) Button(L.T("Kaldırma tamamlandı, tara", "Uninstall completed, scan"), UninstallChoice.ConfirmCompleted);
        if (canRetry) Button(L.T("Yönetici olarak tekrar dene", "Retry as administrator"), UninstallChoice.RetryAsAdmin);
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        string summary = knownFailure ? L.T($"{name} kaldırılamadı.", $"{name} could not be uninstalled.") : registered ? L.T($"{name} kaldırması bitmemiş olabilir.", $"The uninstall of {name} may not be finished.") : L.T($"{name} büyük olasılıkla kaldırıldı.", $"{name} was most likely uninstalled.");
        string advice = knownFailure ? L.T("Kaldırıcının kendi hata mesajını kontrol et.", "Check the uninstaller's own error message.") : L.T("Kaldırma penceresi hâlâ açıksa önce onu tamamla, sonra \"Kaldırma tamamlandı, tara\" de.", "If the uninstall window is still open, finish it first, then choose \"Uninstall completed, scan\".");
        string details = L.T($"Kaldırma aracı {code} koduyla sonlandı. ", $"The uninstaller exited with code {code}. ") +
            (knownFailure ? L.T("Windows Installer işlemin tamamlanmadığını bildirdi.", "Windows Installer reported that the operation did not complete.") : L.T("Bu kodun anlamı kaldırma aracına bağlıdır; tek başına başarı veya hata nedeni belirlenemiyor.", "The meaning of this code depends on the uninstaller; success or the reason for failure cannot be determined from it alone.")) +
            " " + (registered ? L.T("Uygulamanın kaldırma kaydı hâlâ mevcut.", "The application's uninstall entry still exists.") : L.T("Uygulamanın kaldırma kaydı artık bulunmuyor; bu tek başına işlemin tamamen bittiğini garanti etmez.", "The application's uninstall entry is gone; this alone does not guarantee the operation fully finished.")) +
            " " + (canRetry ? L.T("Yetki sorunu gördüysen yönetici olarak yeniden deneyebilirsin; Windows izin isteyecek.", "If you saw a permission problem, you can retry as administrator; Windows will ask for permission.") : L.T("Yönetici olarak deneme de sonuç vermedi.", "Retrying as administrator did not help either."));
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = summary, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        body.Children.Add(new TextBlock { Text = advice + L.T(" Hiçbir kalıntı silinmedi.", " No leftovers were deleted."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        body.Children.Add(Ui.Info("", details));
        panel.Children.Add(body);
        Body.Content = panel;
    }
}
