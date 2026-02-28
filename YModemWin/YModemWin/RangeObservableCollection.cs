using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;

namespace YModemWin;

/// <summary>
/// ObservableCollection with AddRange support to avoid multiple CollectionChanged events
/// </summary>
public class RangeObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// Adds multiple items and raises one Reset collection notification.
    /// WPF ListCollectionView does not support range Add notifications.
    /// </summary>
    public void AddRange(IEnumerable<T> items)
    {
        if (items == null)
            throw new ArgumentNullException(nameof(items));

        var pendingItems = items as IList<T> ?? items.ToList();
        if (pendingItems.Count == 0)
        {
            return;
        }

        CheckReentrancy();

        foreach (var item in pendingItems)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
