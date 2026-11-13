using Expanse;

/// Which vblank a frame committed now shows at (FramePacing).
static class FramePacingTests
{
    public static void Run(Action<string, bool, string> expect)
    {
        void Is(string name, bool ok, string detail = "") => expect($"frame pacing: {name}", ok, detail);
        const long period = 100; // a refresh, in ticks
        long? At(long now, long vblank) => FramePacing.ShownAt(now, vblank, period);

        Is("well before the next vblank: that one", At(1000, 1070) == 1070, $"{At(1000, 1070)}");
        Is("exactly the late share before it: still that one", At(1000, 1025) == 1025, $"{At(1000, 1025)}");
        Is("closer than that: the one after", At(1000, 1010) == 1110, $"{At(1000, 1010)}");
        Is("a vblank just gone: the next", At(1000, 990) == 1090, $"{At(1000, 990)}");
        Is("a stale reading, refreshes ago: the next due", At(1000, 650) == 1050, $"{At(1000, 650)}");
        Is("stale, landing on the late share: that one", At(1000, 625) == 1025, $"{At(1000, 625)}");
        Is("stale, due just after the late share: that one", At(1000, 630) == 1030, $"{At(1000, 630)}");
        Is("stale, due inside the late share: the one after", At(1000, 610) == 1110, $"{At(1000, 610)}");
        Is("no period: no answer", FramePacing.ShownAt(1000, 1070, 0) == null);
        // Commits a refresh apart land a refresh apart, wherever in the refresh they fall.
        long? last = null;
        bool even = true;
        for (long now = 1000; now < 2000; now += period)
        {
            long? at = At(now + (now / period % 3) * 20, 1070);
            if (last is long l && at - l != period) even = false;
            last = at;
        }
        Is("uneven commits a refresh apart show a refresh apart", even);
    }
}
