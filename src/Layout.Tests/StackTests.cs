using Expanse;

/// Windows stacked inside each other, and their tabs (Stacks).
static class StackTests
{
    public static void Run(Action<string, bool, string> expect)
    {
        void Is(string name, bool ok, string detail = "") => expect($"stacks: {name}", ok, detail);
        static string Show(List<WindowStack> s) => string.Join("; ", s.Select(x => $"outer {x.Outer}, members {string.Join(",", x.Members)}, front {x.Front}"));
        List<WindowStack> Find(params RectD[] frames) => Stacks.Find(frames, Enumerable.Range(0, frames.Length).Select(i => (long)i).ToList());
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

        // How much must be inside
        One("sticking out, with enough inside, counts", [big, new(100 - 45, 100, 300, 200)], 0, [0, 1], 1);
        Is("sticking out further doesn't", Find(big, new(100 - 46, 100, 300, 200)).Count == 0);
        One("slightly offset, the same size", [big, big.Offset(new(16, 16))], 1, [0, 1], 1);
        Is("exactly the share is inside", Stacks.IsInside(new(0, 0, 100, 100), new(15, 0, 500, 500)));
        Is("just under it isn't", !Stacks.IsInside(new(0, 0, 100, 100), new(15.1, 0, 500, 500)));
        Is("touching isn't inside", !Stacks.IsInside(new(0, 0, 100, 100), new(100, 0, 500, 500)));
        // As a log showed: Firefox is taller than Chrome, so it could never lie wholly inside it.
        RectD chrome = new(-3965, -8125, 2098, 1226), firefox = new(-3766, -8096, 1911, 1288), code = new(-3900, -8000, 1500, 900);
        One("a window taller than the one it's on, mostly within it", [firefox, chrome], 1, [0, 1], 1);
        One("and with one wholly inside as well", [code, firefox, chrome], 2, [0, 1, 2], 2);
        Is("the larger stays the outer one when the other is in front", Find(chrome, firefox)[0].Outer == 0);

        // Separate stacks
        var two = Find(big, small, new(2000, 100, 800, 600), new(2100, 200, 100, 100));
        Is("two stacks stay apart", two.Count == 2 && two.All(s => s.Members.Length == 2), Show(two));

        // Raising one doesn't move the tabs
        RectD[] before = [small, big, smaller], after = [big, smaller, small];
        long[] idsBefore = [30, 10, 20], idsAfter = [10, 20, 30];
        var was = Stacks.Find(before, idsBefore)[0];
        var now = Stacks.Find(after, idsAfter)[0];
        Is("raising keeps the order of the tabs", was.Members.Select(i => idsBefore[i]).SequenceEqual(now.Members.Select(i => idsAfter[i])));
        Is("raising keeps the outer window", idsBefore[was.Outer] == 10 && idsAfter[now.Outer] == 10);
        Is("raising changes the one in front", idsBefore[was.Front] == 20 && idsAfter[now.Front] == 30);

        // Which show at 100%
        WindowStack first = new(1, [0, 1], 1), second = new(3, [2, 3], 3);
        WindowStack[] both = [first, second];
        int[] noGroups = [-1, -1, -1, -1, -1], firstInGroup = [7, 7, -1, -1, 7], split = [7, -1, -1, -1, -1];
        Is("in a stack, its tabs show", Stacks.ShowingAt100(both, 0, noGroups, -1).SequenceEqual([first]));
        Is("the other stack's don't", !Stacks.ShowingAt100(both, 3, noGroups, -1).Contains(first));
        Is("in no stack and no group, none show", Stacks.ShowingAt100(both, 4, noGroups, -1).Count == 0);
        Is("nothing focused on the canvas, none show", Stacks.ShowingAt100(both, -1, noGroups, -1).Count == 0);
        Is("another window of the stack's group shows them", Stacks.ShowingAt100(both, 4, firstInGroup, 7).SequenceEqual([first]));
        Is("a window of another group doesn't", Stacks.ShowingAt100(both, 4, firstInGroup, 8).Count == 0);
        Is("a stack partly in the group counts", Stacks.ShowingAt100(both, 4, split, 7).SequenceEqual([first]));
        Is("a maximised window of the group, not among the frames", Stacks.ShowingAt100(both, -1, firstInGroup, 7).SequenceEqual([first]));
        Is("two stacks in one group both show", Stacks.ShowingAt100(both, 4, [7, 7, 7, 7, 7], 7).Count == 2);
        Is("ungrouped windows aren't a group", Stacks.ShowingAt100(both, 4, noGroups, -1).Count == 0);

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
        var work = new RectD(0, 0, 2000, 950);
        Is("no room below goes over the window's bottom edge", Stacks.TabRects(new(100, 100, 800, 850), [60], work, 1)[0].Bottom == 950);
        Is("in a row", Stacks.TabRects(big, [50, 50, 50], view, 1).Select(t => t.Y).Distinct().Count() == 1);
        foreach (double scale in new[] { 1, 1.25, 1.5, 1.75, 2 })
        {
            var narrowed = Stacks.TabRects(new(100.5, 100.25, 301, 200), [190, 190, 190], view, scale);
            Is($"whole pixels at {scale:0.##}x", narrowed.All(t => t.X == Math.Floor(t.X) && t.Y == Math.Floor(t.Y) && t.W == Math.Floor(t.W) && t.H == Math.Floor(t.H)), string.Join(" ", narrowed));
            Is($"no overlap at {scale:0.##}x", narrowed.Zip(narrowed.Skip(1)).All(p => p.First.Right <= p.Second.X), string.Join(" ", narrowed));
            var led = Stacks.TabRects(new(100.5, 100.25, 301, 200), [190, 190, 190], view, scale, Stacks.FitRoom(scale));
            var fit = Stacks.FitRect(led[0], scale);
            Is($"the fit button is on whole pixels at {scale:0.##}x", fit.X == Math.Floor(fit.X) && fit.W == Math.Floor(fit.W), $"{fit}");
            Is($"and clear of the first tab at {scale:0.##}x", fit.Right < led[0].X, $"{fit} {led[0]}");
        }

        // The fit button
        RectD stackBox = new(100, 100, 800, 600);
        var fitted = Stacks.TabRects(stackBox, [60, 40], view, 1, Stacks.FitRoom(1));
        var button = Stacks.FitRect(fitted[0], 1);
        Is("the button starts at the stack's left", button == new RectD(100, 708, 22, 22), $"{button}");
        Is("the tabs follow it", fitted[0] == new RectD(126, 708, 80, 22), $"{fitted[0]}");
        var tightLed = Stacks.TabRects(new(100, 100, 300, 200), [190, 190, 190], view, 1, Stacks.FitRoom(1));
        Is("narrowed tabs leave room for it", tightLed[^1].Right <= 400 && Stacks.FitRect(tightLed[0], 1).X == 100, string.Join(" ", tightLed));
        Is("it stays on screen to the left", view.Contains(Stacks.FitRect(Stacks.TabRects(new(-500, 100, 800, 600), [60], view, 1, Stacks.FitRoom(1))[0], 1)));
        var atRight = Stacks.TabRects(new(1990, 100, 800, 600), [60], view, 1, Stacks.FitRoom(1));
        Is("it and the tabs stay on screen to the right", view.Contains(Stacks.FitRect(atRight[0], 1)) && view.Contains(atRight[0]), $"{atRight[0]}");

        // Fitting: whatever the apps do, the stack holds together, and a second click changes nothing
        RectD Fit(RectD front, double w, double h) => Stacks.Fitted(front, new SizeD(w, h));
        Is("takes the front window's corner", Fit(chrome, 2098, 1226) == chrome);
        RectD[] fittedAll = [Fit(chrome, 1911, 1226), Fit(chrome, 1500, 1226), chrome];
        One("windows of the same size at one spot are a stack", [chrome, chrome, chrome], 2, [0, 1, 2], 2);
        One("an app with a limit on its size is smaller, and stays in", fittedAll, 2, [0, 1, 2], 2);
        One("an app that won't get as small as the front is larger, and stays in", [Fit(chrome, 2400, 1400), chrome], 0, [0, 1], 1);
        var size = new SizeD(chrome.W, chrome.H);
        Is("fitting twice is the same as once", Stacks.Fitted(Stacks.Fitted(chrome, size), size) == chrome);
    }
}
