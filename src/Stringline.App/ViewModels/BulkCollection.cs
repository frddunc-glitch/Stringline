using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Stringline.App.ViewModels;

/// <summary>An observable collection whose items can be replaced all at once, with one reset rather than a change per item.</summary>
internal sealed class BulkCollection<T> : ObservableCollection<T>
{
    /// <summary>Replaces the items, unless they are already these, in this order.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        var list = items.ToList();
        if (list.SequenceEqual(Items))
        {
            return;
        }

        Items.Clear();
        foreach (var item in list)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
