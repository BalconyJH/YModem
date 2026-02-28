using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace YModemWin;

/// <summary>
/// ObservableCollection with AddRange support to avoid multiple CollectionChanged events
/// </summary>
public class RangeObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// Adds multiple items and triggers a single reset notification.
    /// </summary>
    public void AddRange(IEnumerable<T> items)
    {
        if (items == null)
            throw new ArgumentNullException(nameof(items));

        CheckReentrancy();

        var hasNewItem = false;
        
        foreach (var item in items)
        {
            Items.Add(item);
            hasNewItem = true;
        }

        if (!hasNewItem)
        {
            return;
        }

        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
