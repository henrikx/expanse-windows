using Expanse;

/// Where pictures go to be warmed up before a zoom out (Warming).
static class WarmingTests
{
    public static void Run(Action<string, bool, string> expect)
    {
        void Is(string name, bool ok, string detail = "") => expect($"warming: {name}", ok, detail);
        bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;
        var view = new RectD(0, 0, 1000, 800);
        var now = new Camera(new Vec(5000, 0), 1);       // 100%, looking at x 5000 to 6000
        var to = new Camera(new Vec(-1000, -1000), 0.2); // the overview

        Is("on screen at 100%: left alone", Warming.Placement(new RectD(5200, 100, 400, 300), now, to, view) == null);
        Is("partly on screen: left alone", Warming.Placement(new RectD(5900, 100, 400, 300), now, to, view) == null);
        Is("not in the overview either: left alone", Warming.Placement(new RectD(90000, 0, 400, 300), now, to, view) == null);

        var frame = new RectD(1000, 500, 400, 300);
        var p = Warming.Placement(frame, now, to, view);
        Is("off screen, in the overview: warmed", p != null);
        if (p is { } at)
        {
            // Under `now`, the picture scaled by `s` at (x, y) lands where `to` shows the window.
            var shown = now.ToView(new RectD(at.x, at.y, frame.W * at.scale, frame.H * at.scale));
            var want = to.ToView(frame);
            Is("lands where the overview shows it", Near(shown.X, want.X) && Near(shown.Y, want.Y) && Near(shown.W, want.W) && Near(shown.H, want.H),
                $"{shown.X},{shown.Y} {shown.W}x{shown.H} vs {want.X},{want.Y} {want.W}x{want.H}");
            Is("on screen now", shown.Intersects(view));
        }
    }
}
