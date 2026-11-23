namespace Expanse;

/// What a wheel turn (or a touchpad's scroll or pinch) does to the canvas.
public enum ScrollDoes { Zoom, PanX, PanY }

public static class Scrolling
{
    /// What a turn of `delta` (120 a notch) does, and how much: positive zooms in, or moves the
    /// canvas's content right or down. A touchpad's pinch arrives as Ctrl + scroll, so that zooms,
    /// as a plain scroll does; Ctrl + Shift moves up and down, and Shift sideways.
    public static (ScrollDoes does, int amount) Of(bool horizontal, int delta, bool ctrl, bool shift) =>
        horizontal ? (ScrollDoes.PanX, -delta)
        : ctrl && shift ? (ScrollDoes.PanY, delta)
        : shift ? (ScrollDoes.PanX, delta)
        : (ScrollDoes.Zoom, delta);
}

/// Scrolling on the empty desktop at 100%: whether you mean the canvas to come up. A wheel's notch
/// (120) does; a touchpad sends a few at a time, with some sideways in every scroll up or down, so
/// those add up first. Zooming in has nowhere to go from 100%.
public sealed class ScrollStart
{
    /// How far a scroll has to come, zooming out and moving, and the pause that starts it over.
    public const int ZoomOut = 30, Move = 60;
    public const long PauseMs = 300;

    readonly int[] sums = new int[3];
    long last;

    /// A turn at `now` (ms): how far the scroll has come if the canvas is to come up for it, which
    /// it does only for what there's been most of. Otherwise null.
    public int? Add(ScrollDoes does, int amount, long now)
    {
        if (now - last > PauseMs) Array.Clear(sums);
        last = now;
        int i = (int)does;
        if (Math.Sign(sums[i]) != Math.Sign(amount)) sums[i] = 0;
        sums[i] += amount;
        int sum = sums[i];
        if (does == ScrollDoes.Zoom ? sum > -ZoomOut : Math.Abs(sum) < Move) return null;
        if (sums.Any(s => Math.Abs(s) > Math.Abs(sum))) return null;
        Array.Clear(sums);
        return sum;
    }
}
