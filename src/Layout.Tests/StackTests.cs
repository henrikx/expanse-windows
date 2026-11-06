using Expanse;

/// Windows stacked inside each other, and their tabs (Stacks).
static class StackTests
{
    public static void Run(Action<string, bool, string> expect)
    {
        void Is(string name, bool ok, string detail = "") => expect($"stacks: {name}", ok, detail);
        const double T = Stacks.Tolerance;
        static string Show(List<WindowStack> s) => string.Join("; ", s.Select(x => $"outer {x.Outer}, members {string.Join(",", x.Members)}, front {x.Front}"));
        List<WindowStack> Find(params RectD[] frames) => Stacks.Find(frames, Enumerable.Range(0, frames.Length).Select(i => (long)i).ToList(), T);
        void One(string name, RectD[] frames, int outer, int[] members, int front)
        {
            var s = Find(frames);
            Is(name, s.Count == 1 && s[0].Outer == outer && s[0].Members.SequenceEqual(members) && s[0].Front == front, Show(s));
        }
        RectD big = new(100, 100, 800, 600), small = new(200, 200, 300, 200), smaller = new(250, 250, 100, 100);

        // What makes a stack
        Is("nothing, no stacks", Find().Count == 0);
        Is("one window, no stack", Find(big).Count == 0);
        Is("apart, no stack", Find(big, new(2000, 100, 800, 600)).Count == 0);
        Is("partly overlapping, no stack", Find(big, new(500, 400, 800, 600)).Count == 0);
        One("a small window behind a big one", [small, big], 1, [0, 1], 1);
        One("a small window in front of a big one is still a stack", [big, small], 0, [0, 1], 1);
        One("the same size, the one in front holds the tabs", [big, big], 1, [0, 1], 1);
        One("nested windows all join the outermost", [smaller, big, small], 1, [0, 1, 2], 2);
        One("each inside the big one, though not inside each other", [big, new(100, 100, 300, 300), new(500, 300, 300, 300)], 0, [0, 1, 2], 2);

        // The tolerance
        One("sticking out by the tolerance counts", [big, new(100 - T, 100, 300, 200)], 0, [0, 1], 1);
        Is("sticking out by more doesn't", Find(big, new(100 - T - 1, 100, 300, 200)).Count == 0);
        One("slightly offset, the same size", [big, big.Offset(new(T, T))], 1, [0, 1], 1);
        Is("the tolerance is the caller's, for the display's scale",
            Stacks.Find([big, new(100 - 2 * T, 100, 300, 200)], [0, 1], 2 * T).Count == 1);

        // Separate stacks
        var two = Find(big, small, new(2000, 100, 800, 600), new(2100, 200, 100, 100));
        Is("two stacks stay apart", two.Count == 2 && two.All(s => s.Members.Length == 2), Show(two));

        // Raising one doesn't move the tabs
        RectD[] before = [small, big, smaller], after = [big, smaller, small];
        long[] idsBefore = [30, 10, 20], idsAfter = [10, 20, 30];
        var was = Stacks.Find(before, idsBefore, T)[0];
        var now = Stacks.Find(after, idsAfter, T)[0];
        Is("raising keeps the order of the tabs", was.Members.Select(i => idsBefore[i]).SequenceEqual(now.Members.Select(i => idsAfter[i])));
        Is("raising keeps the outer window", idsBefore[was.Outer] == 10 && idsAfter[now.Outer] == 10);
        Is("raising changes the one in front", idsBefore[was.Front] == 20 && idsAfter[now.Front] == 30);

        // The tabs
        var view = new RectD(0, 0, 2000, 1000);
        var tabs = Stacks.TabRects(new(100, 100, 800, 600), [60, 40], view, 1);
        Is("no tabs, none", Stacks.TabRects(big, [], view, 1).Length == 0);
        Is("tabs sit just below the window, from its left", tabs[0] == new RectD(100, 708, 80, 22), $"{tabs[0]}");
        Is("the next beside it", tabs[1] == new RectD(184, 708, 60, 22), $"{tabs[1]}");
        var doubled = Stacks.TabRects(new(100, 100, 800, 600), [60, 40], view, 2);
        Is("and at the display's scale", doubled[0] == new RectD(100, 716, 100, 44), $"{doubled[0]}");
        Is("long text is capped", Stacks.TabRects(big, [5000], view, 1)[0].W == Stacks.TabMaxWidth);
        var tight = Stacks.TabRects(new(100, 100, 300, 200), [190, 190, 190], view, 1);
        Is("too many narrow to the window's width", tight[^1].Right <= 400 && tight[0].W < 190, $"{tight[^1]}");
        var mixed = Stacks.TabRects(new(100, 100, 300, 200), [20, 500, 500], view, 1);
        Is("short ones stay short when narrowing", mixed[0].W == 40, $"{mixed[0]}");
        var tiny = Stacks.TabRects(new(100, 100, 20, 20), [100, 100], view, 1);
        Is("never narrower than the minimum", tiny.All(t => t.W == Stacks.TabMinWidth));
        Is("on screen below the view", view.Contains(Stacks.TabRects(new(100, 900, 800, 600), [60], view, 1)[0]));
        Is("on screen to the left", view.Contains(Stacks.TabRects(new(-500, 100, 800, 600), [60], view, 1)[0]));
        Is("on screen to the right", view.Contains(Stacks.TabRects(new(1990, 100, 800, 600), [60], view, 1)[0]));
        Is("in a row", Stacks.TabRects(big, [50, 50, 50], view, 1).Select(t => t.Y).Distinct().Count() == 1);
        foreach (double scale in new[] { 1, 1.25, 1.5, 1.75, 2 })
        {
            var narrowed = Stacks.TabRects(new(100.5, 100.25, 301, 200), [190, 190, 190], view, scale);
            Is($"whole pixels at {scale:0.##}x", narrowed.All(t => t.X == Math.Floor(t.X) && t.Y == Math.Floor(t.Y) && t.W == Math.Floor(t.W) && t.H == Math.Floor(t.H)), string.Join(" ", narrowed));
            Is($"no overlap at {scale:0.##}x", narrowed.Zip(narrowed.Skip(1)).All(p => p.First.Right <= p.Second.X), string.Join(" ", narrowed));
        }
    }
}
