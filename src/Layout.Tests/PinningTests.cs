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
        Same("in the middle: nothing", work, Pinning.Reserve(work, [new(700, 400, 400, 300)], 1));
        Same("in a corner: the narrower strip (a tall group by the left edge)", new(400, 0, 1600, 1200), Pinning.Reserve(work, [new(0, 0, 400, 900)], 1));
        Same("in a corner: a wide one by the top", new(0, 300, 2000, 900), Pinning.Reserve(work, [new(0, 0, 1200, 300)], 1));
        Same("covering most of the screen: nothing", work, Pinning.Reserve(work, [new(0, 0, 1500, 1100)], 1));
        Same("two: each takes its edge", new(400, 0, 1200, 1200), Pinning.Reserve(work, [new(0, 100, 400, 600), new(1600, 100, 400, 600)], 1));
        Same("edge reach follows the display's scale", new(0, 0, 1400, 1200), Pinning.Reserve(work, [new(1400, 100, 520, 700)], 2));
        Same("past the reach at a scale of 1: nothing", work, Pinning.Reserve(work, [new(1400, 100, 520, 700)], 1));
        Same("a second strip that would leave less than a third isn't taken", new(600, 0, 1400, 1200),
            Pinning.Reserve(work, [new(0, 100, 600, 500), new(1250, 100, 750, 500)], 1));

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
