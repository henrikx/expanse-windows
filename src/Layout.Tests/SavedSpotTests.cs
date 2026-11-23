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

        // Marks
        Is("likeness: the same marks", AppMarks.Likeness(["a", "b"], ["b", "a"]) == 1);
        Is("likeness: none shared", AppMarks.Likeness(["a"], ["b"]) == 0);
        Is("likeness: one of three", AppMarks.Likeness(["a", "b"], ["b", "c"]) == 1.0 / 3);
        Is("likeness: a repeated mark counts once", AppMarks.Likeness(["a", "a"], ["a"]) == 1);
        Is("likeness: none on either side is 0", AppMarks.Likeness(null, null) == 0 && AppMarks.Likeness([], []) == 0 && AppMarks.Likeness(["a"], null) == 0);
        var marked = Seek(title: "t1") with { Marks = ["a", "b", "c"] };
        SavedSpot[] two = [Spot(title: "t1", hwnd: 50), Spot(title: "x", hwnd: 51) with { Marks = ["a", "b", "c"] }];
        Is("marks beat title", Match(marked, two) == 1);
        Is("the exact handle beats marks", Match(marked, [two[1], Spot(title: "x", hwnd: 10, pid: 1)]) == 1);
        Is("the most alike wins", Match(marked, [Spot() with { Marks = ["a", "b"] }, Spot() with { Marks = ["a", "b", "c"] }]) == 1);
        Is("of equally alike, the one with its title", Match(marked, [Spot(title: "x") with { Marks = ["a", "b"] }, Spot() with { Marks = ["b", "c"] }]) == 1);
        Is("of equally alike, the first", Match(marked, [Spot() with { Marks = ["a", "b"] }, Spot() with { Marks = ["b", "c"] }]) == 0);
        Is("half alike is enough", Match(marked with { Title = "x", Marks = ["a", "b"] }, [Spot() with { Marks = ["a"] }]) == 0);
        Is("less than half alike comes after title", Match(marked, [Spot(title: "x") with { Marks = ["a"] }, Spot()]) == 1);
        Is("less than half alike comes before the app alone", Match(marked with { Title = "x" }, [Spot(), Spot() with { Marks = ["a"] }]) == 1);
        Is("nothing alike is another window", Match(marked with { Title = "x" }, [Spot() with { Marks = ["z"] }]) == -1);
        Is("a title match still counts when marks differ", Match(marked, [Spot() with { Marks = ["z"] }]) == 0);
        Is("a taken spot isn't taken by marks", Match(marked, [two[1], Spot(title: "x", hwnd: 52) with { Marks = ["a", "b"] }], takenHandle(51)) == 1);
        Is("too small a window isn't matched by marks", Match(marked with { Size = new(10, 10) }, [two[1]]) == -1);
        Is("another app's marks don't match", Match(marked with { App = @"C:\Apps\Other.exe" }, [two[1]]) == -1);
        Is("without marks, a marked spot is matched as before", Match(Seek(title: "nope"), [two[1]]) == 0);
        Is("with marks, an unmarked spot is matched as before", Match(marked with { Title = "nope" }, [two[0]]) == 0);
        Is("best likeness is the highest of the app's", SavedSpots.BestLikeness(marked, [Spot() with { Marks = ["a"] }, two[1], two[0]]) == 1);
        Is("best likeness of nothing is 0", SavedSpots.BestLikeness(marked, []) == 0);

        Is("known: by its marks", SavedSpots.FindKnown(marked, two, none) == 1);
        Is("known: not by title or app", SavedSpots.FindKnown(marked, [two[0]], none) == -1);
        Is("known: not by less than half alike", SavedSpots.FindKnown(marked, [Spot() with { Marks = ["a"] }], none) == -1);
        Is("known: not one that's taken", SavedSpots.FindKnown(marked, two, takenHandle(51)) == -1);
        Is("known: not without marks", SavedSpots.FindKnown(Seek(), two, none) == -1);

        // Waiting to be known
        var waiting = new WaitingWindows();
        Is("nothing waits at first", !waiting.Any && waiting.Due(0, _ => true).Count == 0);
        waiting.Add(1, now: 100, patience: 50, mayTake: true);
        waiting.Add(2, now: 100, patience: 5000, mayTake: false);
        waiting.Add(3, now: 100, patience: 5000, mayTake: true);
        Is("none due before it's known or out of patience", waiting.Due(149, _ => false).Count == 0 && waiting.Contains(1));
        Is("a second add doesn't start the wait again", Again());
        Is("a known one is due, and no longer waits", waiting.Due(1600, h => h == 2) is [(2, false, true)] && !waiting.Contains(2));
        Is("one forgotten never comes due", waiting.Forget(3) && !waiting.Forget(3) && waiting.Due(9999, _ => true).Count == 0 && !waiting.Any);
        var settling = new WaitingWindows();
        RectD box = new(0, 0, 800, 600);
        settling.Add(1, now: 0, patience: 9000, mayTake: true);
        settling.Add(2, now: 1000, patience: 9000, mayTake: true);
        Is("known ones wait while windows are still opening", settling.Due(2000, _ => true).Count == 0);
        settling.Saw(1, box, 2000);
        settling.Saw(2, box, 2000);
        settling.Saw(2, box with { W = 700 }, 3000);
        Is("known ones wait while one is still changing", settling.Due(4000, _ => true).Count == 0);
        settling.Saw(2, box with { W = 700 }, 4400);
        settling.Saw(7, box, 4400);
        Is("the same again, or a window that isn't waiting, isn't a change", settling.Due(4500, h => h == 1) is [(1, true, true)]);
        settling.Saw(2, box, 9900);
        Is("out of patience, quiet or not", settling.Due(10000, _ => false) is [(2, true, false)] && !settling.Any);
        bool Again()
        {
            waiting.Add(1, now: 149, patience: 1000, mayTake: false);
            return waiting.Due(150, _ => false) is [(1, true, false)];
        }
    }
}
