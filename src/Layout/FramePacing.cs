namespace Expanse;

/// When a frame committed now will be on screen, from DWM's clock: an animation is placed for that
/// moment, not for whenever its code happened to run. DWM's waits (DwmFlush) return a few
/// milliseconds early or late, and placed by when the code ran, frames shown a refresh apart moved
/// unevenly (12.6 ms of movement, then 7.7).
public static class FramePacing
{
    /// A commit closer than this share of a refresh to a vblank is shown at the one after.
    public const double LateShare = 0.25;

    /// The vblank a frame committed at `now` shows at: the first from DWM's `vblank` on (which can be
    /// stale, from before `now`) at least LateShare of a `period` after `now`. All in the same ticks
    /// (QueryPerformanceCounter's, as Stopwatch's). Null without a period.
    public static long? ShownAt(long now, long vblank, long period)
    {
        if (period <= 0) return null;
        long earliest = now + (long)(period * LateShare);
        if (vblank >= earliest) return vblank;
        long steps = (earliest - vblank + period - 1) / period;
        return vblank + steps * period;
    }
}
