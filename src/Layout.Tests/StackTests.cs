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
        Is("on screen to the right, with room", view.Contains(Stacks.TabRects(new(1800, 100, 800, 600), [60], view, 1)[0]));
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
        var atRight = Stacks.TabRects(new(1800, 100, 800, 600), [60], view, 1, Stacks.FitRoom(1));
        Is("it and the tabs stay on screen to the right", view.Contains(Stacks.FitRect(atRight[0], 1)) && view.Contains(atRight[0]), $"{atRight[0]}");

        // A stack mostly off one side of the screen (on another monitor, say): as a screenshot showed,
        // with 57 px of it here the tabs ran past its far side.
        RectD mostlyOff = new(-1000, 100, 1057, 600);
        var cutLeft = Stacks.TabRects(mostlyOff, [100], view, 1, Stacks.FitRoom(1));
        Is("cut off on the left, the tab ends by the stack's right edge", cutLeft[^1].Right <= mostlyOff.Right, $"{cutLeft[^1]}");
        Is("and is as narrow as a tab goes", cutLeft[0].W == Stacks.TabMinWidth, $"{cutLeft[0]}");
        var cutLeftRoomy = Stacks.TabRects(new(-1000, 100, 1200, 600), [100, 100], view, 1, Stacks.FitRoom(1));
        var shownLeft = Stacks.FitRect(cutLeftRoomy[0], 1);
        Is("with room in the part showing, the row starts at the screen's edge", shownLeft.X == 0 && cutLeftRoomy[^1].Right <= 200, $"{shownLeft} {cutLeftRoomy[^1]}");
        RectD offRight = new(1970, 100, 1000, 600);
        var cutRight = Stacks.TabRects(offRight, [100], view, 1, Stacks.FitRoom(1));
        Is("cut off on the right, the row starts at the stack's left edge", Stacks.FitRect(cutRight[0], 1).X == offRight.X, $"{cutRight[0]}");
        var cutRightRoomy = Stacks.TabRects(new(1700, 100, 1000, 600), [100, 100], view, 1, Stacks.FitRoom(1));
        Is("with room there, the row stays on screen", cutRightRoomy[^1].Right <= view.Right, $"{cutRightRoomy[^1]}");
        var wide = Stacks.TabRects(new(-1000, 100, 4000, 600), [60, 40], view, 1, Stacks.FitRoom(1));
        Is("off both sides, the row starts at the screen's edge", Stacks.FitRect(wide[0], 1).X == 0, $"{wide[0]}");
        var scatter = new Random(557);
        int pastFar = 0, overlaps = 0, cutTried = 0;
        string pastDetail = "";
        for (int i = 0; i < 4000; i++)
        {
            double rowScale = new[] { 1, 1.25, 1.5, 2 }[i % 4];
            double rowW = 300 + scatter.Next(2500), rowX = scatter.Next(-3000, 2000);
            var rowStack = new RectD(rowX, 100, rowW, 400);
            if (!rowStack.Intersects(view)) continue;
            var rowWidths = Enumerable.Range(0, 1 + i % 4).Select(_ => 20.0 + scatter.Next(300)).ToList();
            var tabRow = Stacks.TabRects(rowStack, rowWidths, view, rowScale, Stacks.FitRoom(rowScale));
            var rowButton = Stacks.FitRect(tabRow[0], rowScale);
            if (rowStack.X < view.X && rowStack.Right <= view.Right)
            {
                cutTried++;
                if (tabRow[^1].Right > rowStack.Right + 1) { pastFar++; pastDetail = $"{rowStack} {tabRow[^1]}"; }
            }
            else if (rowStack.Right > view.Right && rowStack.X >= view.X)
            {
                cutTried++;
                if (rowButton.X < rowStack.X - 1) { pastFar++; pastDetail = $"{rowStack} {rowButton}"; }
            }
            if (rowButton.Right > tabRow[0].X || tabRow.Zip(tabRow.Skip(1)).Any(p => p.First.Right > p.Second.X)) overlaps++;
        }
        Is("sweep: a row never overhangs the far side of a stack cut off by the screen", pastFar == 0, $"{pastFar} of {cutTried}, {pastDetail}");
        Is("sweep: nothing in a row overlaps", overlaps == 0, $"{overlaps}");
        Is("sweep: it met stacks cut off", cutTried > 500, $"{cutTried}");

        // Going to a group leaves room for the tabs. These go through GroupOrigin, the whole decision, not
        // its parts: tests of the parts alone passed while the tabs still sat over the app.
        long[] threeIds = [1, 2, 3];
        RectD alone = new(2000, 100, 400, 300);
        var roomy = Stacks.WithTabRoom([big, small, alone], threeIds, 1);
        Is("a stacked window reaches past the tabs", roomy[0] == big with { H = 638 }, $"{roomy[0]}");
        Is("one inside it reaches just as far", roomy[1].Bottom == roomy[0].Bottom && roomy[1].Y == small.Y, $"{roomy[1]}");
        Is("a window in no stack is left as it is", roomy[2] == alone, $"{roomy[2]}");
        Vec GroupAt(RectD[] frames, int focus, RectD work, double k = 1) => Stacks.GroupOrigin(frames, Enumerable.Range(0, frames.Length).Select(i => (long)i).ToList(),
            Enumerable.Range(0, frames.Length).ToList(), focus, [], default, work, k);

        // As a log showed: a group taller than the screen, going to a window beside the stack, not in it.
        // The view stopped with that window on the bottom edge and the stack past it.
        RectD codeWindow = new(11290, -3551, 1105, 1160), edge = new(12618, -3551, 1965, 1120);
        RectD[] logged = [new(11276, -4638, 1115, 1046), codeWindow, codeWindow, new(12618, -4659, 1963, 1067), new(10183, -3504, 1026, 1016), edge];
        var ultrawide = new RectD(0, 0, 3440, 1408);
        var fromLog = GroupAt(logged, 5, ultrawide);
        Is("the logged group: the view goes on past the focused window", fromLog == new Vec(11143, -3761), $"{fromLog}");
        var codeThere = codeWindow.Offset(new Vec(-fromLog.X, -fromLog.Y));
        var codeTab = Stacks.TabRects(codeThere, [60], ultrawide, 1, Stacks.FitRoom(1))[0];
        Is("the logged group: the tabs are below the stack, not over it", codeTab.Y >= codeThere.Bottom && codeTab.Bottom <= ultrawide.Bottom, $"{codeThere} {codeTab}");
        Is("the logged group: the focused window is all on screen", edge.Y - fromLog.Y >= 0 && edge.Bottom - fromLog.Y <= ultrawide.Bottom, $"{fromLog}");

        // The rule's limits
        var codeBox = Stacks.BoxWithTabs([codeWindow, codeWindow], new WindowStack(1, [0, 1], 1), 1);
        Is("a stack's box takes in its tabs", codeBox == codeWindow with { H = 1198 }, $"{codeBox}");
        Is("a stack that fits leaves the view alone", Stacks.RoomForTabs(new Vec(11000, -3700), [codeBox], edge, ultrawide) == new Vec(11000, -3700));
        Is("a stack off screen leaves it alone", Stacks.RoomForTabs(new Vec(0, -3839), [codeBox], edge, ultrawide) == new Vec(0, -3839));
        var barely = new Vec(11143, codeBox.Y - 1400);
        Is("a stack barely on screen leaves it alone", Stacks.RoomForTabs(barely, [codeBox], edge with { Y = codeBox.Y - 1000 }, ultrawide) == barely);
        var focusAtTop = new Vec(11143, -3839);
        Is("the focused window's top stays on screen", Stacks.RoomForTabs(focusAtTop, [codeBox], edge with { Y = -3839 }, ultrawide) == focusAtTop);
        var stackAtTop = new RectD(11290, -3839, 1105, 1538);
        Is("a stack too tall for the screen keeps its top on it", Stacks.RoomForTabs(focusAtTop, [stackAtTop], null, ultrawide) == focusAtTop);
        var nothing = Stacks.GroupOrigin([], [], [], -1, [], new RectD(0, 0, 500, 400), new RectD(0, 0, 1000, 800), 1);
        Is("an empty group is centred", nothing == new Vec(-250, -200), $"{nothing}");
        var withMaximised = Stacks.GroupOrigin([big, small], [1, 2], [0, 1], -1, [new RectD(100, 2000, 800, 600)], default, new RectD(0, 0, 1000, 800), 1);
        Is("a maximised member counts towards the group's size", withMaximised.Y > 700, $"{withMaximised}");

        // What you see, over many layouts: the focused window on screen if it fits, and a stack's tabs
        // never over it, unless moving the view further would cut off a top or the stack barely shows.
        var random = new Random(20261106);
        RectD[] screens = [new(0, 0, 3440, 1408), new(0, 0, 1920, 1032), new(0, 40, 2560, 1400), new(0, 0, 1366, 728)];
        double[] scales = [1, 1.25, 1.5, 2];
        double Pick(double low, double high) => Math.Round(low + random.NextDouble() * (high - low));
        int tried = 0, rescued = 0, rescuedBeside = 0, focusOff = 0, tabsOver = 0;
        string firstWrong = "";
        for (int run = 0; run < 6000; run++)
        {
            var screen = screens[run % screens.Length];
            double k = scales[run / screens.Length % scales.Length];
            var outer = new RectD(Pick(-600, 600), Pick(-400, 1000), Pick(600, screen.W * 0.9), Pick(400, screen.H * 1.05));
            double innerW = Pick(outer.W * 0.5, outer.W), innerH = Pick(outer.H * 0.5, outer.H);
            var inner = new RectD(outer.X + Pick(0, outer.W - innerW), outer.Y + Pick(0, outer.H - innerH), innerW, innerH);
            var frames = new List<RectD> { outer, inner };
            int extra = 1 + run % 4;
            for (int i = 0; i < extra; i++)
                frames.Add(new RectD(outer.X + Pick(-2000, 2000), outer.Y + Pick(-1500, 1500), Pick(500, 1500), Pick(400, 1300)));
            var ids = Enumerable.Range(0, frames.Count).Select(i => (long)i).ToList();
            var stacks = Stacks.Find(frames, ids);
            if (stacks.Count != 1) continue; // others landed inside each other, so the counts below would mix stacks
            int focus = run % 3 == 2 ? 2 : run % 3;
            bool focusBeside = !stacks[0].Members.Contains(focus);
            var members = ids.Select(i => (int)i).ToList();
            var origin = Stacks.GroupOrigin(frames, ids, members, focus, [], default, screen, k);
            tried++;

            var shownFocus = frames[focus].Offset(new Vec(-origin.X, -origin.Y));
            bool fits = frames[focus].W <= screen.W && frames[focus].H <= screen.H;
            if (fits && !screen.Contains(shownFocus))
            {
                focusOff++;
                if (firstWrong == "") firstWrong = $"focus off screen, run {run}: {shownFocus} on {screen}";
            }

            var box = stacks[0].Members.Select(i => frames[i]).Aggregate((a, b) => a.Union(b));
            bool TabsOver(Vec at)
            {
                var seen = box.Offset(new Vec(-at.X, -at.Y));
                return seen.Intersects(screen) && Stacks.TabRects(seen, [60, 60], screen, k, Stacks.FitRoom(k))[0].Y < seen.Bottom;
            }
            if (TabsOver(origin))
            {
                var seen = box.Offset(new Vec(-origin.X, -origin.Y));
                double withRoom = seen.H + Stacks.TabRoom(k);
                double shown = Math.Min(seen.Y + withRoom, screen.Bottom) - Math.Max(seen.Y, screen.Y);
                bool blocked = seen.Y <= screen.Y || shownFocus.Y <= screen.Y;
                if (!blocked && shown >= Stacks.MostlyShown * withRoom)
                {
                    tabsOver++;
                    if (firstWrong == "") firstWrong = $"tabs over the stack, run {run}: {seen} on {screen}, focus {shownFocus}, scale {k}";
                }
            }
            var plainBounds = frames.Aggregate((a, b) => a.Union(b));
            if (TabsOver(Groups.FocusOrigin(plainBounds, frames[focus], screen)) && !TabsOver(origin))
            {
                rescued++;
                if (focusBeside) rescuedBeside++;
            }
        }
        Is("sweep: enough layouts tried", tried > 2000, $"{tried}");
        Is("sweep: the focused window stays on screen", focusOff == 0, $"{focusOff} of {tried}. {firstWrong}");
        Is("sweep: tabs don't sit over a stack that has room", tabsOver == 0, $"{tabsOver} of {tried}. {firstWrong}");
        // Or a sweep that never met the problem would pass as well.
        Is("sweep: it met the problem, with focus in the stack", rescued - rescuedBeside > 20, $"{rescued - rescuedBeside}");
        Is("sweep: it met the problem, with focus beside the stack", rescuedBeside > 20, $"{rescuedBeside}");

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
