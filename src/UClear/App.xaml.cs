using System.Windows;

namespace UClear;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public App()
    {
        // An unexpected UI error is reported instead of silently closing the program; deletions in progress verify each item on their own.
        DispatcherUnhandledException += (_, e) =>
        {
            Log(e.Exception);
            MessageBox.Show(L.T("Beklenmeyen bir hata oluştu; işlem yarıda kalmış olabilir.\n\n", "An unexpected error occurred; the operation may be incomplete.\n\n") + e.Exception.Message, "UClear", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception error) Log(error); };
    }
    static void Log(Exception error)
    {
        try
        {
            string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UClear");
            System.IO.Directory.CreateDirectory(folder);
            System.IO.File.AppendAllText(System.IO.Path.Combine(folder, "errors.log"), $"{DateTimeOffset.Now:O}\n{error}\n\n");
        }
        catch { }
    }
}
