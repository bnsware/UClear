using System.ComponentModel;

namespace UClear;

public sealed class CleanupChoice : INotifyPropertyChanged
{
    public Finding Finding { get; }
    private bool selected = true;
    public bool Selected { get => selected; set { selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public CleanupChoice(Finding finding) => Finding = finding;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class CleanupSelection
{
    public List<CleanupChoice> Choices { get; }
    public List<Finding> Selected => Choices.Where(c => c.Selected).Select(c => c.Finding).ToList();
    public CleanupSelection(IEnumerable<Finding> candidates) => Choices = CleanupPolicy.Eligible(candidates).Select(f => new CleanupChoice(f)).ToList();
    public void SelectAll(bool value) { foreach (var choice in Choices) choice.Selected = value; }
    public string? Conflict
    {
        get
        {
            foreach (var parent in Choices.Where(c => c.Selected))
            foreach (var child in Choices.Where(c => !c.Selected))
            {
                bool nested = parent.Finding.Kind == "Klasör" && child.Finding.Kind == "Klasör" && Safety.IsWithin(child.Finding.Path, parent.Finding.Path);
                bool registry = parent.Finding.Kind is "Registry" or "Kaldırma kaydı" && child.Finding.Kind is "Registry" or "Registry başvurusu" && parent.Finding.View == child.Finding.View &&
                    (child.Finding.Path.StartsWith(parent.Finding.Path + "\\", StringComparison.OrdinalIgnoreCase) || child.Finding.Path.Equals(parent.Finding.Path, StringComparison.OrdinalIgnoreCase));
                if (nested || registry) return L.T("Seçmediğin bir öğe, seçili üst klasör/kaydın içinde. Onu korumak için üst öğenin de seçimini kaldır: ", "An item you did not select is inside a selected parent folder/key. To keep it, also unselect the parent: ") + parent.Finding.DisplayPath;
            }
            return null;
        }
    }
}
