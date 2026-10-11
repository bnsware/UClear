using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UClear;

public sealed partial record InstalledApp(string Name, string Publisher, string Version, string Location,
    string UninstallCommand, string RegistryPath, string View, long EstimatedBytes, string InstallDate, string DisplayIcon = "",
    string QuietUninstallCommand = "", string PackageFullName = "", bool SystemPackage = false)
{
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();
    public string Detail => string.Join("  ·  ", new[] { Publisher, Version }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public string SizeLabel => EstimatedBytes > 0 ? Finding.FormatBytes(EstimatedBytes) : "—";
    public bool IsStore => PackageFullName.Length > 0;
    public DateTime? InstalledOn => DateTime.TryParseExact(InstallDate, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) ? date : null;
    // Second line in the app list: who made it, how big, when installed.
    public string Subtitle => string.Join(" · ", new[] { IsStore ? "Store" : "", Publisher, EstimatedBytes > 0 ? SizeLabel : "", InstalledOn?.ToString("dd.MM.yyyy") ?? "" }.Where(x => x.Length > 0));
    // Identity, not content: list selection and check state must survive icon loads and other updates.
    public bool Equals(InstalledApp? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}

public sealed class Finding : INotifyPropertyChanged
{
    public required string Kind { get; init; }
    public required string Path { get; init; }
    public required string Reason { get; init; }
    public required string Confidence { get; init; }
    public string View { get; init; } = "";
    public string? ValueName { get; init; }
    public string? ExpectedValue { get; init; }
    public int ExpectedValueKind { get; init; }
    public string DisplayPath => ValueName == null ? Path : $"{Path} → {(ValueName.Length == 0 ? L.T("(Varsayılan)", "(Default)") : ValueName)}";
    public bool CanDelete { get; init; }
    public long Bytes { get; init; }
    public string SizeText => Bytes > 0 ? FormatBytes(Bytes) : "—";
    // Third line in the results list: size for folders, Registry view for keys (the same path can exist in both views).
    public string Note => Bytes > 0 ? SizeText : View == "Registry32" ? L.T("32 bit kayıt", "32-bit entry") : View == "Registry64" ? L.T("64 bit kayıt", "64-bit entry") : "—";
    // Default list order: safest removals first, review-only items last.
    public int Rank => !CanDelete ? 2 : Confidence is "Belirsiz" or "Paylaşılan" ? 1 : 0;
    private bool selected;
    public bool Selected { get => selected; set { selected = value; OnPropertyChanged(); } }
    private string result = "İnceleme bekliyor";
    public string Result { get => result; set { result = value; OnPropertyChanged(); } }
    public static string FormatBytes(long bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824d:0.0} GB" : bytes >= 1048576 ? $"{bytes / 1048576d:0.0} MB" : $"{bytes / 1024d:0.0} KB";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed record Target(string Name, string Location = "", string RegistryPath = "", string View = "", string ExecutablePath = "", string[]? ReferenceDirectories = null);
public sealed record ScanResult(List<Finding> Findings, List<string> Warnings, int LocationsChecked, Target? ResolvedTarget = null);
public sealed record HistoryEntry(DateTimeOffset Time, string App, string Action, string Detail, string? ItemPath = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string CopyText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ItemPath)) return ItemPath;
            if (Action is not ("Silindi" or "Silinemedi")) return Detail;
            string text = Detail;
            if (Action == "Silinemedi")
            {
                int separator = text.IndexOf(" — ", StringComparison.Ordinal);
                if (separator >= 0) text = text[..separator];
            }
            return System.Text.RegularExpressions.Regex.Replace(text, @"\s+\[(?:Registry32|Registry64)?\]$", "");
        }
    }
}

public static class CleanupPolicy
{
    public static List<Finding> Eligible(IEnumerable<Finding> findings) => findings.Where(f =>
        f.CanDelete && f.Kind is "Klasör" or "Registry" or "Registry başvurusu" or "Kaldırma kaydı" &&
        f.Confidence is not ("Belirsiz" or "Paylaşılan") && f.Result != "Silindi · doğrulandı").ToList();
}

public static class UninstallOutcome
{
    public static bool NeedsRestart(bool isMsi, int code) => isMsi && code is 3010 or 1641;
    public static bool KnownFailure(bool isMsi, int code) => isMsi && code != 0 && !NeedsRestart(isMsi, code);
    public static bool NeedsConfirmation(int code, bool registrationExists) => code != 0 || registrationExists;
}

// Runs on the reporting thread, unlike Progress<T>, so scanners can flush their own state from it.
public sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
public sealed record ScanProgress(long Count, long? Total, string Stage)
{
    public double Percent => Total > 0 ? Math.Min(100, Count * 100d / Total.Value) : 0;
    public string Text => Total.HasValue ? $"{L.V(Stage)} · {L.T($"%{Percent:0}", $"{Percent:0}%")} · {Count:N0}/{Total:N0}" : $"{L.V(Stage)} · {Count:N0} {L.T("öğe · Toplam keşfediliyor…", "items · counting total…")}";
}
