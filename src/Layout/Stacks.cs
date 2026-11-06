namespace Expanse;

/// Windows lying inside another: `Outer` is the largest, `Members` are the tabs (the outer one too)
/// in an order that doesn't change as they're raised, and `Front` is the one on top. All are indexes
/// into the frames.
public sealed record WindowStack(int Outer, int[] Members, int Front);

/// The rules of stacks on the canvas. A stack is worked out from where windows are, like a group,
/// so there is no list of members to keep: drop a window inside another and they're stacked. Which
/// is in front doesn't decide it, or the tabs would go when you raised a small window from behind
/// a big one.
public static class Stacks
{
    /// How much of a window's area must lie within another for it to count as inside. Not all of it:
    /// a window a little taller than the one it's on could otherwise never join it.
    public const double InsideShare = 0.85;
    /// A tab's widest and, however many there are, narrowest, and the room round them, at a display scale of 1.
    public const double TabMaxWidth = 200, TabMinWidth = 36, TabGap = 4, TabDrop = 8;
    /// What an icon adds to a tab's width: the icon and the space before its text.
    public const double TabIconRoom = GroupTree.IconSize + TabIconGap, TabIconGap = 5;

    /// What the fit button (a round one, as tall as a tab) takes up to the left of the tabs: pass to TabRects.
    public static double FitRoom(double k) => Math.Round(Groups.LabelHeight * k) + Math.Round(TabGap * k);

    /// The fit button, to the left of the first tab.
    public static RectD FitRect(RectD firstTab, double k) => new(firstTab.X - FitRoom(k), firstTab.Y, firstTab.H, firstTab.H);

    /// Where the fit button puts a window whose app gave it `size`: at the same corner as the front
    /// window, so one that won't take the size still lines up, and stays in the stack.
    public static RectD Fitted(RectD front, SizeD size) => new(front.X, front.Y, size.W, size.H);

    /// `frames` bottom first. `ids` stay the same for a window, and put the tabs in their order.
    /// The largest window takes everything inside it, so nested windows all join the outermost.
    /// Of equal sizes, the one in front is the outer one.
    public static List<WindowStack> Find(IReadOnlyList<RectD> frames, IReadOnlyList<long> ids)
    {
        var stacks = new List<WindowStack>();
        var taken = new HashSet<int>();
        var largestFirst = Enumerable.Range(0, frames.Count).OrderByDescending(i => frames[i].W * frames[i].H).ThenByDescending(i => i).ToList();
        foreach (int outer in largestFirst)
        {
            if (taken.Contains(outer)) continue;
            var inside = largestFirst.Where(i => i != outer && !taken.Contains(i) && IsInside(frames[i], frames[outer])).ToList();
            if (inside.Count == 0) continue;
            taken.Add(outer);
            taken.UnionWith(inside);
            inside.Add(outer);
            stacks.Add(new WindowStack(outer, inside.OrderBy(i => ids[i]).ToArray(), inside.Max()));
        }
        return stacks;
    }

    /// The stacks whose tabs show at 100%: the one you're in (`focused`, an index, or -1), and any in
    /// the same canvas group as it. `groupOf`: each window's group (-1: none). `focusedGroup`: the
    /// focused window's, which may not be among the frames (a maximised one).
    public static List<WindowStack> ShowingAt100(IReadOnlyList<WindowStack> stacks, int focused, IReadOnlyList<int> groupOf, int focusedGroup) =>
        stacks.Where(s => s.Members.Contains(focused) || (focusedGroup >= 0 && s.Members.Any(m => groupOf[m] == focusedGroup))).ToList();

    /// Enough of `w` lies within `outer`.
    public static bool IsInside(RectD w, RectD outer)
    {
        double across = Math.Min(w.Right, outer.Right) - Math.Max(w.X, outer.X), down = Math.Min(w.Bottom, outer.Bottom) - Math.Max(w.Y, outer.Y);
        return across > 0 && down > 0 && across * down >= InsideShare * w.W * w.H;
    }

    /// The tabs of a stack, in view space, in a row just below `viewBounds`, all of its windows: the
    /// same size at any zoom, narrower where they wouldn't fit its width, and always on screen.
    /// On whole pixels, or their text would blur. `lead`: room to leave before the first tab.
    public static RectD[] TabRects(RectD viewBounds, IReadOnlyList<double> textWidths, RectD view, double k, double lead = 0)
    {
        int n = textWidths.Count;
        if (n == 0) return [];
        double h = Math.Round(Groups.LabelHeight * k), gap = Math.Round(TabGap * k), gaps = gap * (n - 1);
        var widths = textWidths.Select(t => Math.Min(t + 2 * Groups.LabelPad * k, TabMaxWidth * k)).ToArray();
        if (lead + widths.Sum() + gaps > viewBounds.W)
        {
            double even = (viewBounds.W - lead - gaps) / n;
            widths = widths.Select(w => Math.Max(Math.Min(w, even), TabMinWidth * k)).ToArray();
        }
        double total = lead + widths.Sum() + gaps;
        double x = Math.Round(Math.Clamp(viewBounds.X, view.X, Math.Max(view.X, view.Right - total))) + lead;
        double y = Math.Round(Math.Clamp(viewBounds.Bottom + TabDrop * k, view.Y, Math.Max(view.Y, view.Bottom - h)));
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
