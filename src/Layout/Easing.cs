namespace Expanse;

/// How far along (0 to 1) an animation is at time `t` (0 to 1).
public static class Easing
{
    /// Ease-out cubic: fastest at once, then slowing. For a wheel turn, which carries on from where
    /// the last one was heading and so must keep going without a fresh start.
    public static double Out(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

    /// CSS's "ease", cubic-bezier(0.25, 0.1, 0.25, 1): a short start, fastest a fifth of the way in,
    /// then a long slow-down. Ease-out moved 13% of a 0.22 s zoom out in its first frame (100 Hz),
    /// just when a frame is likeliest to come late, so any hitch there showed as a jump; this
    /// moves 3% then, and at most 10% in any frame.
    public static double Ease(double t) => Bezier(0.25, 0.1, 0.25, 1, t);

    /// The cubic Bézier through (0,0), (x1,y1), (x2,y2), (1,1), as CSS's cubic-bezier(): the y at x = `t`.
    public static double Bezier(double x1, double y1, double x2, double y2, double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        static double At(double a, double b, double u) => 3 * (1 - u) * (1 - u) * u * a + 3 * (1 - u) * u * u * b + u * u * u;
        // x rises with u (x1 and x2 within 0 to 1), so halving finds the u for x = t.
        double lo = 0, hi = 1;
        for (int i = 0; i < 40; i++)
        {
            double mid = (lo + hi) / 2;
            if (At(x1, x2, mid) < t) lo = mid; else hi = mid;
        }
        return At(y1, y2, (lo + hi) / 2);
    }
}
