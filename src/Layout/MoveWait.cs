namespace Expanse;

/// Getting the real windows into place under the overlay before it drops (Canvas.Lift,
/// WaitForMoves, HoldOverlayForRepaint). Kept apart from Windows so it can be tried against apps of
/// any speed on a fake clock (SlowAppTests).
public static class MoveWait
{
    /// The longest the overlay waits for slow apps to move, so one that won't can't hold it up for long.
    /// Input and focus wait too, but only for as long as an app is slow: a window flashing is worse.
    public const int LimitMs = 500;
    /// The wait's frames are capped as well as its time: a frame can return early (composition paused).
    /// Enough that at 240 Hz the time limit still comes first.
    public const int FrameLimit = 150;
    /// How long the overlay stays up after the windows have landed and focus has moved.
    public const int HoldMs = 80;

    /// Where Lift brings a window so it's wholly on the screen, or as near as it fits.
    public static Vec LiftSpot(RectD frame, RectD monitor) => new(
        Math.Clamp(frame.X, monitor.X, Math.Max(monitor.X, monitor.Right - frame.W)),
        Math.Clamp(frame.Y, monitor.Y, Math.Max(monitor.Y, monitor.Bottom - frame.H)));

    /// The order to send windows their moves (their indexes, by frames in view space): those in view
    /// first, otherwise as they come. An app handles its windows' moves one at a time, so nothing you
    /// can see should wait behind windows you can't.
    public static int[] InViewFirst(IReadOnlyList<RectD> frames, RectD view) =>
        Enumerable.Range(0, frames.Count).OrderBy(i => frames[i].Intersects(view) ? 0 : 1).ToArray();

    /// At its spot. Going out of view counts too once it's off the main screen: Lift brought it onto
    /// the screen, and a slow app would still be showing there when the overlay drops.
    public static bool Landed(bool atPlaced, bool inView, bool onMainScreen) => atPlaced || (!inView && !onMainScreen);

    /// Waits frame by frame until everything has landed or the limit is reached. The frames waited.
    public static int Run(Func<bool> allLanded, Func<double> elapsedMs, Action nextFrame)
    {
        int frame = 0;
        for (; frame < FrameLimit && elapsedMs() < LimitMs; frame++)
        {
            if (allLanded()) return frame;
            nextFrame();
        }
        return frame;
    }
}
