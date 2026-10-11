using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UClear;

public sealed partial record InstalledApp : INotifyPropertyChanged
{
    private ImageSource? icon;
    public ImageSource Icon => icon ?? AppIcons.Fallback;
    public void SetIcon(ImageSource value) { icon = value; PropertyChanged?.Invoke(this, new(nameof(Icon))); }
    // Ticked for bulk removal; Windows-owned Store packages can never be ticked.
    private bool selected;
    public bool Selected { get => selected; set { selected = value && CanRemove; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public bool CanRemove => !SystemPackage;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class AppIcons
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    public static readonly ImageSource Fallback = MakeFallback();
    private static ImageSource MakeFallback()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(63, 84, 104)), null, new RectangleGeometry(new Rect(1, 3, 30, 26), 6, 6)));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(153, 194, 213)), null, Geometry.Parse("M7,9 L25,9 25,11 7,11Z M7,15 L13,15 13,23 7,23Z M17,15 L25,15 25,17 17,17Z M17,21 L23,21 23,23 17,23Z")));
        var image = new DrawingImage(group); image.Freeze(); return image;
    }
    public static ImageSource Read(InstalledApp app)
    {
        // Store apps ship PNG logos instead of icon resources.
        if (app.DisplayIcon.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(app.DisplayIcon))
        {
            try
            {
                var logo = new BitmapImage(); logo.BeginInit(); logo.CacheOption = BitmapCacheOption.OnLoad; logo.DecodePixelWidth = 32; logo.UriSource = new Uri(app.DisplayIcon); logo.EndInit();
                logo.Freeze(); return logo;
            }
            catch (Exception e) when (e is IOException or NotSupportedException or UriFormatException or ArgumentException or InvalidOperationException) { return Fallback; }
        }
        foreach (var candidate in Candidates(app))
        {
            IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
            try
            {
                if (!Path.IsPathFullyQualified(candidate.Path) || candidate.Path.StartsWith(@"\\") || !File.Exists(candidate.Path)) continue;
                ExtractIconEx(candidate.Path, candidate.Index, out large, out small, 1);
                var handle = large != IntPtr.Zero ? large : small;
                if (handle == IntPtr.Zero) continue;
                var image = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                image.Freeze(); return image;
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or ExternalException) { }
            finally { if (large != IntPtr.Zero) DestroyIcon(large); if (small != IntPtr.Zero && small != large) DestroyIcon(small); }
        }
        return Fallback;
    }
    private static IEnumerable<(string Path, int Index)> Candidates(InstalledApp app)
    {
        string display = Environment.ExpandEnvironmentVariables(app.DisplayIcon.Trim());
        if (display.Length > 0)
        {
            var match = Regex.Match(display, @"^(.*?)(?:,\s*(-?\d+))?$");
            int.TryParse(match.Groups[2].Value, out int index);
            yield return (match.Groups[1].Value.Trim().Trim('"'), index);
        }
        string[] executables = [];
        try
        {
            if (Path.IsPathFullyQualified(app.Location) && !app.Location.StartsWith(@"\\") && Directory.Exists(app.Location))
                executables = Directory.GetFiles(app.Location, "*.exe", SearchOption.TopDirectoryOnly)
                    .Where(p => !Regex.IsMatch(Path.GetFileName(p), "unins|setup|update|crash|helper", RegexOptions.IgnoreCase))
                    .OrderByDescending(p => Safety.Normalize(app.Name).Contains(Safety.Normalize(Path.GetFileNameWithoutExtension(p))))
                    .Take(4).ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        foreach (string exe in executables) yield return (exe, 0);
    }
}
