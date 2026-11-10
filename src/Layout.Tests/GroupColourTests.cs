using Expanse;

/// Group colours: the random pick, the fade to solid, and the wheel.
static class GroupColourTests
{
    public static void Run(Action<string, bool, string> expect)
    {
        void Is(string name, bool ok, string detail = "") => expect($"colours: {name}", ok, detail);
        static bool Near(float got, double want255, double within = 5) => Math.Abs(got * 255 - want255) <= within;
        static bool NearA(float got, double want) => Math.Abs(got - want) < 0.001;

        // --- Packing, for saving ---
        var packed = new GroupColour(12, 200, 255);
        Is("packing round-trips", GroupColour.FromPacked(packed.Packed) == packed);
        Is("the blue packs to its hex", GroupColours.Blue.Packed == 0x4C9AFF);

        // --- The groups panel ---
        var green = new GroupColour(0, 200, 0);
        Is("a group's rows are its colour", GroupColours.TreeBlock(green, false) is { R: 0, B: 0 } block && Near(block.G, 200, 0.5));
        Is("soft: the text stays readable", GroupColours.TreeBlock(green, false).A < 0.25 && GroupColours.TreeBlock(green, true).A < 0.25);
        Is("the highlight is the same colour", GroupColours.TreeHighlight(green, true) with { A = 0 } == GroupColours.TreeBlock(green, true) with { A = 0 });
        Is("painting nothing leaves what's under", GroupColours.Over(new Tint(1, 0, 0, 0), 31, 31, 33) == (31, 31, 33));
        Is("painting solid covers it", GroupColours.Over(new Tint(1, 0, 0, 1), 31, 31, 33) == (255, 0, 0));
        Is("half over black is half", GroupColours.Over(new Tint(1, 1, 1, 0.5f), 0, 0, 0) == (128, 128, 128));

        // --- Hue, saturation and brightness ---
        Is("red", GroupColours.FromHsv(0, 1, 1) == new GroupColour(255, 0, 0));
        Is("green", GroupColours.FromHsv(120, 1, 1) == new GroupColour(0, 255, 0));
        Is("blue", GroupColours.FromHsv(240, 1, 1) == new GroupColour(0, 0, 255));
        Is("no saturation is white", GroupColours.FromHsv(77, 0, 1) == new GroupColour(255, 255, 255));
        Is("grey has no saturation", GroupColours.Saturation(new GroupColour(90, 90, 90)) == 0);
        Is("hue distance goes the short way round", GroupColours.HueDistance(350, 10) == 20);
        for (int h = 0; h < 360; h += 7)
        {
            var c = GroupColours.FromHsv(h, 0.7, 1);
            Is($"hue {h} round-trips", GroupColours.HueDistance(GroupColours.Hue(c), h) < 1.5, $"got {GroupColours.Hue(c)}");
            Is($"hue {h} saturation round-trips", Math.Abs(GroupColours.Saturation(c) - 0.7) < 0.01);
        }

        // --- A new group's colour ---
        var random = new Random(7);
        for (int run = 0; run < 300; run++)
        {
            var one = GroupColours.Pick([], random);
            Is("a pick has the blue's saturation", Math.Abs(GroupColours.Saturation(one) - 0.7) < 0.01);
            Is("a pick is at full brightness", Math.Max(one.R, Math.Max(one.G, one.B)) == 255);
            var taken = new List<GroupColour> { GroupColours.FromHsv(random.NextDouble() * 360, 0.7, 1) };
            var next = GroupColours.Pick(taken, random);
            // One hue rules out a sixth of the wheel: twelve tries all landing there is one in 2 billion.
            Is("a pick keeps clear of the one taken", GroupColours.HueDistance(GroupColours.Hue(next), GroupColours.Hue(taken[0])) >= GroupColours.HuesApart - 1.5);
        }
        var everyHue = Enumerable.Range(0, 36).Select(i => GroupColours.FromHsv(i * 10, 0.7, 1)).ToList();
        Is("a pick still comes back with every hue taken", GroupColours.Saturation(GroupColours.Pick(everyHue, random)) > 0);
        var hues = Enumerable.Range(0, 200).Select(_ => GroupColours.Hue(GroupColours.Pick([], random))).ToList();
        Is("picks are spread round the wheel", hues.Max() - hues.Min() > 300, $"{hues.Min()} to {hues.Max()}");
        Is("a grey group doesn't rule out red", Enumerable.Range(0, 100).Any(_ =>
            GroupColours.HueDistance(GroupColours.Hue(GroupColours.Pick([new GroupColour(90, 90, 90)], random)), 0) < 15));
        Is("a pick is repeatable from a seed", GroupColours.Pick([], new Random(3)) == GroupColours.Pick([], new Random(3)));

        // --- The fade: the blue keeps the look it always had ---
        var b = GroupColours.Blue;
        var light = GroupColours.Fill(b, 0, false);
        Is("light fill is the colour", Near(light.R, 76, 0.5) && Near(light.G, 154, 0.5) && Near(light.B, 255, 0.5));
        Is("light fill is faint", NearA(light.A, 0.08));
        var lightHover = GroupColours.Fill(b, 0, true);
        Is("light hovered fill is a little stronger", NearA(lightHover.A, 0.14));
        var dark = GroupColours.Fill(b, 1, false);
        Is("solid fill is the old dark blue", Near(dark.R, 8) && Near(dark.G, 30) && Near(dark.B, 90), $"{dark}");
        Is("solid fill's strength", NearA(dark.A, 0.55));
        var darkHover = GroupColours.Fill(b, 1, true);
        Is("solid hovered fill is the old one", Near(darkHover.R, 20) && Near(darkHover.G, 60) && Near(darkHover.B, 150), $"{darkHover}");
        Is("solid hovered fill's strength", NearA(darkHover.A, 0.7));
        var edge = GroupColours.Edge(b, 1, false);
        Is("solid border is the old one", Near(edge.R, 30) && Near(edge.G, 90) && Near(edge.B, 200), $"{edge}");
        Is("solid border is at full strength", NearA(edge.A, 1));
        Is("light border's strength", NearA(GroupColours.Edge(b, 0, false).A, 0.5));
        foreach (double s in new[] { 0, 0.5, 1 })
        {
            var e = GroupColours.Edge(b, s, true);
            Is($"hovered border is the colour at solidity {s}", Near(e.B, 255, 0.5) && Near(e.R, 76, 0.5) && e.A == 1);
        }

        // --- The fade: any colour ---
        for (int h = 0; h < 360; h += 15)
        {
            var c = GroupColours.FromHsv(h, 0.7, 1);
            Tint before = GroupColours.Fill(c, 0, false);
            for (double s = 0.1; s <= 1.001; s += 0.1)
            {
                var t = GroupColours.Fill(c, s, false);
                bool inRange = new[] { t.R, t.G, t.B, t.A }.All(v => v >= 0 && v <= 1);
                Is($"hue {h} stays in range at {s:0.0}", inRange, $"{t}");
                Is($"hue {h} darkens and strengthens at {s:0.0}", t.R + t.G + t.B < before.R + before.G + before.B && t.A > before.A);
                before = t;
            }
            var solid = GroupColours.Fill(c, 1, false);
            float strongest = Math.Max(c.R, Math.Max(c.G, c.B));
            Is($"hue {h} solid keeps its strongest channel", (c.R == strongest ? solid.R : c.G == strongest ? solid.G : solid.B) >= Math.Max(solid.R, Math.Max(solid.G, solid.B)) - 0.001f);
            Is($"hue {h} hover is lighter than plain when solid", GroupColours.Fill(c, 1, true).A > solid.A);
        }
        Is("solidity past the ends is held", GroupColours.Fill(b, 2, false) == GroupColours.Fill(b, 1, false) && GroupColours.Fill(b, -1, false) == GroupColours.Fill(b, 0, false));

        // --- Where the wheel goes ---
        var view = new RectD(0, 0, 1000, 800);
        var swatch = new RectD(100, 200, 22, 22);
        var wheel = GroupColours.WheelRect(swatch, view, 1);
        Is("wheel: centred on the circle", wheel.Center == swatch.Center, $"{wheel}");
        Is("wheel: square, at its size", wheel.W == GroupColours.WheelSize && wheel.H == wheel.W);
        Is("wheel: follows the display's scale", GroupColours.WheelRect(swatch, view, 2).W == 2 * wheel.W);
        foreach (var at in new RectD[] { new(-500, 200, 22, 22), new(990, 200, 22, 22), new(100, -900, 22, 22), new(100, 790, 22, 22), new(5000, 5000, 22, 22) })
            Is($"wheel: on screen from {at}", view.Contains(GroupColours.WheelRect(at, view, 1)), $"{GroupColours.WheelRect(at, view, 1)}");
        var tiny = new RectD(0, 0, 100, 100);
        Is("wheel: a view smaller than it starts at the corner", GroupColours.WheelRect(swatch, tiny, 1).Origin == new Vec(0, 0));

        // --- Growing out of the circle ---
        Is("growing: starts as the circle", GroupColours.WheelGrowing(swatch, wheel, 0) == swatch);
        Is("growing: ends as the wheel", GroupColours.WheelGrowing(swatch, wheel, 1) == wheel);
        Is("growing: past the end is the wheel", GroupColours.WheelGrowing(swatch, wheel, 1.5) == wheel);
        double lastWidth = swatch.W;
        for (double t = 0.05; t < 1; t += 0.05)
        {
            var grown = GroupColours.WheelGrowing(swatch, wheel, t);
            Is($"growing: from the circle's centre at {t:0.00}", grown.Center.DistanceTo(swatch.Center) < 0.001, $"{grown}");
            Is($"growing: stays round at {t:0.00}", Math.Abs(grown.W - grown.H) < 0.001);
            Is($"growing: only gets bigger at {t:0.00}", grown.W > lastWidth && grown.W < wheel.W);
            lastWidth = grown.W;
        }
        Is("growing: quick at first", GroupColours.WheelGrowing(swatch, wheel, 0.5).W > (swatch.W + wheel.W) / 2);
        var atEdge = new RectD(0, 0, 22, 22);
        var clamped = GroupColours.WheelRect(atEdge, view, 1);
        Is("growing: at the screen's edge it still starts as the circle and ends on screen",
            GroupColours.WheelGrowing(atEdge, clamped, 0) == atEdge && view.Contains(GroupColours.WheelGrowing(atEdge, clamped, 1)));

        // --- On the wheel ---
        Is("on: the centre", GroupColours.OnWheel(wheel, wheel.Center));
        Is("on: the rim", GroupColours.OnWheel(wheel, wheel.Center + new Vec(wheel.W / 2, 0)));
        Is("off: the square's corner", !GroupColours.OnWheel(wheel, wheel.Origin));
        Is("the centre is white", GroupColours.ColourOnWheel(wheel, wheel.Center) == new GroupColour(255, 255, 255));
        Is("right of centre is red", GroupColours.ColourOnWheel(wheel, wheel.Center + new Vec(wheel.W / 2, 0)) == new GroupColour(255, 0, 0));
        Is("past the rim counts as the rim", GroupColours.ColourOnWheel(wheel, wheel.Center + new Vec(wheel.W * 5, 0)) == new GroupColour(255, 0, 0));
        var offRandom = new Random(11);
        for (int run = 0; run < 2000; run++)
        {
            double angle = offRandom.NextDouble() * Math.PI * 2, distance = Math.Sqrt(offRandom.NextDouble()) * wheel.W / 2;
            var p = wheel.Center + new Vec(Math.Cos(angle) * distance, Math.Sin(angle) * distance);
            var picked = GroupColours.ColourOnWheel(wheel, p);
            var back = GroupColours.ColourOnWheel(wheel, GroupColours.PointOnWheel(wheel, picked));
            int off = Math.Max(Math.Abs(picked.R - back.R), Math.Max(Math.Abs(picked.G - back.G), Math.Abs(picked.B - back.B)));
            Is("a picked colour's point gives it back", off <= 3, $"{picked} -> {back}");
            Is("a colour's point is on the wheel", wheel.Center.DistanceTo(GroupColours.PointOnWheel(wheel, picked)) <= wheel.W / 2 + 0.001);
        }
        Is("the blue has a point on the wheel", GroupColours.OnWheel(wheel, GroupColours.PointOnWheel(wheel, b)));

        // --- The wheel's pixels ---
        int size = 150;
        var pixels = GroupColours.WheelPixels(size);
        Is("pixels: four bytes each", pixels.Length == size * size * 4);
        int Index(int x, int y) => (y * size + x) * 4;
        Is("pixels: the corner is clear", pixels[Index(0, 0) + 3] == 0);
        Is("pixels: the middle is solid and near white", pixels[Index(75, 75) + 3] == 255 && pixels[Index(75, 75) + 2] > 240 && pixels[Index(75, 75)] > 240);
        int red = Index(145, 75);
        Is("pixels: blue, green, red, alpha order", pixels[red + 2] == 255 && pixels[red] < 40 && pixels[red + 3] == 255, $"{pixels[red]},{pixels[red + 1]},{pixels[red + 2]}");
        bool premultiplied = true;
        for (int i = 0; i < pixels.Length; i += 4)
            premultiplied &= pixels[i] <= pixels[i + 3] && pixels[i + 1] <= pixels[i + 3] && pixels[i + 2] <= pixels[i + 3];
        Is("pixels: premultiplied", premultiplied);
        bool smooth = Enumerable.Range(0, size * size).Any(i => pixels[i * 4 + 3] is > 0 and < 255);
        Is("pixels: the rim is smooth", smooth);
    }
}
