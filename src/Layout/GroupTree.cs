namespace Expanse;

public enum TreeRowKind { Group, App, Others }
public readonly record struct TreeApp(long Key, string Name, string Title);
public readonly record struct TreeGroup(long Id, string Name, bool Collapsed, IReadOnlyList<TreeApp> Apps);
public readonly record struct TreeRow(TreeRowKind Kind, long Key, string Text, int Depth, bool Expandable, bool Expanded, int Count);

/// The groups panel, zoomed out at the right: what the tree shows, what a search leaves of it, and
/// where things are. Sizes at a display scale of 1.
public static class GroupTree
{
    public const double Width = 280, Margin = 16, RowHeight = 28, Indent = 16, IconSize = 16;
    /// The minimap's height and the gap above it, which the panel stops short of.
    public const double BottomReserve = 148;
    /// The panel above its list (the search bar) and below it.
    const double ListTop = 48, ListBottom = 8;
    public const string OthersName = "Other apps";

    public static string AppText(string name, string title) =>
        title.Length == 0 || title == name ? name : $"{name} — {title}";

    /// Every row showing, top down: groups by name, then the apps in no group. Apps sit in name
    /// order rather than the order they were last used, so rows hold still.
    ///
    /// A search keeps an app when every word is somewhere in its group's name, its own name or its
    /// title, so a group's name finds all of its apps and "work code" the Code window in Work. A
    /// group stays for its name or for an app it kept, opened whether or not it was collapsed.
    public static List<TreeRow> Rows(IReadOnlyList<TreeGroup> groups, IReadOnlyList<TreeApp> others, bool othersCollapsed, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool searching = words.Length > 0;
        var rows = new List<TreeRow>();
        void Add(TreeRowKind kind, long key, string name, bool collapsed, IEnumerable<TreeApp> apps, bool alwaysShown)
        {
            var kept = apps.Where(a => !searching || Matches($"{name} {a.Name} {a.Title}", words))
                .OrderBy(a => AppText(a.Name, a.Title), StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Key).ToList();
            if (searching ? kept.Count == 0 && !Matches(name, words) : !alwaysShown && kept.Count == 0) return;
            bool open = searching || !collapsed;
            rows.Add(new TreeRow(kind, key, name, 0, kept.Count > 0, open && kept.Count > 0, kept.Count));
            if (open) rows.AddRange(kept.Select(a => new TreeRow(TreeRowKind.App, a.Key, AppText(a.Name, a.Title), 1, false, false, 0)));
        }
        foreach (var g in groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id))
            Add(TreeRowKind.Group, g.Id, g.Name, g.Collapsed, g.Apps, alwaysShown: true);
        Add(TreeRowKind.Others, -1, OthersName, othersCollapsed, others, alwaysShown: false);
        return rows;
    }

    static bool Matches(string text, string[] words) => words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// The row Enter goes to as you search: the first whose own text has every word, otherwise the
    /// first app (a word may be in its group's name and another in its own). -1: no rows.
    public static int BestRow(IReadOnlyList<TreeRow> rows, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < rows.Count; i++)
            if (words.Length > 0 && Matches(rows[i].Text, words)) return i;
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Kind == TreeRowKind.App) return i;
        return rows.Count > 0 ? 0 : -1;
    }

    /// On the right, from the top of the view and only as long as its rows (one if there are none,
    /// for the message that says so). A long list stops just above the minimap and scrolls.
    public static RectD Panel(Vec view, double k, int rows)
    {
        double w = Width * k, y = Margin * k;
        double most = view.Y - (Margin + BottomReserve) * k - y;
        double fits = (ListTop + ListBottom + Math.Max(rows, 1) * RowHeight) * k;
        return new RectD(view.X - Margin * k - w, y, w, Math.Min(fits, most));
    }

    public static RectD SearchRect(RectD panel, double k) =>
        new(panel.X + 10 * k, panel.Y + 10 * k, panel.W - 20 * k, 30 * k);

    /// Where the text goes in the search bar: past its magnifying glass.
    public static RectD SearchTextRect(RectD search, double k) =>
        new(search.X + 28 * k, search.Y + 3 * k, search.W - 34 * k, search.H - 6 * k);

    public static RectD ListRect(RectD panel, double k) =>
        new(panel.X + 6 * k, panel.Y + ListTop * k, panel.W - 12 * k, panel.H - (ListTop + ListBottom) * k);

    /// The row at `p`, or -1.
    public static int RowAt(Vec p, RectD list, double rowHeight, double scroll, int count)
    {
        if (!list.Contains(p)) return -1;
        int i = (int)Math.Floor((p.Y - list.Y + scroll) / rowHeight);
        return i < count ? i : -1;
    }

    public static RectD RowRect(RectD list, double rowHeight, double scroll, int index) =>
        new(list.X, list.Y + index * rowHeight - scroll, list.W, rowHeight);

    public static double ClampScroll(double scroll, int count, double rowHeight, double height) =>
        Math.Clamp(scroll, 0, Math.Max(0, count * rowHeight - height));

    /// The least scrolling that shows row `index`.
    public static double ScrollToShow(int index, double scroll, double rowHeight, double height)
    {
        double top = index * rowHeight, bottom = top + rowHeight;
        if (top < scroll) return top;
        return bottom > scroll + height ? bottom - height : scroll;
    }

    /// Whether `p` is on a row's open/close arrow.
    public static bool OnExpander(Vec p, RectD row, int depth, double k)
    {
        double x = row.X + depth * Indent * k;
        return row.Contains(p) && p.X >= x && p.X < x + 24 * k;
    }

    /// The highlight moved up or down the rows; it stays at the ends. -1: there are none.
    public static int MoveHighlight(int current, int count, int by)
    {
        if (count == 0) return -1;
        return current < 0 ? (by > 0 ? 0 : count - 1) : Math.Clamp(current + by, 0, count - 1);
    }
}
