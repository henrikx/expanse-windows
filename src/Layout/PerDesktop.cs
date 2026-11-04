namespace Expanse;

/// Things that belong to one virtual desktop. The list everything works on holds the current
/// desktop's, and this keeps the others' until you go back. While the current desktop can't be
/// told, the list stays as it is.
public sealed class PerDesktop<T>
{
    readonly Dictionary<Guid, List<T>> away = new();

    /// The desktop the list is for (null: not known yet).
    public Guid? Here { get; private set; }

    /// Everything on the other desktops.
    public IEnumerable<(Guid Desktop, T Item)> Away => away.SelectMany(a => a.Value.Select(item => (a.Key, item)));

    /// Starts again, with the list being for desktop `here`.
    public void Reset(Guid? here)
    {
        away.Clear();
        Here = here;
    }

    /// Keeps `item` for another desktop.
    public void Put(Guid desktop, T item)
    {
        if (!away.TryGetValue(desktop, out var list)) away[desktop] = list = new();
        list.Add(item);
    }

    /// Brings `items` to the desktop you're on. What belonged to a desktop that's gone comes here,
    /// as Windows moves that desktop's windows to another. True if `items` changed.
    public bool Follow(List<T> items, IReadOnlyCollection<Guid> desktops, Guid? current)
    {
        if (current is not Guid now) return false;
        bool changed = false;
        if (Here != now)
        {
            if (Here is Guid old)
            {
                if (items.Count > 0) away[old] = items.ToList();
                changed = items.Count > 0;
                items.Clear();
            }
            Here = now;
            if (away.Remove(now, out var back))
            {
                items.AddRange(back);
                changed = true;
            }
        }
        foreach (var gone in away.Keys.Where(d => !desktops.Contains(d)).ToList())
        {
            items.AddRange(away[gone]);
            away.Remove(gone);
            changed = true;
        }
        return changed;
    }
}
