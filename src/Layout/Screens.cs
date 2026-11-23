namespace Expanse;

/// Which screen a window counts as on. The canvas is the main screen's.
public static class Screens
{
    /// Its centre is on another monitor.
    public static bool CentreElsewhere(RectD frame, RectD main, IReadOnlyList<RectD> others) =>
        !main.Contains(frame.Center) && others.Any(m => m.Contains(frame.Center));

    /// Off the canvas: its centre is on another monitor, or none of it is on the main screen and
    /// some is on another (with monitors of different sizes, a centre can be on neither). One off
    /// every screen isn't: left alone it would be out of reach.
    public static bool IsElsewhere(RectD frame, RectD main, IReadOnlyList<RectD> others) =>
        CentreElsewhere(frame, main, others) || (!frame.Intersects(main) && others.Any(frame.Intersects));

    /// Whether a canvas window lies over another monitor's own (`theirs`), of these windows, the top
    /// one first, with `front` (canvas windows about to be brought to the front) over them all.
    public static bool Covers(IEnumerable<(RectD frame, bool theirs)> topFirst, IEnumerable<RectD> front)
    {
        var canvas = front.ToList();
        foreach (var (frame, theirs) in topFirst)
        {
            if (!theirs) canvas.Add(frame);
            else if (canvas.Any(frame.Intersects)) return true;
        }
        return false;
    }
}
