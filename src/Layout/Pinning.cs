namespace Expanse;

/// Pinned groups: a group taken off the canvas and kept at a spot on the screen, at 100%, while
/// you move around the canvas under it. Its apps are real windows that stay where they are, above
/// the canvas. Spots are in view space (the main screen's, from its top-left).
public static class Pinning
{
    /// How near a screen edge (view pixels, at a display scale of 1) a pinned group may be and
    /// still count as sitting at it, taking that strip from glides.
    public const double EdgeReach = 48;

    /// The part of `work` that going to a window uses, with pinned groups at `spots` sitting at its
    /// edges taken out: the strip from the edge to the far side of the group, like a docked bar. A
    /// group at two edges (a corner) takes the narrower strip. One that covers more than half the
    /// work area takes nothing, and nor does one that would leave less than a third of it either
    /// way: a window has to go somewhere.
    public static RectD Reserve(RectD work, IEnumerable<RectD> spots, double k)
    {
        var left = work;
        double reach = EdgeReach * k;
        foreach (var s in spots)
        {
            if (s.W * s.H > work.W * work.H / 2 || !s.Intersects(work)) continue;
            var strips = new List<(double size, RectD rest)>(4);
            if (s.X - work.X <= reach) strips.Add((s.Right - work.X, RestFrom(left, x: s.Right)));
            if (work.Right - s.Right <= reach) strips.Add((work.Right - s.X, RestTo(left, right: s.X)));
            if (s.Y - work.Y <= reach) strips.Add((s.Bottom - work.Y, RestFrom(left, y: s.Bottom)));
            if (work.Bottom - s.Bottom <= reach) strips.Add((work.Bottom - s.Y, RestTo(left, bottom: s.Y)));
            if (strips.Count == 0) continue;
            var rest = strips.MinBy(t => t.size).rest;
            if (rest.W < work.W / 3 || rest.H < work.H / 3) continue;
            left = rest;
        }
        return left;
    }

    static RectD RestFrom(RectD r, double? x = null, double? y = null)
    {
        double nx = Math.Max(r.X, x ?? r.X), ny = Math.Max(r.Y, y ?? r.Y);
        return new RectD(nx, ny, r.Right - nx, r.Bottom - ny);
    }

    static RectD RestTo(RectD r, double? right = null, double? bottom = null) =>
        new(r.X, r.Y, Math.Min(r.Right, right ?? r.Right) - r.X, Math.Min(r.Bottom, bottom ?? r.Bottom) - r.Y);

    /// How wide the strip along a snapped group's screen-side edge is, which slides it out when clicked.
    /// Narrower than the padding apps are kept from the box's edge, so no app covers it.
    public const double HandleWidth = 18;

    /// How much of its apps' width stays on the screen when a group slides out.
    public const double SlidShare = 0.1;

    /// Which side of `work` (-1 left, 1 right, 0 neither) the group at `box` is snapped against: within
    /// the reach of that edge, and of the two the nearer.
    public static int SnappedSide(RectD box, RectD work, double k)
    {
        double reach = EdgeReach * k, left = box.X - work.X, right = work.Right - box.Right;
        bool l = left <= reach && left >= -0.5, r = right <= reach && right >= -0.5;
        return l && r ? (left <= right ? -1 : 1) : l ? -1 : r ? 1 : 0;
    }

    /// The strip along the side of `box` that's against the screen (`side`: -1 left, 1 right).
    public static RectD Handle(RectD box, int side, double k)
    {
        double w = Math.Round(HandleWidth * k);
        return new RectD(side < 0 ? box.X : box.Right - w, box.Y, w, box.H);
    }

    /// `box` slid out through the screen's `side`, so the padding on its inner side and about a tenth
    /// of its apps' width stay in `work`.
    public static RectD Slid(RectD box, int side, RectD work)
    {
        double pad = Groups.Padding, shown = pad + SlidShare * Math.Max(0, box.W - 2 * pad);
        return box with { X = Layout.Round(side < 0 ? work.X - (box.W - shown) : work.Right - shown) };
    }

    /// The part of `box` on `work`, for taking room from glides while it's slid out.
    public static RectD Visible(RectD box, RectD work)
    {
        double x = Math.Max(box.X, work.X), y = Math.Max(box.Y, work.Y);
        return new RectD(x, y, Math.Max(0, Math.Min(box.Right, work.Right) - x), Math.Max(0, Math.Min(box.Bottom, work.Bottom) - y));
    }

    /// `spot` moved onto `view` as far as it fits: from its top-left if it's bigger.
    public static RectD OnScreen(RectD spot, RectD view)
    {
        double x = Math.Max(view.X, Math.Min(spot.X, view.Right - spot.W));
        double y = Math.Max(view.Y, Math.Min(spot.Y, view.Bottom - spot.H));
        return spot with { X = Layout.Round(x), Y = Layout.Round(y) };
    }

    /// Pinned while zoomed out: the group, `shown` on screen, grows to 100% (`full`) round its
    /// own centre, and moves only as far as it must to be wholly on screen.
    public static RectD Grown(RectD shown, SizeD full, RectD view) =>
        OnScreen(new RectD(shown.MidX - full.W / 2, shown.MidY - full.H / 2, full.W, full.H), view);

    /// Where an unpinned group's box goes on the canvas: `box` if it's clear, otherwise the nearest
    /// spot that is. Clear: `Groups.Gap` from every other group's box and from every app (its own
    /// apps aren't in `apps`), so it takes in no one else and nothing has to make way.
    public static RectD Land(RectD box, IReadOnlyList<RectD> boxes, IReadOnlyList<RectD> apps)
    {
        var obstacles = boxes.Concat(apps).ToList();
        bool Clear(RectD b) => !obstacles.Any(o => b.Inset(-Groups.Gap, -Groups.Gap).Intersects(o));
        if (Clear(box)) return box;
        // The nearest clear spot is, along each axis, where it was or against an obstacle's gap.
        var xs = new List<double> { box.X };
        var ys = new List<double> { box.Y };
        foreach (var o in obstacles)
        {
            xs.Add(o.X - Groups.Gap - box.W);
            xs.Add(o.Right + Groups.Gap);
            ys.Add(o.Y - Groups.Gap - box.H);
            ys.Add(o.Bottom + Groups.Gap);
        }
        RectD? best = null;
        double bestDistance = double.MaxValue;
        foreach (double x in xs)
            foreach (double y in ys)
            {
                double d = (x - box.X) * (x - box.X) + (y - box.Y) * (y - box.Y);
                if (d >= bestDistance) continue;
                var b = box with { X = x, Y = y };
                if (!Clear(b)) continue;
                best = b;
                bestDistance = d;
            }
        // Something always clears to the right of everything.
        return best ?? box with { X = obstacles.Max(o => o.Right) + Groups.Gap };
    }

    /// Between `a` and `b`, `t` of the way.
    public static RectD Between(RectD a, RectD b, double t) => new(
        a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.W + (b.W - a.W) * t, a.H + (b.H - a.H) * t);
}
