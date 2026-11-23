namespace Expanse;

/// What a wheel turn (or a touchpad's scroll or pinch) does to the canvas.
public enum ScrollDoes { Zoom, PanX, PanY }

public static class Scrolling
{
    /// What a turn of `delta` (120 a notch) does, and how much: positive zooms in, or moves the
    /// canvas's content right or down. A wheel zooms; a touchpad's two-finger scroll (`touchpad`)
    /// moves up and down, and its pinch, which arrives as Ctrl + scroll, zooms. Ctrl + Shift moves up
    /// and down, and Shift sideways.
    public static (ScrollDoes does, int amount) Of(bool horizontal, int delta, bool ctrl, bool shift, bool touchpad) =>
        horizontal ? (ScrollDoes.PanX, -delta)
        : ctrl && shift ? (ScrollDoes.PanY, delta)
        : shift ? (ScrollDoes.PanX, delta)
        : touchpad && !ctrl ? (ScrollDoes.PanY, delta)
        : (ScrollDoes.Zoom, delta);
}

/// Which scroll turns are a touchpad's: Windows sends them as a mouse's, a wheel's notch at a time on
/// some. Two fingers on it are scrolling or pinching, and a scroll glides on after they lift.
public sealed class TouchpadTurns
{
    /// How long after a report of two fingers they're still on it (it keeps reporting while they are),
    /// the longest from the fingers or the last turn to the next (a glide's last notches come far
    /// apart), and the longest a glide goes on, so a wheel turned straight after isn't taken for it
    /// for good.
    public const long FingersMs = 150, PauseMs = 500, GlideMs = 4000;

    long? turnAt;

    /// When two fingers were last on it (null: not yet).
    public long? FingersAt { get; private set; }

    /// The touchpad says `fingers` are on it, at `now` (ms).
    public void Fingers(int fingers, long now)
    {
        if (fingers >= 2) FingersAt = now;
    }

    /// Whether the turn at `now` is a touchpad's.
    public bool Turn(long now)
    {
        bool its = now - FingersAt <= PauseMs || (now - turnAt <= PauseMs && now - FingersAt <= GlideMs);
        turnAt = its ? now : null;
        return its;
    }
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
