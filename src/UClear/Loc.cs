using System.IO;
using System.Text.Json;

namespace UClear;
// Every user-visible text is written in both languages at the place it is used: L.T("Türkçe", "English").
// Values the engine also uses as identifiers (kinds, results, categories…) stay Turkish internally and are translated only for display with L.V.
public static class L
{
    static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UClear", "settings.json");
    public static string Language { get; set; } = Load();
    public static bool En => Language == "en";
    public static string T(string tr, string en) => En ? en : tr;
    static string Load()
    {
        try
        {
            if (File.Exists(SettingsPath) && JsonDocument.Parse(File.ReadAllText(SettingsPath)).RootElement.TryGetProperty("language", out var value) && value.GetString() is "tr" or "en")
                return value.GetString()!;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        // First start: Turkish Windows gets Turkish, every other language gets English.
        return System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en";
    }
    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { language = Language }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    // Display text of engine identifiers. Unknown values are shown unchanged; "Prefix: details" values translate their prefix.
    public static string V(string? value)
    {
        if (string.IsNullOrEmpty(value) || !En) return value ?? "";
        if (Values.TryGetValue(value, out var exact)) return exact;
        foreach (var (prefix, english) in Prefixes)
            if (value.StartsWith(prefix, StringComparison.Ordinal)) return english + value[prefix.Length..];
        return value;
    }
    static readonly Dictionary<string, string> Values = new(StringComparer.Ordinal)
    {
        // Finding kinds
        ["Klasör"] = "Folder", ["Registry"] = "Registry", ["Registry başvurusu"] = "Registry reference", ["Kaldırma kaydı"] = "Uninstall entry",
        ["Servis / sürücü"] = "Service / driver", ["Zamanlanmış görev"] = "Scheduled task",
        // Confidence
        ["Güçlü eşleşme"] = "Strong match", ["Belirsiz"] = "Uncertain", ["Paylaşılan"] = "Shared", ["Doğrudan kayıt"] = "Direct entry",
        ["İçerik başvurusu"] = "Content reference", ["Yol başvurusu"] = "Path reference",
        // Results
        ["İnceleme bekliyor"] = "Awaiting review", ["Silindi · doğrulandı"] = "Deleted · verified", ["Hâlâ mevcut"] = "Still present",
        ["Atlandı · inceleme gerekli"] = "Skipped · needs review", ["Silindi"] = "Deleted", ["Doğrulanamadı"] = "Could not verify",
        // Invalid reference categories
        ["Başlangıç"] = "Startup", ["Kaldırma"] = "Uninstall", ["Uygulama yolu"] = "App path", ["Servis"] = "Service",
        ["Başlangıç durumu"] = "Startup state", ["Kısayol"] = "Shortcut", ["Görev"] = "Task",
        // Invalid reference states
        ["Hedef mevcut"] = "Target exists", ["Boş komut; inceleme gerekli"] = "Empty command; needs review", ["Eksik tırnak; komut çözümlenemedi"] = "Missing quote; command could not be parsed",
        ["Dolaylı veya çözümlenemeyen komut"] = "Indirect or unresolvable command", ["Tırnaksız boşluklu yol; belirsiz"] = "Unquoted path with spaces; ambiguous",
        ["Ağ, göreli veya çözümlenemeyen yol"] = "Network, relative or unresolvable path", ["Disk bağlı değil veya çıkarılabilir/ağ diski"] = "Drive not connected, or removable/network drive",
        ["Bağlantı üzerinden hedef; inceleme gerekli"] = "Target behind a link; needs review", ["Hedef bir klasör; inceleme gerekli"] = "Target is a folder; needs review",
        ["32/64 bit karşılığı mevcut; inceleme gerekli"] = "32/64-bit counterpart exists; needs review", ["Hedef yolu bulunamadı"] = "Target path not found",
        ["Hedef dosya bulunamadı"] = "Target file not found", ["Hedef dosya bulunamadı; klasörde program dosyası kalmamış"] = "Target file not found; no program files left in its folder",
        ["Başlangıç komutu silinmiş; devre dışı durum kaydı kalmış"] = "Startup command removed; a disabled state entry remains",
        ["Başlangıç komutu silinmiş; Windows durum kaydı kalmış"] = "Startup command removed; a Windows state entry remains",
        ["Başlangıç klasörü öğesi silinmiş; devre dışı durum kaydı kalmış"] = "Startup folder item removed; a disabled state entry remains",
        ["Başlangıç klasörü öğesi silinmiş; Windows durum kaydı kalmış"] = "Startup folder item removed; a Windows state entry remains",
        // Disk entries
        ["Dosya"] = "File", ["Boş klasör"] = "Empty folder",
        // Leftover candidates
        ["Program klasörü"] = "Program folder", ["Veri klasörü"] = "Data folder", ["Kayıtsız program"] = "Unregistered program",
        // Process kinds (lock inspection)
        ["Kritik"] = "Critical", ["Dosya Gezgini"] = "File Explorer", ["Uygulama"] = "Application", ["Konsol"] = "Console", ["Bilinmiyor"] = "Unknown",
        // History actions
        ["Silinemedi"] = "Not deleted", ["Tarama"] = "Scan", ["Kaldırıcı"] = "Uninstaller", ["Doğrulama"] = "Verification", ["Doğrulama özeti"] = "Verification summary",
        ["Silme başladı"] = "Deletion started", ["Store kaldırma"] = "Store removal", ["Zorla kaldırma"] = "Force removal", ["Toplu kaldırma"] = "Bulk removal",
        ["Kaydedildi"] = "Saved", ["Etkinleştirildi"] = "Enabled", ["Devre dışı"] = "Disabled", ["Kalıntı araması"] = "Leftover search",
        ["Geçersiz başvurular"] = "Invalid references", ["Araçlar"] = "Tools", ["Hosts"] = "Hosts",
        ["Kaldırıldı"] = "Removed", ["Başlatıldı"] = "Started",
    };
    static readonly (string Prefix, string English)[] Prefixes =
    [
        ("Silinemedi: ", "Not deleted: "), ("Atlandı: ", "Skipped: "), ("Doğrulanamadı: ", "Could not verify: "), ("Kaldırılamadı: ", "Could not remove: "),
    ];
}
