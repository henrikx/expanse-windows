using Expanse;

/// The animation curves (Easing).
static class EasingTests
{
    public static void Run(Action<string, bool, string> expect)
    {
        void Is(string name, bool ok, string detail = "") => expect($"easing: {name}", ok, detail);
        bool Near(double a, double b) => Math.Abs(a - b) < 1e-3;

        Is("ease starts at 0 and ends at 1", Easing.Ease(0) == 0 && Easing.Ease(1) == 1);
        Is("ease holds outside 0 to 1", Easing.Ease(-1) == 0 && Easing.Ease(2) == 1);
        Is("out starts at 0 and ends at 1", Easing.Out(0) == 0 && Easing.Out(1) == 1);
        Is("a straight bezier is straight", Near(Easing.Bezier(1.0 / 3, 1.0 / 3, 2.0 / 3, 2.0 / 3, 0.3), 0.3), $"{Easing.Bezier(1.0 / 3, 1.0 / 3, 2.0 / 3, 2.0 / 3, 0.3)}");
        // CSS's "ease" at half time: 0.8024 (as browsers compute it).
        Is("ease at half time as CSS's", Near(Easing.Ease(0.5), 0.8024), $"{Easing.Ease(0.5)}");

        // A 0.22 s zoom out at 100 Hz: 22 frames.
        const int frames = 22;
        double[] Steps(Func<double, double> f) => Enumerable.Range(0, frames).Select(i => f((i + 1.0) / frames) - f((double)i / frames)).ToArray();
        double[] ease = Steps(Easing.Ease), outSteps = Steps(Easing.Out);
        bool rises = true;
        for (int i = 1; i < frames; i++) if (Easing.Ease((double)i / frames) < Easing.Ease((i - 1.0) / frames)) rises = false;
        Is("ease never goes back", rises);
        Is("ease's first frame moves a quarter of ease-out's", ease[0] < outSteps[0] / 4, $"{ease[0]:0.000} vs {outSteps[0]:0.000}");
        Is("ease's biggest frame is smaller than ease-out's", ease.Max() < outSteps.Max(), $"{ease.Max():0.000} vs {outSteps.Max():0.000}");
        Is("ease's biggest frame isn't among the first three", Array.IndexOf(ease, ease.Max()) >= 3, $"frame {Array.IndexOf(ease, ease.Max()) + 1}");
    }
}
