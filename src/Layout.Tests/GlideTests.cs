using Expanse;

/// Which windows a glide lifts (Layout.GlideArea, Layout.LiftedByGlide): only those it shows some
/// of. Parked windows far off were once brought on screen and flashed.
static class GlideTests
{
    public static void Run(Action<string, bool, string> expect)
    {
        void Is(string name, bool ok, string detail = "") => expect($"glide: {name}", ok, detail);
        var view = new RectD(0, 0, 1000, 800);
        var from = new Camera(new Vec(0, 0), 1);
        var to = new Camera(new Vec(3000, 0), 1);
        var area = Layout.GlideArea(from, to, view);

        Is("the area runs from the start view to the destination", area == new RectD(0, 0, 4000, 800), area.ToString());
        Is("a window in the start view is lifted", Layout.LiftedByGlide(new(100, 100, 300, 200), area));
        Is("a window in the destination view is lifted", Layout.LiftedByGlide(new(3500, 100, 300, 200), area));
        Is("a window between them is lifted", Layout.LiftedByGlide(new(1800, 100, 300, 200), area));
        Is("a parked window far to the left is not", !Layout.LiftedByGlide(new(-5000, 100, 300, 200), area));
        Is("a parked window far to the right is not", !Layout.LiftedByGlide(new(9000, 100, 300, 200), area));
        Is("a parked window far above is not", !Layout.LiftedByGlide(new(1800, -5000, 300, 200), area));
        Is("a parked window far below is not", !Layout.LiftedByGlide(new(1800, 5000, 300, 200), area));
        Is("a window touching the left edge is not", !Layout.LiftedByGlide(new(-300, 100, 300, 200), area));
        Is("a window touching the right edge is not", !Layout.LiftedByGlide(new(4000, 100, 300, 200), area));
        Is("a window touching the top edge is not", !Layout.LiftedByGlide(new(100, -200, 300, 200), area));
        Is("a window touching the bottom edge is not", !Layout.LiftedByGlide(new(100, 800, 300, 200), area));
        Is("a window a pixel inside the edge is lifted", Layout.LiftedByGlide(new(3999, 100, 300, 200), area));

        Is("the same camera gives just the view", Layout.GlideArea(from, from, view) == view);
        var moved = new Camera(new Vec(500, 200), 1);
        Is("the same camera elsewhere gives just that view", Layout.GlideArea(moved, moved, view) == new RectD(500, 200, 1000, 800));

        var zoomedOut = new Camera(new Vec(-1000, -800), 0.25);
        var wide = Layout.GlideArea(zoomedOut, from, view);
        Is("a zoomed-out start covers its larger area", wide == new RectD(-1000, -800, 4000, 3200), wide.ToString());
        Is("a window only the zoomed-out view shows is lifted", Layout.LiftedByGlide(new(-900, -700, 100, 100), wide));
    }
}
