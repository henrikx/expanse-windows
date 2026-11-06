namespace Expanse;

/// A saved window's entry, as far as matching a window to it goes (LayoutStore.TakeMatch).
public readonly record struct SavedSpot(string App, string Class, string Title, double W, double H, double? PW, double? PH, long Hwnd, uint Pid);

/// A window looking for its entry. `Title` is the hashed key (LayoutStore.TitleKey).
public readonly record struct SpotSeeker(string App, string Class, string Title, SizeD Size, long Hwnd, uint Pid);

public static class SavedSpots
{
    /// The index of the entry for a window, or -1: the same window (same handle in the same
    /// process), then same app and title, then same app. `taken` says whether another open
    /// window, or one away and due back, owns a handle.
    public static int FindMatch(SpotSeeker w, IReadOnlyList<SavedSpot> pool, Func<long, uint, bool> taken)
    {
        int i = IndexOf(pool, e => e.Hwnd == w.Hwnd && e.Pid == w.Pid && SameApp(e, w));
        if (i >= 0) return i;
        // A window far smaller than an entry (an update or loading box, a small dialog) doesn't
        // take it: the app's main window, a moment later, does (see FindLarger).
        bool Free(SavedSpot e) => !taken(e.Hwnd, e.Pid) && !TooSmall(w.Size, e);
        i = IndexOf(pool, e => SameApp(e, w) && e.Title == w.Title && Free(e));
        return i >= 0 ? i : IndexOf(pool, e => SameApp(e, w) && Free(e));
    }

    /// The first free entry of the app's that `w` is too small to take.
    public static int FindLarger(SpotSeeker w, IReadOnlyList<SavedSpot> pool, Func<long, uint, bool> taken) =>
        IndexOf(pool, e => SameApp(e, w) && TooSmall(w.Size, e) && !taken(e.Hwnd, e.Pid));

    /// Under a quarter of the entry's area: not the same window coming back.
    public static bool TooSmall(SizeD size, SavedSpot e) =>
        (e.PW ?? e.W) is double w && (e.PH ?? e.H) is double h && w > 0 && h > 0 && size.W * size.H < 0.25 * w * h;

    public static bool SameApp(SavedSpot e, SpotSeeker w) =>
        string.Equals(e.App, w.App, StringComparison.OrdinalIgnoreCase) && e.Class == w.Class;

    static int IndexOf(IReadOnlyList<SavedSpot> pool, Func<SavedSpot, bool> test)
    {
        for (int i = 0; i < pool.Count; i++) if (test(pool[i])) return i;
        return -1;
    }
}
