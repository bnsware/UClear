using System.Collections.Concurrent;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Threading;

namespace UClear;
// Scanners post results from worker threads; the UI drains them once per second so long scans show partial results without flooding the dispatcher.
public sealed class LiveFeed<T> : IDisposable
{
    readonly ConcurrentQueue<T> queue = new();
    readonly DispatcherTimer timer;
    readonly Action<List<T>> deliver;
    readonly int maxPerTick;
    public LiveFeed(Action<List<T>> deliver, int maxPerTick = 5000)
    {
        this.deliver = deliver; this.maxPerTick = maxPerTick;
        timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Drain(this.maxPerTick), Dispatcher.CurrentDispatcher);
    }
    public void Post(T item) => queue.Enqueue(item);
    public void Drain(int max = int.MaxValue)
    {
        var batch = new List<T>();
        while (batch.Count < max && queue.TryDequeue(out var item)) batch.Add(item);
        if (batch.Count > 0) deliver(batch);
    }
    public void Dispose() => timer.Stop();
}
public static class GridSort
{
    // DataGrid clears its sort whenever ItemsSource changes; keep the user's choice, else apply the page default.
    public static void Bind(DataGrid grid, System.Collections.IEnumerable source, params SortDescription[] defaults)
    {
        // An open cell edit blocks re-sorting ("Sorting is not allowed during an AddNew or EditItem transaction").
        grid.CommitEdit(DataGridEditingUnit.Row, true); grid.CancelEdit();
        var current = grid.Items.SortDescriptions.ToList();
        var directions = grid.Columns.Select(c => c.SortDirection).ToList();
        grid.ItemsSource = source;
        if (grid.Items.SortDescriptions.Count == 0)
            foreach (var sort in current.Count > 0 ? current : defaults.ToList()) grid.Items.SortDescriptions.Add(sort);
        for (int i = 0; i < directions.Count && i < grid.Columns.Count; i++) grid.Columns[i].SortDirection = current.Count > 0 ? directions[i] : null;
    }
}
