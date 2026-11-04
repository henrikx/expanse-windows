using Expanse;

static class CameraTests
{
    public static void Run(Action<string, bool, string> expect)
    {
        var middle = new Vec(800, 450);
        bool Near(Vec a, Vec b) => a.DistanceTo(b) < 1e-6;
        void Is(string name, bool ok, string detail = "") => expect($"camera: {name}", ok, detail);

        // The spot under a pointer near the left edge, zooming out from 100%.
        var start = new Camera(new Vec(1000, 500), 1);
        var focus = start.ToCanvas(new Vec(100, 450));

        var c = start;
        c.ZoomOutOnto(c.Scale / 1.2, focus, 0.35, middle);
        Is("the view moves towards the spot, not away", c.ToCanvas(middle).X < start.ToCanvas(middle).X, c.ToString());
        Is("it scales", Math.Abs(c.Scale - 1 / 1.2) < 1e-9, c.ToString());
        Is("the pull is how far along it goes", Math.Abs(start.ToCanvas(middle).X - c.ToCanvas(middle).X - 700 * 0.35) < 1e-6, c.ToString());

        double last = c.ToView(focus).DistanceTo(middle);
        for (int i = 0; i < 12; i++)
        {
            double before = c.ToCanvas(middle).X;
            c.ZoomOutOnto(c.Scale / 1.2, focus, 0.35, middle);
            double now = c.ToView(focus).DistanceTo(middle);
            Is($"notch {i}: closer on screen", now < last, $"{now} from {last}");
            Is($"notch {i}: never past the spot", c.ToCanvas(middle).X >= focus.X && c.ToCanvas(middle).X < before, c.ToString());
            last = now;
        }
        Is("many notches: it's about in the middle", last < 5, last.ToString());

        var centred = new Camera(new Vec(1000, 500), 0.5);
        var there = centred.ToCanvas(middle);
        centred.ZoomOutOnto(0.25, there, 0.35, middle);
        Is("pointing at the middle: no pan", Near(centred.ToCanvas(middle), there), centred.ToString());

        var full = new Camera(new Vec(1000, 500), 0.5);
        full.ZoomOutOnto(0.25, focus, 1, middle);
        Is("a full pull puts it in the middle", Near(full.ToView(focus), middle), full.ToString());

        var far = new Camera(new Vec(0, 0), Camera.MinScale);
        var farBefore = far;
        far.ZoomOutOnto(Camera.MinScale / 2, focus, 0.35, middle);
        Is("as far out as it goes: nothing moves", far.Scale == farBefore.Scale && Near(far.Origin, farBefore.Origin), far.ToString());

        var clamped = new Camera(new Vec(0, 0), Camera.MinScale * 1.1);
        clamped.ZoomOutOnto(Camera.MinScale / 2, focus, 0.35, middle);
        Is("the last notch stops at the limit", clamped.Scale == Camera.MinScale, clamped.ToString());

        // Zooming in, the spot under the pointer stays there at every frame of the glide.
        var pointer = new Vec(300, 200);
        var wide = new Camera(new Vec(-500, 700), 0.25);
        var spot = wide.ToCanvas(pointer);
        var closer = wide;
        closer.Zoom(0.5, pointer);
        foreach (double e in new[] { 0, 0.1, 0.5, 0.9, 1.0 })
        {
            var frame = Camera.Between(wide, closer, e, pointer);
            Is($"between {e}: the spot stays under the pointer", Near(frame.ToView(spot), pointer), frame.ToString());
        }
        Is("between: the zoom changes evenly", Math.Abs(Camera.Between(wide, closer, 0.5, pointer).Scale - Math.Sqrt(0.25 * 0.5)) < 1e-9, "");
        Is("between: starts at the first", Near(Camera.Between(wide, closer, 0, pointer).Origin, wide.Origin), "");
        Is("between: ends at the second", Near(Camera.Between(wide, closer, 1, pointer).Origin, closer.Origin), "");

        // About the middle, as every other glide goes: both middles' straight line.
        var other = new Camera(new Vec(4000, -900), 1);
        var half = Camera.Between(wide, other, 0.5, middle);
        var wantMiddle = new Vec((wide.ToCanvas(middle).X + other.ToCanvas(middle).X) / 2, (wide.ToCanvas(middle).Y + other.ToCanvas(middle).Y) / 2);
        Is("between: the middle moves in a straight line", Near(half.ToCanvas(middle), wantMiddle), half.ToString());

        var bad = start;
        bad.ZoomOutOnto(double.NaN, focus, 0.35, middle);
        Is("not a number: nothing moves", bad.Scale == start.Scale && Near(bad.Origin, start.Origin), bad.ToString());
    }
}
