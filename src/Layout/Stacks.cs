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

    /// What the tabs take below a stack, with the same space again below them.
    public static double TabRoom(double k) => Math.Round(TabDrop * k) * 2 + Math.Round(Groups.LabelHeight * k);

    /// The frames for working out where to go to them: one in a stack reaches down past the stack's
    /// tabs, so they don't end up pushed over it at the bottom of the screen.
    public static RectD[] WithTabRoom(IReadOnlyList<RectD> frames, IReadOnlyList<long> ids, double k)
    {
        var roomy = frames.ToArray();
        foreach (var stack in Find(frames, ids))
        {
            double bottom = stack.Members.Max(i => frames[i].Bottom) + TabRoom(k);
            foreach (int i in stack.Members) roomy[i] = frames[i] with { H = bottom - frames[i].Y };
        }
        return roomy;
    }

    /// All of a stack with the room its tabs take below it.
    public static RectD BoxWithTabs(IReadOnlyList<RectD> frames, WindowStack stack, double k)
    {
        var box = stack.Members.Select(i => frames[i]).Aggregate((a, b) => a.Union(b));
        return box with { H = box.H + TabRoom(k) };
    }

    /// How much of a stack (with its tabs' room) must show for the view to move to fit its tabs. A
    /// stack barely on screen isn't what you came to see, and mustn't push the view away from what is.
    public const double MostlyShown = 0.75;

    /// The view's top-left (from Groups.FocusOrigin) moved on so that stacks mostly on screen have their
    /// tabs on it too, rather than pushed up over them. `stacks`: from BoxWithTabs. As far as that goes
    /// without taking the top of `focus` or of the stack off the screen. `work`: in view space.
    public static Vec RoomForTabs(Vec origin, IEnumerable<RectD> stacks, RectD? focus, RectD work)
    {
        double y = origin.Y;
        foreach (var s in stacks.OrderByDescending(s => s.Bottom))
        {
            var seen = s.Offset(new Vec(-origin.X, -y));
            if (!seen.Intersects(work)) continue;
            double shown = Math.Min(seen.Bottom, work.Bottom) - Math.Max(seen.Y, work.Y);
            if (shown < MostlyShown * s.H) continue;
            double over = seen.Bottom - work.Bottom, limit = seen.Y - work.Y;
            if (focus is RectD f) limit = Math.Min(limit, f.Y - y - work.Y);
            if (over > 0 && limit > 0) y += Math.Min(over, limit);
        }
        return new Vec(origin.X, Math.Round(y));
    }

    /// The view's top-left at 100% for going to a canvas group, the whole of that decision. `members`:
    /// indexes into `frames`, the windows that aren't maximised. `focus`: one of them, or -1.
    /// `maximised`: the group's maximised windows' frames, which count towards its size. `box`: the group's, if it's empty.
    public static Vec GroupOrigin(IReadOnlyList<RectD> frames, IReadOnlyList<long> ids, IReadOnlyCollection<int> members, int focus,
        IReadOnlyCollection<RectD> maximised, RectD box, RectD work, double k)
    {
        var roomy = WithTabRoom(frames, ids, k);
        var sized = members.Select(i => roomy[i]).Concat(maximised).ToList();
        var bounds = sized.Count == 0 ? box : sized.Aggregate((a, b) => a.Union(b));
        RectD? seen = focus >= 0 ? roomy[focus] : null;
        var stacks = Find(frames, ids).Where(s => s.Members.Any(members.Contains)).Select(s => BoxWithTabs(frames, s, k));
        return RoomForTabs(Groups.FocusOrigin(bounds, seen, work), stacks, seen, work);
    }

    /// The stacks whose tabs show at 100%: the one you're in (`focused`, an index, or -1), and any in
    /// the same canvas group as it. `groupOf`: each window's group (-1: none). `focusedGroup`: the
    /// focused window's, which may not be among the frames (a maximised one).
    public static List<WindowStack> ShowingAt100(IReadOnlyList<WindowStack> stacks, int focused, IReadOnlyList<int> groupOf, int focusedGroup) =>
        stacks.Where(s => s.Members.Contains(focused) || (focusedGroup >= 0 && s.Members.Any(m => groupOf[m] == focusedGroup))).ToList();

    /// How far apart two windows' widths, and heights, may be for them to count as the same size.
    public const double SameSizeSlack = 16;

    /// `a` and `b` are about the same size, as after the fit button: resizing one of a stack's windows
    /// takes along only those, not ones of other sizes that lie in it.
    public static bool SameSize(RectD a, RectD b) =>
        Math.Abs(a.W - b.W) <= SameSizeSlack && Math.Abs(a.H - b.H) <= SameSizeSlack;

    /// Enough of `w` lies within `outer`.
    public static bool IsInside(RectD w, RectD outer)
    {
        double across = Math.Min(w.Right, outer.Right) - Math.Max(w.X, outer.X), down = Math.Min(w.Bottom, outer.Bottom) - Math.Max(w.Y, outer.Y);
        return across > 0 && down > 0 && across * down >= InsideShare * w.W * w.H;
    }

    /// The tabs of a stack, in view space, in a row just below `viewBounds`, all of its windows: the
    /// same size at any zoom, narrower where they wouldn't fit the part of it on `view`. They stay on
    /// the view, but don't overhang the stack for that: one mostly off a side (on another monitor,
    /// say) keeps its row by the edge of the part showing, even if that takes some of the row off.
    /// On whole pixels, or their text would blur. `lead`: room to leave before the first tab.
    public static RectD[] TabRects(RectD viewBounds, IReadOnlyList<double> textWidths, RectD view, double k, double lead = 0)
    {
        int n = textWidths.Count;
        if (n == 0) return [];
        double h = Math.Round(Groups.LabelHeight * k), gap = Math.Round(TabGap * k), gaps = gap * (n - 1);
        double left = Math.Max(viewBounds.X, view.X), right = Math.Min(viewBounds.Right, view.Right);
        var widths = textWidths.Select(t => Math.Min(t + 2 * Groups.LabelPad * k, TabMaxWidth * k)).ToArray();
        if (lead + widths.Sum() + gaps > right - left)
        {
            double even = (right - left - lead - gaps) / n;
            widths = widths.Select(w => Math.Max(Math.Min(w, even), TabMinWidth * k)).ToArray();
        }
        widths = widths.Select(Math.Floor).ToArray();
        double total = lead + widths.Sum() + gaps;
        double start = viewBounds.X < view.X ? Math.Min(left, right - total)
            : viewBounds.Right > view.Right ? left
            : Math.Min(left, Math.Max(view.X, view.Right - total));
        double x = Math.Round(start) + lead;
        double y = Math.Round(Math.Clamp(viewBounds.Bottom + TabDrop * k, view.Y, Math.Max(view.Y, view.Bottom - h)));
        var tabs = new RectD[n];
        for (int i = 0; i < n; i++)
        {
            tabs[i] = new RectD(x, y, widths[i], h);
            x += widths[i] + gap;
        }
        return tabs;
    }
}
