using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace YModemWin;

/// <summary>
/// ObservableCollection with AddRange support to avoid multiple CollectionChanged events
/// </summary>
public class RangeObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// Adds multiple items without triggering any UI updates
    /// </summary>
    public void AddRangeSilent(IEnumerable<T> items)
    {
        if (items == null)
            throw new ArgumentNullException(nameof(items));

        CheckReentrancy();
        
        foreach (var item in items)
        {
            Items.Add(item);
        }
    }
}

