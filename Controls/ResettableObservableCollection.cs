using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace ZDesk.Controls;

internal sealed class ResettableObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IReadOnlyList<T> items)
    {
        if (Count == items.Count && this.SequenceEqual(items)) return;
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
