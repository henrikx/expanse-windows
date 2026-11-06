namespace Expanse;

/// Windows lying inside another: `Outer` holds the tab bar, `Members` are its tabs (the outer one
/// too) in an order that doesn't change as they're raised, and `Front` is the one on top. All are
/// indexes into the frames.
public sealed record WindowStack(int Outer, int[] Members, int Front);

/// The rules of stacks on the canvas. A stack is worked out from where windows are, like a group,
/// so there is no list of members to keep: drop a window inside another and they're stacked. Which
/// is in front doesn't decide it, or the tabs would go when you raised a small window from behind
/// a big one.
public static class Stacks
{
    /// How far a window may stick out of another and still count as inside it, at a display scale of 1.
    public const double Tolerance = 16;
    /// A tab's widest and, however many there are, narrowest, and the room round them, at a display scale of 1.
    public const double TabMaxWidth = 200, TabMinWidth = 36, TabGap = 4, TabDrop = 8;
    /// What an icon adds to a tab's width: the icon and the space before its text.
    public const double TabIconRoom = GroupTree.IconSize + TabIconGap, TabIconGap = 5;

    /// `frames` bottom first. `ids` stay the same for a window, and put the tabs in their order.
    /// The largest window takes everything inside it, so nested windows all join the outermost.
    /// Of equal sizes, the one in front is the outer one.
    public static List<WindowStack> Find(IReadOnlyList<RectD> frames, IReadOnlyList<long> ids, double tolerance)
    {
        var stacks = new List<WindowStack>();
        var taken = new HashSet<int>();
        var largestFirst = Enumerable.Range(0, frames.Count).OrderByDescending(i => frames[i].W * frames[i].H).ThenByDescending(i => i).ToList();
        foreach (int outer in largestFirst)
        {
            if (taken.Contains(outer)) continue;
            var room = frames[outer].Inset(-tolerance, -tolerance);
            var inside = largestFirst.Where(i => i != outer && !taken.Contains(i) && room.Contains(frames[i])).ToList();
            if (inside.Count == 0) continue;
            taken.Add(outer);
            taken.UnionWith(inside);
            inside.Add(outer);
            stacks.Add(new WindowStack(outer, inside.OrderBy(i => ids[i]).ToArray(), inside.Max()));
        }
        return stacks;
    }

    /// The tabs of a stack, in view space, in a row just below the outer window `viewOuter`: the
    /// same size at any zoom, narrower where they wouldn't fit its width, and always on screen.
    /// On whole pixels, or their text would blur.
    public static RectD[] TabRects(RectD viewOuter, IReadOnlyList<double> textWidths, RectD view, double k)
    {
        int n = textWidths.Count;
        if (n == 0) return [];
        double h = Math.Round(Groups.LabelHeight * k), gap = Math.Round(TabGap * k), gaps = gap * (n - 1);
        var widths = textWidths.Select(t => Math.Min(t + 2 * Groups.LabelPad * k, TabMaxWidth * k)).ToArray();
        if (widths.Sum() + gaps > viewOuter.W)
        {
            double even = (viewOuter.W - gaps) / n;
            widths = widths.Select(w => Math.Max(Math.Min(w, even), TabMinWidth * k)).ToArray();
        }
        double total = widths.Sum() + gaps;
        double x = Math.Round(Math.Clamp(viewOuter.X, view.X, Math.Max(view.X, view.Right - total)));
        double y = Math.Round(Math.Clamp(viewOuter.Bottom + TabDrop * k, view.Y, Math.Max(view.Y, view.Bottom - h)));
        var tabs = new RectD[n];
        for (int i = 0; i < n; i++)
        {
            double w = Math.Floor(widths[i]);
            tabs[i] = new RectD(x, y, w, h);
            x += w + gap;
        }
        return tabs;
    }
}
