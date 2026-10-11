using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace UClear;
// Microsoft Store (MSIX/AppX) apps of the current user, read from the package repository and each package's manifest.
public static class StoreApps
{
    const string Repository = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
    // Packages Windows itself relies on: removing them breaks the Store, app installation, sign-in or Windows Security.
    static readonly string[] Essential = ["Microsoft.WindowsStore", "Microsoft.DesktopAppInstaller", "Microsoft.StorePurchaseApp", "Microsoft.SecHealthUI", "Microsoft.Windows.", "MicrosoftWindows.", "Windows.", "Microsoft.UI.Xaml", "Microsoft.VCLibs", "Microsoft.NET.", "Microsoft.WindowsAppRuntime", "Microsoft.Services.Store", "Microsoft.AAD.", "Microsoft.AccountsControl", "Microsoft.LockApp", "Microsoft.Win32WebViewHost", "Microsoft.CredDialogHost", "Microsoft.ECApp", "Microsoft.AsyncTextService", "Microsoft.BioEnrollment", "Microsoft.Advertising", "NcsiUwpApp", "Microsoft.ApplicationCompatibilityEnhancements", "Microsoft.WidgetsPlatformRuntime"];
    // Language packs (display language), codecs (photos/videos stop opening), shared runtimes and the winget source.
    static readonly string[] EssentialParts = ["LanguageExperiencePack", "ImageExtension", "VideoExtension", "MediaExtensions", "WinAppRuntime", "Winget.Source", "StartExperiencesApp"];
    public static bool IsEssential(string identity, string root) =>
        Essential.Any(prefix => identity.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) || EssentialParts.Any(part => identity.Contains(part, StringComparison.OrdinalIgnoreCase)) ||
        Safety.IsWithin(root, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    public static List<InstalledApp> Read(List<string>? warnings = null)
    {
        var result = new List<InstalledApp>();
        try
        {
            using var packages = Registry.CurrentUser.OpenSubKey(Repository);
            foreach (string fullName in packages?.GetSubKeyNames() ?? [])
            {
                try
                {
                    using var key = packages!.OpenSubKey(fullName);
                    if (key?.GetValue("PackageRootFolder") is not string root || !File.Exists(Path.Combine(root, "AppxManifest.xml"))) continue;
                    var app = FromManifest(fullName, root, key.GetValue("DisplayName") as string);
                    if (app != null) result.Add(app);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException or System.Security.SecurityException) { warnings?.Add(fullName + ": " + e.Message); }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { warnings?.Add(L.T("Store paketleri okunamadı: ", "Store packages could not be read: ") + e.Message); }
        // Staged older versions can sit next to the current one; list each app once.
        return result.GroupBy(a => a.PackageFullName.Split('_')[0], StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(a => Version.TryParse(a.Version, out var v) ? v : new Version()).First()).ToList();
    }
    static InstalledApp? FromManifest(string fullName, string root, string? registryName)
    {
        using var reader = System.Xml.XmlReader.Create(Path.Combine(root, "AppxManifest.xml"), new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
        var xml = XDocument.Load(reader);
        XElement? Element(string name) => xml.Descendants().FirstOrDefault(e => e.Name.LocalName == name);
        var properties = Element("Properties");
        string Property(string name) => properties?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? "";
        // Frameworks, resource packs and packages without a launchable app are not something a user uninstalls.
        if (Property("Framework").Equals("true", StringComparison.OrdinalIgnoreCase) || Property("ResourcePackage").Equals("true", StringComparison.OrdinalIgnoreCase)) return null;
        if (!xml.Descendants().Any(e => e.Name.LocalName == "Application")) return null;
        var identity = Element("Identity");
        string id = identity?.Attribute("Name")?.Value ?? fullName.Split('_')[0];
        string name = Resolve(registryName, fullName, id) ?? Resolve(Property("DisplayName"), fullName, id) ?? id;
        if (name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) name = id;
        string publisher = Resolve(Property("PublisherDisplayName"), fullName, id) ?? "";
        string installed = "";
        try { installed = Directory.GetCreationTime(root).ToString("yyyyMMdd"); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return new(name, publisher, identity?.Attribute("Version")?.Value ?? "", root, "", "", "Store", 0, installed, Logo(root, Property("Logo")), "", fullName, IsEssential(id, root));
    }
    // "@{Package?ms-resource://...}" strings are resolved by Windows itself.
    static string? Resolve(string? value, string fullName, string identity)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string source = value;
        if (value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
        {
            string resource = value["ms-resource:".Length..];
            string uri = resource.StartsWith("//") ? "ms-resource:" + resource : resource.Contains('/') ? $"ms-resource://{identity}/{resource.TrimStart('/')}" : $"ms-resource://{identity}/Resources/{resource}";
            source = $"@{{{fullName}?{uri}}}";
        }
        if (!source.StartsWith('@')) return value;
        var text = new StringBuilder(512);
        return SHLoadIndirectString(source, text, text.Capacity, IntPtr.Zero) == 0 && text.Length > 0 ? text.ToString() : null;
    }
    // Manifests name "Assets\Logo.png" but ship scaled variants like "Logo.scale-100.png".
    static string Logo(string root, string logo)
    {
        if (logo.Length == 0) return "";
        try
        {
            string path = Path.Combine(root, logo);
            if (File.Exists(path)) return path;
            string? folder = Path.GetDirectoryName(path);
            if (folder == null || !Directory.Exists(folder)) return "";
            return Directory.EnumerateFiles(folder, Path.GetFileNameWithoutExtension(path) + ".*" + Path.GetExtension(path))
                .OrderBy(f => f.Contains("scale-100", StringComparison.OrdinalIgnoreCase) ? 0 : f.Contains("targetsize-32", StringComparison.OrdinalIgnoreCase) ? 1 : 2).FirstOrDefault() ?? "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return ""; }
    }
    public static bool Installed(string fullName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Repository + "\\" + fullName);
        return key != null;
    }
    // Windows' own Remove-AppxPackage removes the package and its data for this user; nothing else is touched.
    public static async Task<string> Remove(InstalledApp app, CancellationToken token)
    {
        if (!app.IsStore || app.SystemPackage) throw new InvalidOperationException(L.T("Bu paket Windows'a ait; kaldırılmaz.", "This package belongs to Windows; it is not removed."));
        if (!Regex.IsMatch(app.PackageFullName, @"^[\w.\-~]+$")) throw new InvalidOperationException(L.T("Paket adı doğrulanamadı.", "The package name could not be verified."));
        string id = app.PackageFullName.Split('_')[0];
        if (IsEssential(id, app.Location)) throw new InvalidOperationException(L.T("Bu paket Windows'a ait; kaldırılmaz.", "This package belongs to Windows; it is not removed."));
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", $"Remove-AppxPackage -Package '{app.PackageFullName}' -ErrorAction Stop" }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException(L.T("PowerShell başlatılamadı.", "PowerShell could not be started."));
        var error = process.StandardError.ReadToEndAsync(token); _ = process.StandardOutput.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        if (Installed(app.PackageFullName)) throw new IOException(L.T("Paket kaldırılamadı: ", "The package could not be removed: ") + (await error).Trim());
        return "Kaldırıldı";
    }
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int SHLoadIndirectString(string source, StringBuilder output, int size, IntPtr reserved);
}
