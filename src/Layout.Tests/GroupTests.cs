using Expanse;

/// The rules of groups on the canvas and of dragging a border (Groups, Resizing), case by case.
static class GroupTests
{
    public static void Run(Action<string, bool, string> expect)
    {
        void Same(string name, RectD want, RectD got) => expect(name, want == got, $"want {want}, got {got}");
        void Is(string name, bool ok) => expect(name, ok, "not so");
        double P = Groups.Padding, G = Groups.Gap;

        // --- Grabbing a border ---
        var box = new RectD(100, 100, 200, 100);
        void Edges(string name, Vec p, ResizeEdges want)
        {
            var got = Resizing.EdgesAt(box, p, 6);
            expect($"edges: {name}", got == want, $"want {want}, got {got}");
        }
        Edges("the middle is no border", new(200, 150), ResizeEdges.None);
        Edges("far outside", new(50, 150), ResizeEdges.None);
        Edges("just outside the left", new(96, 150), ResizeEdges.Left);
        Edges("just inside the right", new(297, 150), ResizeEdges.Right);
        Edges("top", new(200, 103), ResizeEdges.Top);
        Edges("bottom", new(200, 204), ResizeEdges.Bottom);
        Edges("a corner is both edges", new(99, 101), ResizeEdges.Left | ResizeEdges.Top);
        Edges("the opposite corner", new(301, 199), ResizeEdges.Right | ResizeEdges.Bottom);
        Edges("beside the box, past a corner's reach", new(90, 150), ResizeEdges.None);
        var thin = new RectD(0, 0, 8, 100);
        expect("edges: a thin box takes the nearer edge", Resizing.EdgesAt(thin, new(7, 50), 6) == ResizeEdges.Right, "not right");

        // --- Dragging a border ---
        var noMin = new SizeD(0, 0);
        Same("resize: right edge", new(0, 0, 150, 100), Resizing.Resize(new(0, 0, 100, 100), ResizeEdges.Right, new(50, 30), noMin));
        Same("resize: left edge moves the origin", new(-20, 0, 120, 100), Resizing.Resize(new(0, 0, 100, 100), ResizeEdges.Left, new(-20, 30), noMin));
        Same("resize: corner", new(0, 10, 90, 90), Resizing.Resize(new(0, 0, 100, 100), ResizeEdges.Right | ResizeEdges.Top, new(-10, 10), noMin));
        Same("resize: minimum holds the far edge", new(60, 0, 40, 100), Resizing.Resize(new(0, 0, 100, 100), ResizeEdges.Left, new(90, 0), new SizeD(40, 40)));
        Same("resize: must keep what it must contain", new(20, 0, 80, 100),
            Resizing.Resize(new(0, 0, 100, 100), ResizeEdges.Left, new(70, 0), noMin, new RectD(20, 20, 30, 30)));

        // --- Membership: the rectangle decides, by an app's centre ---
        var g1 = new RectD(0, 0, 400, 300);
        Is("member: centre inside", Groups.IsMember(g1, new(300, 100, 150, 100)));
        Is("member: centre outside, though overlapping", !Groups.IsMember(g1, new(350, 100, 150, 100)));
        var ms = Groups.Members(g1, [new(10, 10, 50, 50), new(1000, 0, 50, 50), new(100, 100, 50, 50)]);
        expect("members: the ones inside", ms.SequenceEqual([0, 2]), string.Join(",", ms));

        // --- New Group ---
        RectD[] row = [new(0, 0, 100, 100), new(300, 0, 100, 100), new(150, 20, 100, 60), new(1000, 0, 100, 100)];
        var made = Groups.Create(row, [0, 1], []);
        Is("create: made", made != null);
        Same("create: fits the selection with padding", new(-P, -P, 400 + 2 * P, 100 + 2 * P), made!.Box);
        Is("create: an app in the way is out, and its centre stays out", !Groups.IsMember(made.Box, made.Apps[2]));
        Is("create: an app in the way is wholly clear of the box", !made.Box.Intersects(made.Apps[2]));
        Same("create: selected apps don't move", row[0], made.Apps[0]);
        Same("create: apps elsewhere don't move", row[3], made.Apps[3]);
        Is("create: refused when it would hit a group", Groups.Create(row, [0, 1], [new(380, 0, 300, 300)]) == null);
        Is("create: refused closer to a group than the gap", Groups.Create(row, [0, 1], [new(400 + P + G - 1, 0, 300, 300)]) == null);
        Is("create: fine at the gap", Groups.Create(row, [0, 1], [new(400 + P + G, 0, 300, 300)]) != null);
        Is("create: nothing selected, no group", Groups.Create(row, [], []) == null);
        var besideGroup = Groups.Create([new(0, 0, 100, 100), new(40, 40, 20, 20)], [0], [new(100 + P + G, -500, 800, 1000)]);
        Is("create: a pushed app doesn't land in another group", besideGroup != null
            && !Groups.IsMember(new(100 + P + G, -500, 800, 1000), besideGroup.Apps[1]) && !Groups.IsMember(besideGroup.Box, besideGroup.Apps[1]));

        // --- Pushing out ---
        var pushed = Groups.PushOut(new(380, 100, 40, 40), g1, []);
        Same("push: the nearest way out, the gap clear", new(400 + G, 100, 40, 40), pushed);

        // --- Settling: dropped or swept apps ---
        var roomy = new RectD(0, 0, 1000, 1000);
        var s = Groups.Settle([roomy], [new(100, 100, 100, 100)]);
        Same("settle: an app well inside changes nothing", roomy, s.Boxes[0]);
        Same("settle: nor does it move", new(100, 100, 100, 100), s.Apps[0]);
        s = Groups.Settle([roomy], [new(2000, 2000, 100, 100)]);
        Same("settle: an app outside changes nothing", roomy, s.Boxes[0]);
        s = Groups.Settle([g1], [new(300, 100, 150, 100)]);
        Same("settle: poking out, the box grows to fit with padding", new(0, 0, 450 + P, 300), s.Boxes[0]);
        Same("settle: the app stays", new(300, 100, 150, 100), s.Apps[0]);
        s = Groups.Settle([new(0, 0, 400, 300)], [new(300, 100, 150, 100), new(420, 120, 40, 40)]);
        Is("settle: growing takes in the next app", Groups.IsMember(s.Boxes[0], s.Apps[1]));
        Is("settle: and encloses it", s.Boxes[0].Contains(s.Apps[1].Inset(-P, -P)));
        s = Groups.Settle([new(0, 0, 400, 300)], [new(300, 100, 150, 100), new(400, 0, 100, 40), new(440, 60, 150, 40)]);
        Is("settle: the chain carries on", s.Boxes[0].Contains(s.Apps[2].Inset(-P, -P)));
        var wall = new RectD(400 + G + 10, -500, 300, 1200);
        s = Groups.Settle([new(0, 0, 400, 300), wall], [new(300, 100, 150, 100)]);
        Same("settle: blocked, grows only as far as the gap", new(0, 0, 410, 300), s.Boxes[0]);
        Same("settle: and the app slides inside", new(410 - P - 150, 100, 150, 100), s.Apps[0]);
        Same("settle: the other group stays put", wall, s.Boxes[1]);
        var huge = new RectD(50, 50, 600, 100);
        var starts = new Dictionary<int, RectD> { [0] = new(5000, 5000, 600, 100) };
        var snug = new RectD(0, 0, 400, 300);
        s = Groups.Settle([snug, wall], [huge], starts);
        Same("settle: too big to fit, a dragged app snaps back", starts[0], s.Apps[0]);
        Same("settle: and the box is as it was", snug, s.Boxes[0]);
        s = Groups.Settle([snug, wall], [huge]);
        Is("settle: too big, not dragged: pushed out", !Groups.IsMember(s.Boxes[0], s.Apps[0]) && !Groups.IsMember(s.Boxes[1], s.Apps[0]));
        s = Groups.Settle([new(0, 0, 400, 300), new(1000, 0, 400, 300)], [new(300, 100, 150, 100), new(1320, 100, 150, 100)]);
        Is("settle: each group for itself", s.Boxes[0].Right == 450 + P && s.Boxes[1].Right == 1470 + P);
        s = Groups.Settle([new(0, 0, 400, 300)], []);
        Same("settle: an empty group stays", new(0, 0, 400, 300), s.Boxes[0]);

        // --- Moving a group ---
        var at = new RectD(0, 0, 100, 100);
        Same("move: free", new(50, 50, 100, 100), Groups.Move(at, new(50, 50, 100, 100), []));
        var other = new RectD(200, 0, 100, 100);
        Same("move: stops at the gap", new(200 - G - 100, 0, 100, 100), Groups.Move(at, new(150, 0, 100, 100), [other]));
        Same("move: slides along the other group", new(200 - G - 100, 40, 100, 100), Groups.Move(at, new(150, 40, 100, 100), [other]));
        Same("move: a jump clear past it is allowed", new(500, 0, 100, 100), Groups.Move(at, new(500, 0, 100, 100), [other]));

        // --- Growing a group ---
        Same("grow: free", new(0, 0, 150, 100), Groups.Grow(at, new(0, 0, 150, 100), []));
        Same("grow: stops at the gap", new(0, 0, 200 - G, 100), Groups.Grow(at, new(0, 0, 500, 100), [other]));
        Same("grow: left side too", new(300 + G, 0, 200 - G, 100), Groups.Grow(new(320 + G, 0, 180 - G, 100), new(0, 0, 500, 100), [other]));
        Same("grow: shrinking is never held back", new(0, 0, 50, 100), Groups.Grow(at, new(0, 0, 50, 100), [other]));

        // --- Dragging a group's border ---
        var edit = new RectD(0, 0, 400, 300);
        var keep = new RectD(0, 0, 200, 200);
        Same("border: grows", new(0, 0, 500, 300), Groups.ResizeBox(edit, edit, ResizeEdges.Right, new(100, 0), keep, []));
        Same("border: stops at its apps", new(0, 0, 200, 300), Groups.ResizeBox(edit, edit, ResizeEdges.Right, new(-350, 0), keep, []));
        Same("border: an empty group stops at the minimum", new(0, 0, Groups.MinWidth, 300), Groups.ResizeBox(edit, edit, ResizeEdges.Right, new(-350, 0), null, []));
        Same("border: stops at another group", new(0, 0, 800 - G, 300), Groups.ResizeBox(edit, edit, ResizeEdges.Right, new(900, 0), keep, [new(800, 0, 100, 100)]));
        Same("border: the corner too", new(0, 0, 500, 350), Groups.ResizeBox(edit, edit, ResizeEdges.Right | ResizeEdges.Bottom, new(100, 50), keep, []));

        // --- Where clicking a group looks ---
        var workArea = new RectD(0, 0, 1000, 800);
        var o = Groups.FocusOrigin(new(0, 0, 500, 400), null, workArea);
        expect("focus: fits: centred", o == new Vec(-250, -200), $"{o}");
        o = Groups.FocusOrigin(new(0, 0, 3000, 400), new(0, 0, 400, 400), workArea);
        expect("focus: too wide: the last-used app, wholly on screen", o.X <= 0 && o.X + 1000 >= 400, $"{o}");
        o = Groups.FocusOrigin(new(0, 0, 3000, 400), new(2600, 0, 400, 400), workArea);
        expect("focus: and at the far end", o.X <= 2600 && o.X + 1000 >= 3000, $"{o}");
        o = Groups.FocusOrigin(new(0, 0, 3000, 3000), new(1400, 1400, 200, 200), workArea);
        expect("focus: already in view after centring: no shift", o == new Vec(1000, 1100), $"{o}");
        o = Groups.FocusOrigin(new(0, 0, 3000, 400), new(100, 0, 1500, 400), workArea);
        expect("focus: an app wider than the screen: its left edge", o.X == 100, $"{o}");
        o = Groups.FocusOrigin(new(0, 0, 500, 400), null, new RectD(100, 50, 1000, 800));
        expect("focus: the work area's offset counts", o == new Vec(-350, -250), $"{o}");

        // --- Tidy Up: groups as blocks ---
        RectD[] tidyApps = [new(0, 0, 100, 100), new(90, 10, 100, 100), new(1000, 0, 100, 100), new(1090, 10, 100, 100), new(500, 500, 100, 100)];
        RectD[] tidyBoxes = [new(-50, -50, 300, 300), new(950, -50, 300, 300)];
        var before = tidyBoxes.Select(b => Groups.Members(b, tidyApps)).ToArray();
        var tidied = Groups.Tidy(tidyApps, tidyBoxes);
        Is("tidy: apps inside a group no longer overlap", !tidyApps[0].Intersects(tidyApps[1]) && !tidyApps[2].Intersects(tidyApps[3]));
        Is("tidy: boxes enclose their apps with padding", tidied[0].Contains(tidyApps[0].Inset(-P, -P)) && tidied[0].Contains(tidyApps[1].Inset(-P, -P)));
        Same("tidy: boxes fit their apps", Groups.Fit([tidyApps[0], tidyApps[1]]), tidied[0]);
        Is("tidy: membership never changes", tidied.Select((b, i) => Groups.Members(b, tidyApps).SequenceEqual(before[i])).All(x => x));
        Is("tidy: groups stay apart", !tidied[0].Inset(-G + 1, -G + 1).Intersects(tidied[1]));
        Is("tidy: an ungrouped app stays out", tidied.All(b => !b.Intersects(tidyApps[4])));
        RectD[] crowdedApps = [new(0, 0, 100, 100), new(200, 0, 100, 100), new(125, 20, 50, 60)];
        RectD[] crowdedBoxes = [new(-30, -30, 160, 160), new(170, -30, 160, 160)];
        before = crowdedBoxes.Select(b => Groups.Members(b, crowdedApps)).ToArray();
        tidied = Groups.Tidy(crowdedApps, crowdedBoxes);
        Is("tidy: a crowded layout keeps membership", tidied.Select((b, i) => Groups.Members(b, crowdedApps).SequenceEqual(before[i])).All(x => x));
        Is("tidy: and the ungrouped app ends outside", tidied.All(b => !Groups.IsMember(b, crowdedApps[2])));
        RectD[] emptyTidyApps = [new(0, 0, 100, 100)];
        var emptyTidy = Groups.Tidy(emptyTidyApps, [new(500, 500, 300, 300)]);
        Is("tidy: an empty group keeps its size", emptyTidy[0].W == 300 && emptyTidy[0].H == 300);

        RectD[] namedApps = [new(5000, 5000, 100, 100)];
        var named = Groups.Tidy(namedApps, [new(0, 0, 300, 300)], [[0]]);
        Is("tidy: told who's a member, the box follows them", Groups.IsMember(named[0], namedApps[0]));

        // --- Fitting to a screen: groups as blocks ---
        RectD[] fitOld = [new(0, 0, 400, 400), new(1000, 0, 400, 400), new(2000, 0, 100, 100)];
        RectD[] fitNew = [new(0, 0, 300, 300), new(600, 0, 300, 300), new(700, 100, 100, 100)];
        RectD[] fitBoxes = [new(-50, -50, 500, 500), new(950, -50, 500, 500)];
        int[][] fitMembers = [[0], [1]];
        var fitted = Groups.AfterFit(fitOld, fitNew, fitBoxes, fitMembers, refit: false);
        Is("fit: members stay members", Groups.IsMember(fitted[0], fitNew[0]) && Groups.IsMember(fitted[1], fitNew[1]));
        Is("fit: boxes don't overlap", !fitted[0].Intersects(fitted[1]));
        Is("fit: an app that wasn't a member stays out", fitted.All(b => !Groups.IsMember(b, fitNew[2])));
        Is("fit: boxes still enclose", fitted[0].Contains(fitNew[0].Inset(-P, -P)) && fitted[1].Contains(fitNew[1].Inset(-P, -P)));
        RectD[] sameFrames = [new(0, 0, 100, 100)];
        var unmoved = Groups.AfterFit(sameFrames, sameFrames.ToArray(), [new(-100, -100, 500, 500)], [[0]], refit: false);
        Same("fit: nothing moved, the box keeps its extra room", new(-100, -100, 500, 500), unmoved[0]);

        // --- The label and the button ---
        var (label, trash) = Groups.LabelRects(new(100, 200, 500, 300), 80, 1);
        Is("label: above the top-left", label.X == 100 && label.Bottom < 200);
        Is("label: as wide as the text with its padding", label.W == 80 + 2 * Groups.LabelPad);
        Is("label: the can to its right", trash.X >= label.Right && trash.Y == label.Y);
        var (bigLabel, _) = Groups.LabelRects(new(100, 200, 500, 300), 80, 2);
        Is("label: follows the display's scale", bigLabel.H == 2 * label.H);
        var (longLabel, _) = Groups.LabelRects(new(0, 200, 500, 300), 5000, 1);
        Is("label: a long name is capped", longLabel.W <= Groups.LabelMaxWidth);
        var view = new RectD(0, 0, 1000, 800);
        Same("button: where the group's label will be", label, Groups.ButtonRect(new(100, 200, 500, 300), 80, view, 1));
        Same("button: and at the display's scale", bigLabel, Groups.ButtonRect(new(100, 200, 500, 300), 80, view, 2));
        Is("button: on screen with the box's top out of view", view.Contains(Groups.ButtonRect(new(100, -500, 500, 900), 80, view, 1)));
        Is("button: on screen with the box's left out of view", view.Contains(Groups.ButtonRect(new(-500, 200, 900, 300), 80, view, 1)));
        Is("button: on screen with the box past the far corner", view.Contains(Groups.ButtonRect(new(5000, 5000, 500, 300), 80, view, 1)));

        // --- A marquee over a group ---
        var covered = new RectD(100, 100, 200, 200);
        Is("marquee: round the whole box selects it", Groups.CoveredBy(new(50, 50, 400, 400), covered));
        Is("marquee: exactly the box selects it", Groups.CoveredBy(covered, covered));
        Is("marquee: one edge short doesn't", !Groups.CoveredBy(new(50, 50, 249, 400), covered));
        Is("marquee: inside the box doesn't", !Groups.CoveredBy(new(150, 150, 50, 50), covered));
        Is("marquee: elsewhere doesn't", !Groups.CoveredBy(new(500, 500, 400, 400), covered));

        // --- Groups dragged together ---
        void Moved(string name, Vec want, Vec got) => expect($"together: {name}", want == got, $"want {want}, got {got}");
        RectD[] two = [new(0, 0, 100, 100), new(0, 300, 100, 100)];
        RectD levelWithFirst = new(300, 0, 100, 100); // level with the first box only
        Moved("free", new(50, 60), Groups.MoveTogether(two, new(0, 0), new(50, 60), []));
        Moved("no other group in the way", new(50, 0), Groups.MoveTogether(two, new(0, 0), new(50, 0), [levelWithFirst]));
        Vec zero = new(0, 0);
        double touching = 300 - G - 100;
        Moved("right up to the gap", new(touching, 0), Groups.MoveTogether(two, zero, new(touching, 0), [levelWithFirst]));
        Moved("one step past the gap stays put", new(touching, 0), Groups.MoveTogether(two, new(touching, 0), new(touching + 1, 0), [levelWithFirst]));
        Moved("slides along it", new(touching, -300), Groups.MoveTogether(two, new(touching, 0), new(touching + 1, -300), [levelWithFirst]));
        Moved("a jump clear past it is allowed", new(1000, 0), Groups.MoveTogether(two, zero, new(1000, 0), [levelWithFirst]));
        RectD lower = new(300, 300, 100, 100);
        Moved("the second box is stopped too", new(touching, 0), Groups.MoveTogether(two, new(touching, 0), new(touching + 1, 0), [lower]));
        RectD between = new(0, 150, 100, 100);
        Moved("a group between them is no obstacle while neither meets it", new(5, 0), Groups.MoveTogether(two, zero, new(5, 0), [between]));
        RectD[] oneBox = [new(0, 0, 100, 100)];
        RectD[] corner = [new(110 + G, -1000, 100, 3000), new(-1000, 110 + G, 3000, 100)];
        Moved("blocked on both axes stays where it is", new(10, 10), Groups.MoveTogether(oneBox, new(10, 10), new(20, 20), corner));

        // Wherever the pointer goes, they stay the gap clear of every other group.
        var random = new Random(1);
        RectD[] obstacles = [new(400, 0, 100, 100), new(-400, 200, 100, 100), new(0, 800, 300, 100), new(600, 600, 100, 100)];
        var moved = zero;
        bool clear = true;
        for (int i = 0; i < 2000; i++)
        {
            moved = Groups.MoveTogether(two, moved, moved + new Vec(random.Next(-80, 81), random.Next(-80, 81)), obstacles);
            clear &= !two.Any(b => obstacles.Any(o => b.Offset(moved).Intersects(o.Inset(-G, -G))));
        }
        Is("together: 2000 random steps never come within the gap", clear);
    }
}
