using Expanse;

/// Matching a window to its saved entry (SavedSpots).
static class SavedSpotTests
{
    public static void Run(Action<string, bool, string> expect)
    {
        void Is(string name, bool ok, string detail = "") => expect($"saved spots: {name}", ok, detail);
        const string app = @"C:\Apps\Notes.exe";
        var big = new SizeD(800, 600);
        SpotSeeker Seek(string a = app, string cls = "Main", string title = "t1", SizeD? size = null, long hwnd = 10, uint pid = 1) =>
            new(a, cls, title, size ?? big, hwnd, pid);
        SavedSpot Spot(string a = app, string cls = "Main", string title = "t1", double w = 800, double h = 600, double? pw = null, double? ph = null, long hwnd = 99, uint pid = 7) =>
            new(a, cls, title, w, h, pw, ph, hwnd, pid);
        Func<long, uint, bool> none = (_, _) => false;
        Func<long, uint, bool> takenHandle(long hwnd) => (h, _) => h == hwnd;
        int Match(SpotSeeker w, SavedSpot[] pool, Func<long, uint, bool>? taken = null) => SavedSpots.FindMatch(w, pool, taken ?? none);
        int Larger(SpotSeeker w, SavedSpot[] pool, Func<long, uint, bool>? taken = null) => SavedSpots.FindLarger(w, pool, taken ?? none);

        // The same window
        SavedSpot[] same = [Spot(title: "t1"), Spot(title: "other", hwnd: 10, pid: 1)];
        Is("the exact handle wins over a title match", Match(Seek(), same) == 1);
        Is("the exact handle wins even when it is taken", Match(Seek(), same, takenHandle(10)) == 1);
        Is("the exact handle wins even when the window is too small", Match(Seek(size: new(10, 10)), same) == 1);
        Is("the same handle with another app doesn't match", Match(Seek(), [Spot(a: @"C:\Apps\Other.exe", hwnd: 10, pid: 1)]) == -1);
        Is("the same handle with another class doesn't match", Match(Seek(), [Spot(cls: "Dialog", hwnd: 10, pid: 1)]) == -1);
        Is("the same handle in another process isn't the same window", Match(Seek(), [Spot(title: "zzz", hwnd: 10, pid: 2)]) == 0, "falls back to the app");

        // Title and app
        SavedSpot[] pool = [Spot(title: "a"), Spot(title: "t1"), Spot(title: "t1", hwnd: 98)];
        Is("title beats same-app-only", Match(Seek(), pool) == 1);
        Is("pool order decides among equal titles", Match(Seek(), [Spot(), Spot(hwnd: 98)]) == 0);
        Is("without a title match, the first of the app", Match(Seek(title: "nope"), pool) == 0);
        Is("a taken title match falls to the next free one", Match(Seek(title: "t1"), [Spot(title: "t1", hwnd: 50), Spot(title: "t1", hwnd: 51)], takenHandle(50)) == 1);
        Is("a taken entry isn't taken by app alone", Match(Seek(title: "nope"), [Spot(hwnd: 50), Spot(hwnd: 51)], takenHandle(50)) == 1);
        Is("when all are taken, -1", Match(Seek(title: "nope"), [Spot(hwnd: 50)], takenHandle(50)) == -1);
        Is("taken is asked with the entry's handle and process", SavedSpots.FindMatch(Seek(title: "x"), [Spot(hwnd: 5, pid: 6)], (h, p) => h == 5 && p == 6) == -1);
        Is("too-small entries are skipped", Match(Seek(size: new(100, 100)), [Spot(), Spot(w: 200, h: 200)]) == 1);
        Is("a path differing only in case matches", Match(Seek(a: app.ToUpperInvariant(), title: "x"), [Spot()]) == 0);
        Is("a different class doesn't match", Match(Seek(cls: "Other", title: "x"), [Spot()]) == -1);
        Is("a different app doesn't match", Match(Seek(a: @"C:\Apps\Other.exe", title: "x"), [Spot()]) == -1);
        Is("the class is compared exactly", Match(Seek(cls: "main", title: "x"), [Spot()]) == -1);
        Is("nothing fits: -1", Match(Seek(), []) == -1);

        // Too small
        Is("PW/PH are used over W/H", SavedSpots.TooSmall(new(100, 100), Spot(w: 110, h: 110, pw: 1000, ph: 1000)));
        Is("PW/PH over W/H, the other way", !SavedSpots.TooSmall(new(100, 100), Spot(w: 1000, h: 1000, pw: 110, ph: 110)));
        Is("exactly a quarter of the area isn't too small", !SavedSpots.TooSmall(new(400, 300), Spot()));
        Is("just under a quarter is too small", SavedSpots.TooSmall(new(400, 299.9), Spot()));
        Is("a zero-size entry is never too small", !SavedSpots.TooSmall(new(1, 1), Spot(w: 0, h: 0)));
        Is("a zero-width entry is never too small", !SavedSpots.TooSmall(new(1, 1), Spot(w: 0, h: 600)));
        Is("a zero-height PH is never too small", !SavedSpots.TooSmall(new(1, 1), Spot(ph: 0)));
        Is("a bigger window isn't too small", !SavedSpots.TooSmall(new(900, 700), Spot()));

        // A larger spot
        var tiny = Seek(size: new(100, 100));
        Is("FindLarger finds the too-small case", Larger(tiny, [Spot()]) == 0);
        Is("FindLarger skips a spot the window fits", Larger(tiny, [Spot(w: 150, h: 150), Spot()]) == 1);
        Is("FindLarger skips taken ones", Larger(tiny, [Spot(hwnd: 50), Spot(hwnd: 51)], takenHandle(50)) == 1);
        Is("FindLarger takes the first of equals", Larger(tiny, [Spot(hwnd: 50), Spot(hwnd: 51)]) == 0);
        Is("FindLarger: nothing too big is -1", Larger(Seek(), [Spot()]) == -1);
        Is("FindLarger: another app is -1", Larger(Seek(a: @"C:\Apps\Other.exe", size: new(100, 100)), [Spot()]) == -1);
        Is("FindLarger: empty pool is -1", Larger(tiny, []) == -1);
        Is("FindLarger ignores the handle", Larger(tiny, [Spot(hwnd: 10, pid: 1)]) == 0);
    }
}
