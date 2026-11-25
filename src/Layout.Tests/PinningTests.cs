using Expanse;

/// Pinned groups: what they take from glides, where they grow to and where they land (Pinning).
static class PinningTests
{
    public static void Run(Action<string, bool, string> expect)
    {
        void Is(string name, bool ok, string detail = "") => expect($"pinning: {name}", ok, detail);
        void Same(string name, RectD want, RectD got) => Is(name, want == got, $"{got} vs {want}");
        var work = new RectD(0, 0, 2000, 1200);

        // --- What a pinned group takes from glides ---
        Same("none pinned: all of it", work, Pinning.Reserve(work, [], 1));
        Same("at the right edge: the strip up to its left side", new(0, 0, 1500, 1200), Pinning.Reserve(work, [new(1500, 100, 480, 700)], 1));
        Same("at the left edge", new(420, 0, 1580, 1200), Pinning.Reserve(work, [new(10, 300, 410, 500)], 1));
        Same("at the bottom", new(0, 0, 2000, 900), Pinning.Reserve(work, [new(600, 900, 600, 280)], 1));
        Same("away from the edges: the strip from the side its middle is nearest", new(1100, 0, 900, 1200), Pinning.Reserve(work, [new(700, 400, 400, 300)], 1));
        Same("in a corner: the strip that leaves most (a tall group by the left edge)", new(400, 0, 1600, 1200), Pinning.Reserve(work, [new(0, 0, 400, 900)], 1));
        Same("in a corner: a wide one by the top", new(0, 300, 2000, 900), Pinning.Reserve(work, [new(0, 0, 1200, 300)], 1));
        // A wide screen, 3440x1440 less the taskbar.
        var wide = new RectD(0, 0, 3440, 1392);
        Same("a wide screen: a group at the side as tall as it, wider than tall", new(1400, 0, 2040, 1392), Pinning.Reserve(wide, [new(0, 40, 1400, 1340)], 1));
        Same("a wide screen: one at the side and the bottom", new(0, 0, 2140, 1392), Pinning.Reserve(wide, [new(2140, 200, 1300, 1180)], 1));
        Same("a wide screen: a corner takes the side, not a third of the height", new(1000, 0, 2440, 1392), Pinning.Reserve(wide, [new(0, 480, 1000, 900)], 1));
        Same("a wide screen: as tall as it but at neither side", new(2100, 0, 1340, 1392), Pinning.Reserve(wide, [new(1000, 40, 1100, 1340)], 1));
        Same("covering most of the screen: nothing", work, Pinning.Reserve(work, [new(0, 0, 1500, 1100)], 1));
        Same("two: each takes its edge", new(400, 0, 1200, 1200), Pinning.Reserve(work, [new(0, 100, 400, 600), new(1600, 100, 400, 600)], 1));
        Same("the bottom's reach follows the display's scale", new(0, 0, 2000, 850), Pinning.Reserve(work, [new(200, 850, 1200, 290)], 2));
        Same("past the bottom's reach, and too wide for the side: nothing", work, Pinning.Reserve(work, [new(200, 850, 1200, 290)], 1));
        Same("away from the right side", new(0, 0, 1400, 1200), Pinning.Reserve(work, [new(1400, 100, 520, 700)], 1));
        Same("dead centre, too little left either way: nothing", work, Pinning.Reserve(work, [new(600, 300, 800, 600)], 1));
        // Where the same group was dragged to on a 3440x1440 screen: all of them keep clear.
        foreach (double x in (double[])[0, 95, 123, 172, 279])
            Same($"a wide screen: {x} from the left", new(x + 1303, 0, 3440 - x - 1303, 1440),
                Pinning.Reserve(new(0, 0, 3440, 1440), [new(x, 180, 1303, 1193)], 1));
        Same("a second strip that would leave less than a third isn't taken", new(600, 0, 1400, 1200),
            Pinning.Reserve(work, [new(0, 100, 600, 500), new(1250, 100, 750, 500)], 1));

        // --- Decoration Size ---
        Is("decoration size: as the display's scaling to begin with", Decorations.Scale(1.5, Decorations.Default) == 1.5);
        Is("decoration size: on top of the display's scaling", Decorations.Scale(1.5, 200) == 3 && Decorations.Scale(2, 50) == 1);
        Is("decoration size: a settings file's 0 or 5000 is kept within the choices", Decorations.Scale(1, 0) == 0.5 && Decorations.Scale(1, 5000) == 2);
        foreach (double k in (double[])[0.5, 0.75, 1.1, 1.33, 2, 3])
        {
            // At sizes no display has, what's beside the name still lines up and the label fits its text.
            var (sw, lb, cm, pn, tr) = Groups.LabelRects(new(100, 200, 500, 300), 80 * k, k);
            Is($"decoration size: the name row stays in order and above the box, {k}", sw.Right <= lb.X && lb.Right <= cm.X && cm.Right <= pn.X && pn.Right <= tr.X && tr.Bottom <= 200 && lb.W >= 80 * k);
            foreach (var screen in (Vec[])[new(1366, 768), new(1920, 1080), new(3440, 1440)])
            {
                if (k > 2 && screen.Y < 1000) continue; // no display that small is scaled by half as much again
                var panel = GroupTree.Panel(screen, k, 1000);
                var rows = GroupTree.ListRect(panel, k);
                Is($"decoration size: the groups panel is on the screen with room for rows, {k} on {screen}", panel.X >= 0 && panel.Y >= 0 && panel.Bottom <= screen.Y && rows.H >= GroupTree.RowHeight * k);
            }
        }

        // --- Sliding out of the screen's side ---
        var wk = new RectD(0, 0, 2000, 1200);
        Is("snapped: against the left", Pinning.SnappedSide(new(0, 100, 400, 600), wk, 1) == -1);
        Is("snapped: near the right", Pinning.SnappedSide(new(1560, 100, 400, 600), wk, 1) == 1);
        Is("snapped: in the middle, neither", Pinning.SnappedSide(new(800, 100, 400, 600), wk, 1) == 0);
        Is("snapped: too far from the edge", Pinning.SnappedSide(new(100, 100, 400, 600), wk, 1) == 0);
        Is("snapped: off the screen isn't against it", Pinning.SnappedSide(new(-300, 100, 400, 600), wk, 1) == 0);
        Same("handle: the box's left strip", new(0, 100, 18, 600), Pinning.Handle(new(0, 100, 400, 600), -1, 1));
        Same("handle: the box's right strip", new(1982, 100, 18, 600), Pinning.Handle(new(1600, 100, 400, 600), 1, 1));
        Same("slid left: the padding and a tenth of the apps stay", new(-341, 100, 400, 600), Pinning.Slid(new(0, 100, 400, 600), -1, wk));
        Same("slid right: the same share stays", new(1941, 100, 400, 600), Pinning.Slid(new(1600, 100, 400, 600), 1, wk));
        Same("visible: the part on screen", new(0, 100, 59, 600), Pinning.Visible(new(-341, 100, 400, 600), wk));

        // --- Pinned zoomed out: it grows to 100% round its centre ---
        var view = new RectD(0, 0, 2000, 1200);
        Same("grows round its centre", new(700, 400, 600, 400), Pinning.Grown(new(940, 560, 120, 80), new(600, 400), view));
        Same("and comes on screen", new(1400, 0, 600, 400), Pinning.Grown(new(1900, 10, 60, 40), new(600, 400), view));
        Same("too big: from the top-left", new(0, 0, 2500, 1300), Pinning.Grown(new(900, 500, 250, 130), new(2500, 1300), view));
        Same("on screen: left alone", new(10, 20, 30, 40), Pinning.OnScreen(new(10, 20, 30, 40), view));

        // --- Unpinned: it lands clear of everything ---
        var box = new RectD(0, 0, 400, 300);
        Same("clear: where it is", box, Pinning.Land(box, [new(1000, 0, 200, 200)], [new(0, 500, 100, 100)]));
        var landed = Pinning.Land(box, [new(300, 0, 400, 400)], []);
        Is("on another group: moved clear of it", !landed.Inset(-Groups.Gap, -Groups.Gap).Intersects(new RectD(300, 0, 400, 400)), $"{landed}");
        Same("on another group: the nearest way out", new(300 - Groups.Gap - 400, 0, 400, 300), landed);
        var overApp = Pinning.Land(box, [], [new(350, 50, 200, 100)]);
        Same("over an app: the nearest way out", new(350 - Groups.Gap - 400, 0, 400, 300), overApp);
        var hemmed = Pinning.Land(box, [new(-500, -500, 440, 1300), new(-60, -500, 900, 480), new(-60, 320, 900, 480)], [new(300, 0, 100, 300)]);
        Is("hemmed in: somewhere clear", new[] { new RectD(-500, -500, 440, 1300), new RectD(-60, -500, 900, 480), new RectD(-60, 320, 900, 480), new RectD(300, 0, 100, 300) }
            .All(o => !hemmed.Inset(-Groups.Gap, -Groups.Gap).Intersects(o)), $"{hemmed}");
        Same("between: halfway", new(5, 10, 15, 20), Pinning.Between(new(0, 0, 10, 10), new(10, 20, 20, 30), 0.5));
    }
}
