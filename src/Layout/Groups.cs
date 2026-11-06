namespace Expanse;

/// Which sides of a box a border drag moves. A corner is two of them.
[Flags]
public enum ResizeEdges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

/// Dragging a box's borders. Nothing here knows what the box is: a group now, a window later.
public static class Resizing
{
    /// The edges `p` is within `grab` of, reaching past the box by `grab` too. On a box too thin
    /// to tell its two sides apart, the nearer one.
    public static ResizeEdges EdgesAt(RectD box, Vec p, double grab)
    {
        if (p.X < box.X - grab || p.X > box.Right + grab || p.Y < box.Y - grab || p.Y > box.Bottom + grab) return ResizeEdges.None;
        static ResizeEdges Axis(double at, double low, double high, double grab, ResizeEdges lowEdge, ResizeEdges highEdge)
        {
            double toLow = Math.Abs(at - low), toHigh = Math.Abs(at - high);
            if (toLow > grab && toHigh > grab) return ResizeEdges.None;
            return toLow <= toHigh ? lowEdge : highEdge;
        }
        return Axis(p.X, box.X, box.Right, grab, ResizeEdges.Left, ResizeEdges.Right)
             | Axis(p.Y, box.Y, box.Bottom, grab, ResizeEdges.Top, ResizeEdges.Bottom);
    }

    /// `start` with `edges` moved by `delta`, never past `mustContain` or below `minimum`. The
    /// edges not being dragged stay exactly where they are.
    public static RectD Resize(RectD start, ResizeEdges edges, Vec delta, SizeD minimum, RectD? mustContain = null)
    {
        double left = start.X, top = start.Y, right = start.Right, bottom = start.Bottom;
        if (edges.HasFlag(ResizeEdges.Left))
        {
            left = Math.Min(left + delta.X, right - minimum.W);
            if (mustContain is RectD c) left = Math.Min(left, c.X);
        }
        if (edges.HasFlag(ResizeEdges.Right))
        {
            right = Math.Max(right + delta.X, left + minimum.W);
            if (mustContain is RectD c) right = Math.Max(right, c.Right);
        }
        if (edges.HasFlag(ResizeEdges.Top))
        {
            top = Math.Min(top + delta.Y, bottom - minimum.H);
            if (mustContain is RectD c) top = Math.Min(top, c.Y);
        }
        if (edges.HasFlag(ResizeEdges.Bottom))
        {
            bottom = Math.Max(bottom + delta.Y, top + minimum.H);
            if (mustContain is RectD c) bottom = Math.Max(bottom, c.Bottom);
        }
        return new RectD(left, top, right - left, bottom - top);
    }
}

public sealed record CreatedGroup(RectD Box, RectD[] Apps);
public sealed record SettledGroups(RectD[] Boxes, RectD[] Apps);

/// The rules of groups on the canvas, in canvas space. A group is a box: an app whose centre is
/// inside it is a member, so there is no list of members to keep. A member is always wholly
/// inside with `Padding` to spare, and boxes never come within `Gap` of each other.
public static class Groups
{
    public const double Padding = 24, Gap = Layout.Gap, MinWidth = 240, MinHeight = 160;
    /// The label, at a display scale of 1.
    public const double LabelPad = 10, LabelMaxWidth = 320, LabelHeight = 22, LabelLift = 6;

    public static bool IsMember(RectD box, RectD app) => box.Contains(app.Center);

    public static int[] Members(RectD box, IReadOnlyList<RectD> apps) =>
        Enumerable.Range(0, apps.Count).Where(i => IsMember(box, apps[i])).ToArray();

    /// The box exactly round `apps`, with its padding.
    public static RectD Fit(IEnumerable<RectD> apps) =>
        apps.Select(a => a.Inset(-Padding, -Padding)).Aggregate((a, b) => a.Union(b));

    /// New Group: a box round the selected apps. Others whose centre it would take in are moved
    /// just outside, so only what you picked is in it. Null if it would come too near another
    /// group, or with nothing selected.
    public static CreatedGroup? Create(IReadOnlyList<RectD> apps, IReadOnlyCollection<int> selected, IReadOnlyList<RectD> boxes)
    {
        if (selected.Count == 0) return null;
        var box = Fit(selected.Select(i => apps[i]));
        if (boxes.Any(b => box.Intersects(Zone(b)))) return null;
        var result = apps.ToArray();
        for (int i = 0; i < result.Length; i++)
            if (!selected.Contains(i) && IsMember(box, result[i])) result[i] = PushOut(result[i], box, boxes);
        return new CreatedGroup(box, result);
    }

    /// The shortest way wholly out of `box`, `Gap` clear of it, into no other group.
    public static RectD PushOut(RectD app, RectD box, IReadOnlyList<RectD> otherBoxes)
    {
        RectD[] ways =
        [
            app with { X = box.X - Gap - app.W }, app with { X = box.Right + Gap },
            app with { Y = box.Y - Gap - app.H }, app with { Y = box.Bottom + Gap },
        ];
        var nearest = ways.OrderBy(w => w.Origin.DistanceTo(app.Origin)).ToList();
        return nearest.FirstOrDefault(w => !otherBoxes.Any(b => IsMember(b, w)), nearest[0]);
    }

    /// Brings boxes and apps back to the rules after something moved (a drop, a group moved or
    /// enlarged, a window resized): every box grows to enclose its members, which can take in more
    /// apps in turn. Where another group stops a box growing, the app slides inside. One that
    /// can't fit snaps back to where its drag started (`dragStarts`), or is pushed out of the box.
    public static SettledGroups Settle(IReadOnlyList<RectD> boxes, IReadOnlyList<RectD> apps, IReadOnlyDictionary<int, RectD>? dragStarts = null)
    {
        var b = boxes.ToArray();
        var a = apps.ToArray();
        var starts = dragStarts == null ? new Dictionary<int, RectD>() : new Dictionary<int, RectD>(dragStarts);
        // Boxes only grow and each snap-back happens once, so this ends well before the limit.
        for (int pass = 0; pass < 200; pass++)
        {
            bool changed = false;
            for (int g = 0; g < b.Length; g++)
            {
                var others = b.Where((_, i) => i != g).ToList();
                var members = Members(b[g], a);
                var needed = members.Aggregate(b[g], (u, i) => u.Union(a[i].Inset(-Padding, -Padding)));
                if (needed == b[g]) continue;
                changed = true;
                var allowed = Grow(b[g], needed, others);
                int tooBig = Array.FindIndex(members, i => a[i].W + 2 * Padding > allowed.W || a[i].H + 2 * Padding > allowed.H);
                if (tooBig >= 0)
                {
                    int i = members[tooBig];
                    a[i] = starts.Remove(i, out var start) ? start : PushOut(a[i], allowed, others);
                    continue;
                }
                b[g] = allowed;
                var inside = allowed.Inset(Padding, Padding);
                foreach (int i in members)
                    a[i] = a[i] with
                    {
                        X = Math.Clamp(a[i].X, inside.X, inside.Right - a[i].W),
                        Y = Math.Clamp(a[i].Y, inside.Y, inside.Bottom - a[i].H),
                    };
            }
            if (!changed) break;
        }
        return new SettledGroups(b, a);
    }

    /// A group dragged to `proposed`, from `current`, stops `Gap` short of any other group it runs
    /// into and slides along it. Jumping clear past one is fine.
    public static RectD Move(RectD current, RectD proposed, IReadOnlyList<RectD> others)
    {
        var r = proposed;
        for (int pass = 0; pass <= others.Count; pass++)
            foreach (var o in others)
            {
                var z = Zone(o);
                if (!r.Intersects(z)) continue;
                var w = r;
                var ways = new List<RectD>();
                if (current.Right <= z.X) ways.Add(w with { X = z.X - w.W });
                if (current.X >= z.Right) ways.Add(w with { X = z.Right });
                if (current.Bottom <= z.Y) ways.Add(w with { Y = z.Y - w.H });
                if (current.Y >= z.Bottom) ways.Add(w with { Y = z.Bottom });
                if (ways.Count > 0) r = ways.OrderBy(c => c.Origin.DistanceTo(w.Origin)).First();
            }
        return others.Any(o => r.Intersects(Zone(o))) ? current : r;
    }

    /// `desired`, cut back on whichever side would come within `Gap` of another group. `current`
    /// (clear of them all) says which side that is; shrinking is never held back.
    public static RectD Grow(RectD current, RectD desired, IReadOnlyList<RectD> others)
    {
        var r = desired;
        foreach (var o in others)
        {
            var z = Zone(o);
            if (!r.Intersects(z)) continue;
            var w = r;
            var ways = new List<RectD>();
            if (current.Right <= z.X) ways.Add(w with { W = z.X - w.X });
            if (current.X >= z.Right) ways.Add(w with { X = z.Right, W = w.Right - z.Right });
            if (current.Bottom <= z.Y) ways.Add(w with { H = z.Y - w.Y });
            if (current.Y >= z.Bottom) ways.Add(w with { Y = z.Bottom, H = w.Bottom - z.Bottom });
            if (ways.Count > 0) r = ways.OrderByDescending(c => c.W * c.H).First();
        }
        return r;
    }

    /// A border drag from `start`, the pointer `delta` away. `current` is where the box is now;
    /// `required` the box round its members (null: it's empty, so its minimum size counts instead).
    public static RectD ResizeBox(RectD start, RectD current, ResizeEdges edges, Vec delta, RectD? required, IReadOnlyList<RectD> others)
    {
        var minimum = required == null ? new SizeD(MinWidth, MinHeight) : new SizeD(0, 0);
        return Grow(current, Resizing.Resize(start, edges, delta, minimum, required), others);
    }

    /// A marquee takes a group only when it covers the whole box.
    public static bool CoveredBy(RectD marquee, RectD box) => marquee.Contains(box);

    /// Groups dragged together: how far they go from `boxes`, as near to `wanted` as a step from
    /// `at` (how far they've gone already) allows. A step that would bring one within `Gap` of
    /// another group is dropped along whichever axis still works, so they slide along it, and
    /// jumping clear past one is fine.
    public static Vec MoveTogether(IReadOnlyList<RectD> boxes, Vec at, Vec wanted, IReadOnlyList<RectD> others)
    {
        bool Clear(Vec by) => !boxes.Any(b => others.Any(o => b.Offset(by).Intersects(Zone(o))));
        Vec[] steps = [wanted, new(wanted.X, at.Y), new(at.X, wanted.Y)];
        foreach (var step in steps)
            if (Clear(step)) return step;
        return at;
    }

    /// The canvas point for the view's top-left at 100%, when you click a group: its apps centred
    /// in the work area (`work`, view space), then shifted just enough that `focus`, the one you
    /// used last, is wholly on screen, or from its top-left if it's bigger than the screen.
    public static Vec FocusOrigin(RectD bounds, RectD? focus, RectD work)
    {
        double x = bounds.MidX - work.MidX, y = bounds.MidY - work.MidY;
        if (focus is RectD f)
        {
            if (f.W > work.W || f.X - x < work.X) x = f.X - work.X;
            else if (f.Right - x > work.Right) x = f.Right - work.Right;
            if (f.H > work.H || f.Y - y < work.Y) y = f.Y - work.Y;
            else if (f.Bottom - y > work.Bottom) y = f.Bottom - work.Bottom;
        }
        return new Vec(Math.Round(x), Math.Round(y));
    }

    /// Tidy Up with groups: each group's apps are tidied on their own and its box fitted to them,
    /// then every group and every ungrouped app are tidied as blocks. Moves `apps`; returns the
    /// boxes. Membership doesn't change. `membersOf`: who's in each box, when `apps` aren't what
    /// decides that (the home layout, on a screen where the canvas is a tightened version of it).
    public static RectD[] Tidy(RectD[] apps, IReadOnlyList<RectD> boxes, IReadOnlyList<int[]>? membersOf = null)
    {
        var members = membersOf ?? boxes.Select(b => Members(b, apps)).ToArray();
        var result = boxes.ToArray();
        for (int g = 0; g < result.Length; g++)
        {
            if (members[g].Length == 0) continue;
            var own = members[g].Select(i => apps[i]).ToArray();
            Layout.TidyUp(own);
            for (int k = 0; k < own.Length; k++) apps[members[g][k]] = own[k];
            result[g] = Fit(own);
        }
        var ungrouped = Enumerable.Range(0, apps.Length).Except(members.SelectMany(m => m)).ToArray();
        var blocks = result.Concat(ungrouped.Select(i => apps[i])).ToArray();
        var from = blocks.ToArray();
        Layout.TidyUp(blocks);
        ApplyBlocks(apps, result, members, ungrouped, from, blocks);
        return result;
    }

    /// After a fit to the screen moved and resized `apps` (they were `before`): each box follows
    /// its `members`, encloses them, and groups pushed into something move as blocks, members
    /// and all. `refit`: the box is fitted exactly (Tidy Up did the arranging). Moves `apps`;
    /// returns the boxes. Membership doesn't change.
    public static RectD[] AfterFit(IReadOnlyList<RectD> before, RectD[] apps, IReadOnlyList<RectD> boxes, IReadOnlyList<int[]> members, bool refit)
    {
        var result = boxes.ToArray();
        for (int g = 0; g < result.Length; g++)
        {
            var own = members[g];
            if (own.Length == 0) continue;
            var fit = Fit(own.Select(i => apps[i]));
            if (refit) { result[g] = fit; continue; }
            var shift = new Vec(own.Average(i => apps[i].X - before[i].X), own.Average(i => apps[i].Y - before[i].Y));
            result[g] = result[g].Offset(shift).Union(fit);
        }
        var ungrouped = Enumerable.Range(0, apps.Length).Except(members.SelectMany(m => m)).ToArray();
        // Each box with half the gap all round, so two pushed apart end up the gap clear. An app
        // already overlapping a box's edge before the fit may go on doing so.
        int count = result.Length;
        var blocks = result.Select(b => b.Inset(-Gap / 2, -Gap / 2)).Concat(ungrouped.Select(i => apps[i])).ToArray();
        var keep = new HashSet<(int, int)>();
        for (int g = 0; g < count; g++)
            for (int u = 0; u < ungrouped.Length; u++)
                if (boxes[g].Intersects(before[ungrouped[u]])) keep.Add(Layout.Pair(g, count + u));
        var from = blocks.ToArray();
        Layout.Separate(blocks, Layout.Gap, onlyOverlaps: true, keep);
        ApplyBlocks(apps, result, members, ungrouped, from, blocks);
        foreach (int i in ungrouped)
            for (int g = 0; g < count; g++)
                if (IsMember(result[g], apps[i])) apps[i] = PushOut(apps[i], result[g], result.Where((_, o) => o != g).ToList());
        return result;
    }

    /// The label above a box's top-left and the delete button to its right, in view space, at
    /// display scale `k`: the same size at any zoom.
    public static (RectD label, RectD trash) LabelRects(RectD viewBox, double textWidth, double k)
    {
        double h = Math.Round(LabelHeight * k), y = viewBox.Y - h - Math.Round(LabelLift * k);
        double w = Math.Min(textWidth + 2 * LabelPad * k, LabelMaxWidth * k);
        var label = new RectD(viewBox.X, y, w, h);
        return (label, new RectD(label.Right + Math.Round(4 * k), y, h, h));
    }

    /// The New Group button: where the label of the group it would make goes (`viewBox` is that
    /// group's box, in view space), but always on screen, to be clicked.
    public static RectD ButtonRect(RectD viewBox, double textWidth, RectD view, double k)
    {
        var (label, _) = LabelRects(viewBox, textWidth, k);
        return label with { X = Math.Clamp(label.X, view.X, view.Right - label.W), Y = Math.Clamp(label.Y, view.Y, view.Bottom - label.H) };
    }

    /// How near another group a box may not come.
    static RectD Zone(RectD box) => box.Inset(-Gap, -Gap);

    /// Blocks moved from `from` to `to`: the boxes (first) take their members along, and the rest
    /// are the ungrouped apps.
    static void ApplyBlocks(RectD[] apps, RectD[] boxes, IReadOnlyList<int[]> members, int[] ungrouped, RectD[] from, RectD[] to)
    {
        for (int g = 0; g < boxes.Length; g++)
        {
            var d = to[g].Origin - from[g].Origin;
            boxes[g] = boxes[g].Offset(d);
            foreach (int i in members[g]) apps[i] = apps[i].Offset(d);
        }
        for (int u = 0; u < ungrouped.Length; u++) apps[ungrouped[u]] = to[boxes.Length + u];
    }
}
