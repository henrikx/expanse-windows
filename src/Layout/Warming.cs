namespace Expanse;

/// After a while at 100%, DWM gives back the GPU memory behind the overlay's pictures of windows
/// that aren't on screen (in steps, from about a minute in), and the zoom out that shows them all
/// again stalled for 50 to 400 ms halfway while DWM rebuilt it. Drawn but unseen first (at 1/255
/// opacity: at 0 DWM skips them and rebuilds nothing), where the overview will show them, DWM
/// rebuilds it before the zoom starts, and the zoom runs smoothly. Each overview picture changes
/// the pixels it covers by at most 1/255, and those on screen at 100% stay put.
public static class Warming
{
    /// How long at 100% before a zoom out warms the pictures up: DWM was never seen to give any
    /// memory back sooner than about 60 s.
    public const double MinRestSeconds = 45;

    /// The shortest it waits, even once a frame comes on time: DWM gives the memory back, and may
    /// rebuild it, in steps.
    public const double MinMs = 100;

    /// The longest a zoom out waits for DWM to rebuild before it starts all the same.
    public const double MaxMs = 600;

    /// Where the picture of a window at canvas `frame` goes to be warmed, as a scale and an offset
    /// in canvas coordinates (the pictures' own, under camera `now`): where camera `to` shows it.
    /// Null if `now` shows some of it already (DWM keeps that), or `to` shows none of it.
    public static (double scale, double x, double y)? Placement(RectD frame, Camera now, Camera to, RectD view)
    {
        if (now.ToView(frame).Intersects(view)) return null;
        var shown = to.ToView(frame);
        if (!shown.Intersects(view)) return null;
        var at = now.ToCanvas(shown);
        return (to.Scale / now.Scale, at.X, at.Y);
    }
}
