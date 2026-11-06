using Expanse;

/// Dragging a window's border on the canvas (Resizing): what can be grabbed, where the drag asks
/// the window to go, and where it lands when the app has its say.
static class ResizeTests
{
    static readonly ResizeEdges[] AllEdges =
    [
        ResizeEdges.Left, ResizeEdges.Right, ResizeEdges.Top, ResizeEdges.Bottom,
        ResizeEdges.Left | ResizeEdges.Top, ResizeEdges.Left | ResizeEdges.Bottom,
        ResizeEdges.Right | ResizeEdges.Top, ResizeEdges.Right | ResizeEdges.Bottom,
    ];

    public static void Run(Action<string, bool, string> expect)
    {
        void Same(string name, RectD want, RectD got) => expect(name, want == got, $"want {want}, got {got}");

        // --- Only above Group Selection Zoom ---
        void Takes(string name, double scale, int setting, bool want) =>
            expect($"group zoom: {name}", Groups.TakeClicksAt(scale, setting) == want, $"want {want}");
        Takes("at the setting, groups take clicks", 0.25, 25, true);
        Takes("below it too", 0.10, 25, true);
        Takes("a percent above, they don't", 0.26, 25, false);
        Takes("rounded as the minimap shows it: 25.4% is 25%", 0.254, 25, true);
        Takes("and 25.6% is 26%", 0.256, 25, false);
        Takes("0 turns it off", 0.05, 0, false);
        Takes("never at 100%, whatever is asked", 1, 100, false);

        // --- Grabbing a border ---
        var box = new RectD(100, 100, 400, 300);
        void Edges(string name, Vec p, ResizeEdges want, double k = 1)
        {
            var got = Resizing.WindowEdgesAt(box, p, k);
            expect($"window edges: {name}", got == want, $"want {want}, got {got}");
        }
        Edges("the middle moves the window", new(300, 250), ResizeEdges.None);
        Edges("just outside the left", new(95, 250), ResizeEdges.Left);
        Edges("just inside the left", new(102, 250), ResizeEdges.Left);
        Edges("past the inside reach", new(104, 250), ResizeEdges.None);
        Edges("past the outside reach", new(93, 250), ResizeEdges.None);
        Edges("just outside the right", new(505, 250), ResizeEdges.Right);
        Edges("just inside the bottom", new(300, 398), ResizeEdges.Bottom);
        Edges("just outside the top", new(300, 95), ResizeEdges.Top);
        Edges("a corner is both edges", new(96, 96), ResizeEdges.Left | ResizeEdges.Top);
        Edges("the opposite corner", new(504, 404), ResizeEdges.Right | ResizeEdges.Bottom);
        Edges("beside the box, past a corner's reach", new(90, 90), ResizeEdges.None);
        Edges("the reach grows with the display scale", new(92, 250), ResizeEdges.Left, 2);
        Edges("so does the inside's", new(105, 250), ResizeEdges.Left, 2);
        foreach (var tiny in new RectD[] { new(0, 0, 23, 300), new(0, 0, 300, 23) })
            expect("window edges: too small to have a border", Resizing.WindowEdgesAt(tiny, new(0, 0), 1) == ResizeEdges.None, "grabbed");

        // --- Whose border ---
        void Hit(string name, GrabbableWindow[] windows, Vec p, int? want, ResizeEdges edges = ResizeEdges.None)
        {
            var got = Resizing.WindowEdgeAt(windows, p, 1);
            bool ok = got is (int i, ResizeEdges e) ? i == want && e == edges : want == null;
            expect($"window edge: {name}", ok, $"want {want?.ToString() ?? "none"} {edges}, got {got?.ToString() ?? "none"}");
        }
        static GrabbableWindow W(double x, double y, double w, double h, bool resizable = true, RectD[]? owned = null) =>
            new(new RectD(x, y, w, h), resizable, owned ?? []);
        var behind = W(0, 0, 400, 300);
        var front = W(200, 100, 400, 300);
        Hit("a lone window", [behind], new(-3, 150), 0, ResizeEdges.Left);
        Hit("nothing there", [behind], new(-30, 150), null);
        Hit("a border covered by the window in front can't be grabbed", [behind, front], new(400, 200), null);
        Hit("the same border, uncovered", [behind, front], new(400, 50), 0, ResizeEdges.Right);
        Hit("the front window's border wins over what's under it", [behind, front], new(198, 200), 1, ResizeEdges.Left);
        Hit("the body of the front window takes the press", [behind, front], new(300, 200), null);
        Hit("a window that can't resize has no border", [W(0, 0, 400, 300, resizable: false)], new(-3, 150), null);
        Hit("and still covers what's under it", [behind, W(200, 100, 400, 300, resizable: false)], new(400, 200), null);
        Hit("beside one that can't resize, the body under it takes the press", [behind, W(200, 100, 400, 300, resizable: false)], new(198, 200), null);
        Hit("a dialog covers its own window's border",
            [W(0, 0, 400, 300, owned: [new RectD(350, 100, 100, 100)])], new(399, 150), null);
        Hit("the same border, away from the dialog",
            [W(0, 0, 400, 300, owned: [new RectD(350, 100, 100, 100)])], new(399, 50), 0, ResizeEdges.Right);

        // --- Where the drag asks it to go ---
        var start = new RectD(100, 100, 800, 600);
        var min = Resizing.WindowMinimum;
        var max = new SizeD(1920, 1040);
        Same("drag: right edge", new(100, 100, 850, 600), Resizing.ResizeWindow(start, ResizeEdges.Right, new(50, 30), min, max));
        Same("drag: left edge moves the origin", new(50, 100, 850, 600), Resizing.ResizeWindow(start, ResizeEdges.Left, new(-50, 30), min, max));
        Same("drag: on whole pixels, as a zoomed-out pointer moves in fractions", new(100, 100, 833, 600), Resizing.ResizeWindow(start, ResizeEdges.Right, new(33.3, 0), min, max));
        Same("drag: capped at the screen", new(100, 100, 1920, 600), Resizing.ResizeWindow(start, ResizeEdges.Right, new(5000, 0), min, max));
        Same("drag: capped from the left, the right edge stays", new(-1020, 100, 1920, 600), Resizing.ResizeWindow(start, ResizeEdges.Left, new(-5000, 0), min, max));
        Same("drag: stops at the minimum", new(100, 100, 160, 600), Resizing.ResizeWindow(start, ResizeEdges.Right, new(-5000, 0), min, max));
        Same("drag: a small window doesn't jump up to the minimum",
            new(0, 0, 100, 90), Resizing.ResizeWindow(new(0, 0, 100, 80), ResizeEdges.Bottom, new(0, 10), min, max));
        Same("drag: a window past the cap isn't cut on the side not dragged",
            new(0, 0, 3000, 620), Resizing.ResizeWindow(new(0, 0, 3000, 600), ResizeEdges.Bottom, new(0, 20), min, max));

        var deltas = new[] { -5000, -600, -33.5, -1, 0, 1, 33.5, 600, 5000 };
        int swept = 0, wrong = 0;
        string first = "";
        foreach (var edges in AllEdges)
        foreach (double dx in deltas)
        foreach (double dy in deltas)
        {
            swept++;
            var r = Resizing.ResizeWindow(start, edges, new(dx, dy), min, max);
            string? why =
                !edges.HasFlag(ResizeEdges.Left) && r.X != start.X ? "the left edge moved" :
                !edges.HasFlag(ResizeEdges.Top) && r.Y != start.Y ? "the top edge moved" :
                !edges.HasFlag(ResizeEdges.Right) && r.Right != start.Right ? "the right edge moved" :
                !edges.HasFlag(ResizeEdges.Bottom) && r.Bottom != start.Bottom ? "the bottom edge moved" :
                r.W < min.W || r.H < min.H ? "below the minimum" :
                r.W > max.W || r.H > max.H ? "past the screen" :
                r.X != Math.Round(r.X) || r.Y != Math.Round(r.Y) || r.W != Math.Round(r.W) || r.H != Math.Round(r.H) ? "not on whole pixels" : null;
            if (why == null) continue;
            wrong++;
            if (first.Length == 0) first = $"{edges} by {dx},{dy}: {why}, {r}";
        }
        expect($"drag: {swept} drags keep every rule", wrong == 0, $"{wrong} wrong, first {first}");

        // --- Snapping the dragged edges ---
        var near = new RectD(1000, 300, 400, 200);
        (RectD frame, List<Layout.GuideLine> lines) Snapped(RectD f, ResizeEdges edges, double scale = 1, RectD[]? others = null) =>
            Layout.SnapEdges(f, start, edges, others ?? [near], scale, min, max);
        Same("snap: right edge to a window's left edge", new(100, 100, 900, 600), Snapped(new(100, 100, 895, 600), ResizeEdges.Right).frame);
        Same("snap: to the standard gap", new(100, 100, 900 - Layout.Gap, 600), Snapped(new(100, 100, 900 - Layout.Gap - 3, 600), ResizeEdges.Right).frame);
        Same("snap: left edge to a window's right edge, the right edge stays",
            new(1400, 100, 300, 600), Snapped(new(1405, 100, 295, 600), ResizeEdges.Left).frame);
        Same("snap: bottom edge to a window's top", new(100, 100, 800, 200), Snapped(new(100, 100, 800, 205), ResizeEdges.Bottom).frame);
        Same("snap: top edge to a window's top", new(100, 300, 800, 400), Snapped(new(100, 295, 800, 405), ResizeEdges.Top).frame);
        Same("snap: a corner snaps both ways", new(100, 100, 900, 200), Snapped(new(100, 100, 895, 205), ResizeEdges.Right | ResizeEdges.Bottom).frame);
        Same("snap: out of reach, it stays", new(100, 100, 870, 600), Snapped(new(100, 100, 870, 600), ResizeEdges.Right).frame);
        Same("snap: the reach is in screen pixels, so it's wider zoomed out", new(100, 100, 880, 600), Snapped(new(100, 100, 870, 600), ResizeEdges.Right, 0.25).frame);
        Same("snap: an edge not dragged doesn't", new(100, 100, 800, 205), Snapped(new(100, 100, 800, 205), ResizeEdges.Right).frame);
        Same("snap: the nearest of several wins", new(100, 100, 900, 600),
            Snapped(new(100, 100, 899, 600), ResizeEdges.Right, others: [new(1005, 0, 100, 100), near]).frame);
        Same("snap: never below the minimum, the next nearest takes it",
            new(100, 100, 165, 600), Snapped(new(100, 100, 162, 600), ResizeEdges.Right, others: [new(258, 0, 1, 5), new(265, 0, 5, 5)]).frame);
        Same("snap: below the minimum, it stays where it is",
            new(100, 100, 165, 600), Snapped(new(100, 100, 165, 600), ResizeEdges.Right, others: [new(258, 0, 1, 5)]).frame);
        Same("snap: never past the screen",
            new(0, 0, 1915, 600), Layout.SnapEdges(new(0, 0, 1915, 600), start, ResizeEdges.Right, [new(1920 + 2, 0, 5, 5)], 1, min, max).frame);
        var (_, guideLines) = Snapped(new(100, 100, 895, 600), ResizeEdges.Right);
        expect("snap: a guide along the edge it snapped to, reaching both windows",
            guideLines.Count == 1 && guideLines[0] == new Layout.GuideLine(new(1000, 100), new(1000, 700)), string.Join(" ", guideLines));
        expect("snap: no guide for the standard gap, as when moving", Snapped(new(100, 100, 900 - Layout.Gap - 3, 600), ResizeEdges.Right).lines.Count == 0, "a guide");
        expect("snap: no guide out of reach", Snapped(new(100, 100, 870, 600), ResizeEdges.Right).lines.Count == 0, "a guide");

        int snapsSwept = 0, snapsWrong = 0;
        string firstSnap = "";
        var crowd = new RectD[] { near, new(-300, -200, 350, 250), new(950, 720, 300, 300), new(150, 720, 500, 90), new(-40, 300, 100, 500) };
        foreach (var edges in AllEdges)
        foreach (double dx in deltas)
        foreach (double dy in deltas)
        foreach (double scale in new[] { 0.25, 0.5, 1 })
        {
            snapsSwept++;
            var asked = Resizing.ResizeWindow(start, edges, new(dx, dy), min, max);
            var (r, _) = Layout.SnapEdges(asked, start, edges, crowd, scale, min, max);
            double reach = 10 / scale + 0.5;
            string? why =
                !edges.HasFlag(ResizeEdges.Left) && r.X != start.X ? "the left edge moved" :
                !edges.HasFlag(ResizeEdges.Top) && r.Y != start.Y ? "the top edge moved" :
                !edges.HasFlag(ResizeEdges.Right) && r.Right != start.Right ? "the right edge moved" :
                !edges.HasFlag(ResizeEdges.Bottom) && r.Bottom != start.Bottom ? "the bottom edge moved" :
                r.W < min.W || r.H < min.H ? "below the minimum" :
                r.W > max.W || r.H > max.H ? "past the screen" :
                Math.Abs(r.X - asked.X) > reach || Math.Abs(r.Y - asked.Y) > reach || Math.Abs(r.Right - asked.Right) > reach || Math.Abs(r.Bottom - asked.Bottom) > reach ? "snapped too far" :
                r.X != Math.Round(r.X) || r.Y != Math.Round(r.Y) || r.W != Math.Round(r.W) || r.H != Math.Round(r.H) ? "not on whole pixels" : null;
            if (why == null) continue;
            snapsWrong++;
            if (firstSnap.Length == 0) firstSnap = $"{edges} by {dx},{dy} at {scale}: {why}, {r}";
        }
        expect($"snap: {snapsSwept} drags keep every rule", snapsWrong == 0, $"{snapsWrong} wrong, first {firstSnap}");

        // --- Snapping a group's border ---
        var groupBox = new RectD(0, 0, 400, 300);
        var keepApps = new RectD(50, 50, 200, 100);
        (RectD box, List<Layout.GuideLine> lines) GroupSnap(ResizeEdges edges, Vec delta, RectD? required, RectD[] others, RectD[] windows) =>
            Groups.SnappedResizeBox(groupBox, groupBox, edges, delta, required, others, others.Concat(windows).ToList(), 1);
        var other = new RectD(800, 0, 100, 100);
        var aWindow = new RectD(600, 400, 100, 100);
        Same("group snap: to a window's edge", new(0, 0, 600, 300), GroupSnap(ResizeEdges.Right, new(195, 0), keepApps, [], [aWindow]).box);
        expect("group snap: with a guide", GroupSnap(ResizeEdges.Right, new(195, 0), keepApps, [], [aWindow]).lines.Count == 1, "no guide");
        Same("group snap: to the gap before a window", new(0, 0, 600 - Layout.Gap, 300), GroupSnap(ResizeEdges.Right, new(175, 0), keepApps, [], [aWindow]).box);
        Same("group snap: to another group's top, from the bottom edge", new(0, 0, 400, 400), GroupSnap(ResizeEdges.Bottom, new(0, 95), keepApps, [new(600, 400, 100, 100)], []).box);
        Same("group snap: to the gap before another group", new(0, 0, 800 - Groups.Gap, 300), GroupSnap(ResizeEdges.Right, new(375, 0), keepApps, [other], []).box);
        var turnedDown = GroupSnap(ResizeEdges.Right, new(395, 0), keepApps, [other], []);
        Same("group snap: another group's edge is too close, so it stops at the gap", new(0, 0, 800 - Groups.Gap, 300), turnedDown.box);
        expect("group snap: and shows no guide for the edge it didn't reach", turnedDown.lines.Count == 0, string.Join(" ", turnedDown.lines));
        var hitApps = GroupSnap(ResizeEdges.Right, new(-148, 0), keepApps, [], [new(245, 400, 1, 10)]);
        Same("group snap: its apps win over a snap", new(0, 0, 250, 300), hitApps.box);
        expect("group snap: and leave no guide", hitApps.lines.Count == 0, string.Join(" ", hitApps.lines));
        Same("group snap: an empty group's minimum wins too", new(0, 0, Groups.MinWidth + 5, 300),
            GroupSnap(ResizeEdges.Right, new(Groups.MinWidth + 5 - 400, 0), null, [], [new(Groups.MinWidth - 3, 400, 1, 10)]).box);
        Same("group snap: out of reach, it stays", new(0, 0, 470, 300), GroupSnap(ResizeEdges.Right, new(70, 0), keepApps, [], [aWindow]).box);

        var groupOthers = new RectD[] { new(600, 0, 200, 200), new(0, 500, 300, 100), new(-300, 0, 200, 300) };
        var groupWindows = new RectD[] { new(450, 350, 120, 90), new(-60, 340, 40, 40), new(420, -80, 100, 60) };
        int groupSwept = 0, groupWrong = 0;
        string firstGroup = "";
        foreach (var edges in AllEdges)
        foreach (double dx in deltas)
        foreach (double dy in deltas)
        foreach (double scale in new[] { 0.25, 0.5, 1 })
        {
            groupSwept++;
            var (r, drawn) = Groups.SnappedResizeBox(groupBox, groupBox, edges, new(dx, dy), keepApps, groupOthers, groupOthers.Concat(groupWindows).ToList(), scale);
            string? why =
                !edges.HasFlag(ResizeEdges.Left) && r.X != groupBox.X ? "the left edge moved" :
                !edges.HasFlag(ResizeEdges.Top) && r.Y != groupBox.Y ? "the top edge moved" :
                !edges.HasFlag(ResizeEdges.Right) && r.Right != groupBox.Right ? "the right edge moved" :
                !edges.HasFlag(ResizeEdges.Bottom) && r.Bottom != groupBox.Bottom ? "the bottom edge moved" :
                !r.Contains(keepApps) ? "cut through its apps" :
                groupOthers.Any(o => r.Intersects(o.Inset(-Groups.Gap, -Groups.Gap))) ? "within the gap of another group" :
                drawn.Any(l => l.From.X == l.To.X ? Math.Abs(l.From.X - r.X) >= 0.5 && Math.Abs(l.From.X - r.Right) >= 0.5 : Math.Abs(l.From.Y - r.Y) >= 0.5 && Math.Abs(l.From.Y - r.Bottom) >= 0.5) ? "a guide off the box" : null;
            if (why == null) continue;
            groupWrong++;
            if (firstGroup.Length == 0) firstGroup = $"{edges} by {dx},{dy} at {scale}: {why}, {r}";
        }
        expect($"group snap: {groupSwept} drags keep every rule", groupWrong == 0, $"{groupWrong} wrong, first {firstGroup}");

        // --- Where it lands ---
        var target = new RectD(100, 100, 800, 600);
        var refused = new SizeD(900, 650);
        Same("lands: took the size", target, Resizing.Anchored(target, ResizeEdges.Left | ResizeEdges.Top, target.Size));
        Same("lands: refused dragging right, the left edge stays", new(100, 100, 900, 650), Resizing.Anchored(target, ResizeEdges.Right | ResizeEdges.Bottom, refused));
        Same("lands: refused dragging left, the right edge stays", new(0, 100, 900, 650), Resizing.Anchored(target, ResizeEdges.Left | ResizeEdges.Bottom, refused));
        Same("lands: refused dragging the top, the bottom edge stays", new(100, 50, 900, 650), Resizing.Anchored(target, ResizeEdges.Right | ResizeEdges.Top, refused));

        // --- A stack resizes as one and stays one ---
        var stacked = new RectD[] { new(0, 0, 800, 600), new(0, 0, 800, 600), new(20, 20, 700, 500) };
        long[] ids = [1, 2, 3];
        int stacksSwept = 0, splits = 0;
        string firstSplit = "";
        foreach (var edges in AllEdges)
        foreach (double dx in deltas)
        foreach (double dy in deltas)
        {
            stacksSwept++;
            var to = Resizing.ResizeWindow(stacked[0], edges, new(dx, dy), min, max);
            // The last one's app won't go smaller than it was, as some don't.
            var landed = stacked.Select((f, i) => Resizing.Anchored(to, edges,
                i == 2 ? new SizeD(Math.Max(to.W, f.W), Math.Max(to.H, f.H)) : to.Size)).ToList();
            bool whole = landed[0] == landed[1] && Stacks.Find(landed, ids) is [{ Members.Length: 3 }];
            if (whole) continue;
            splits++;
            if (firstSplit.Length == 0) firstSplit = $"{edges} by {dx},{dy}: {string.Join(" ", landed)}";
        }
        expect($"stack: {stacksSwept} drags leave it whole", splits == 0, $"{splits} split, first {firstSplit}");
    }
}
