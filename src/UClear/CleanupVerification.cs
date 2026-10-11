using System.IO;

namespace UClear;
public static class CleanupVerification
{
    public static string Check(Finding item)
    {
        try
        {
            bool exists;
            if (item.Kind == "Klasör")
            {
                try { File.GetAttributes(item.Path); exists = true; }
                catch (FileNotFoundException) { exists = false; }
                catch (DirectoryNotFoundException) { exists = false; }
            }
            else
            {
                var (root, subkey) = Cleaner.OpenRegistry(item.Path, item.View);
                using (root)
                using (var key = root.OpenSubKey(subkey))
                    exists = key != null && (item.ValueName == null || key.GetValueNames().Contains(item.ValueName, StringComparer.OrdinalIgnoreCase));
            }
            return exists ? "Hâlâ mevcut" : "Silindi · doğrulandı";
        }
        catch (Exception error) { return "Doğrulanamadı: " + error.Message; }
    }
}
