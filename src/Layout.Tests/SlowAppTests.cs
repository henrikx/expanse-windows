using Expanse;

/// A glide's hand-back against simulated apps of any speed, on a fake clock, using the real rules
/// (Layout.GlideArea, LiftedByGlide, MoveWait). A move is posted to the thread that owns the window,
/// so an app handles its windows' moves one at a time, in order, each taking `moveMs`. The speeds are
/// assumptions, not measurements of any real app: this checks the rules, not how Firefox behaves.
static class SlowAppTests
{
    const double GlideMs = 300, FrameMs = 1000.0 / 60;

    sealed class Window(RectD canvas)
    {
        public RectD Canvas = canvas;
        public Vec Real, Placed;
        public int Pending;
    }

    sealed record Result(int Flashed, int Late, int Frames, double DropAfterCommit);

    /// Glides from `from` to `to` with every window in one app. `liftAll` is how glides lifted
    /// before 0.2.13, to show the simulation does catch the flash. `frameMs` is how long a frame takes.
    static Result Glide(List<Window> windows, Camera from, Camera to, RectD view, double moveMs, bool liftAll, double frameMs = FrameMs)
    {
        double now = 0, busyUntil = 0;
        var queue = new List<(double at, Window w, Vec spot)>();
        void Send(Window w, Vec spot)
        {
            busyUntil = Math.Max(busyUntil, now) + moveMs;
            queue.Add((busyUntil, w, spot));
            w.Pending++;
        }
        void Advance(double to)
        {
            now = to;
            foreach (var m in queue.Where(m => m.at <= now).ToList())
            {
                m.w.Real = m.spot;
                m.w.Pending--;
                queue.Remove(m);
            }
        }
        RectD Frame(Window w) => new(w.Real.X, w.Real.Y, w.Canvas.W, w.Canvas.H);
        bool InView(Window w) => to.ToView(w.Canvas).Intersects(view);

        foreach (var w in windows) w.Real = from.ToView(w.Canvas).Origin;
        var area = Layout.GlideArea(from, to, view);
        foreach (var w in windows.Where(w => liftAll || Layout.LiftedByGlide(w.Canvas, area)))
        {
            var spot = MoveWait.LiftSpot(Frame(w), view);
            if (spot != w.Real) Send(w, spot);
        }

        Advance(GlideMs);
        foreach (int i in MoveWait.InViewFirst(windows.Select(w => to.ToView(w.Canvas)).ToList(), view))
        {
            var w = windows[i];
            w.Placed = to.ToView(w.Canvas).Origin;
            if (w.Real != w.Placed || w.Pending > 0) Send(w, w.Placed);
        }
        double committed = now;
        int frames = MoveWait.Run(
            () => windows.All(w => MoveWait.Landed(w.Real == w.Placed, InView(w), Frame(w).Intersects(view))),
            () => now - committed,
            () => Advance(now + frameMs));
        Advance(now + MoveWait.HoldMs);

        // On the screen as the overlay drops, though the view it dropped on doesn't include it.
        int flashed = windows.Count(w => !InView(w) && Frame(w).Intersects(view));
        int late = windows.Count(w => InView(w) && w.Real != w.Placed);
        return new Result(flashed, late, frames, now - committed);
    }

    public static void Run(Action<string, bool, string> expect)
    {
        void Is(string name, bool ok, string detail = "") => expect($"slow apps: {name}", ok, detail);
        var view = new RectD(0, 0, 3440, 1440);
        var from = new Camera(new Vec(0, 0), 1);
        var to = new Camera(new Vec(-2600, 0), 1);
        const int ParkedCount = 20;

        // Parked well to the right: neither the start view nor the destination shows them.
        List<Window> Parked() => Enumerable.Range(0, ParkedCount).Select(i => new Window(new(6000 + i * 100, 100 + i * 10, 1900, 1000))).ToList();
        var inBoth = new RectD(100, 100, 500, 500);
        var arriving = new RectD(-2400, 100, 1024, 634);
        var leaving = new RectD(2000, 100, 1000, 800);

        // --- The bug, and that the simulation shows it ---
        var before = Glide(Parked(), from, to, view, moveMs: 30, liftAll: true);
        // All but the last, whose lift is still queued as the overlay drops, so it hasn't come on screen.
        Is("control: lifting everything, a slow app's parked windows flash", before.Flashed == ParkedCount - 1, $"{before}");
        var now = Glide(Parked(), from, to, view, moveMs: 30, liftAll: false);
        Is("lifting what the glide shows, they don't", now.Flashed == 0, $"{now}");

        // --- At any speed, windows the glide never shows don't flash ---
        foreach (double ms in new[] { 0.0, 1, 5, 15, 30, 60, 120, 500, 5000 })
        {
            var r = Glide(Parked(), from, to, view, ms, liftAll: false);
            Is($"parked windows don't flash at {ms} ms a move", r.Flashed == 0, $"{r}");
        }

        // --- A fast app: lifting everything was fine, and nothing waits for long ---
        var fast = Glide(Parked(), from, to, view, moveMs: 1, liftAll: true);
        Is("a fast app doesn't flash even lifting everything", fast.Flashed == 0, $"{fast}");
        Is("a fast app ends the wait early", fast.DropAfterCommit < MoveWait.LimitMs + MoveWait.HoldMs, $"{fast}");

        // --- The wait is bounded ---
        var stuck = Glide([new Window(leaving)], from, to, view, moveMs: 100_000, liftAll: false);
        Is("an app that never moves holds the overlay no longer than the limit",
            stuck.DropAfterCommit <= MoveWait.LimitMs + FrameMs + MoveWait.HoldMs, $"{stuck}");
        var paused = Glide([new Window(leaving)], from, to, view, moveMs: 100_000, liftAll: false, frameMs: 0);
        Is("frames that return at once are capped too", paused.Frames == MoveWait.FrameLimit, $"{paused}");

        // --- What a slow app can still do: a real limit of waiting, pinned to notice a change ---
        // A window you were looking at has to leave whatever the rule: too slow, and it's still there.
        var left = Glide([new Window(leaving)], from, to, view, moveMs: 400, liftAll: false);
        Is("a window leaving the view still shows if its app is slower than the wait", left.Flashed == 1, $"{left}");
        var leftInTime = Glide([new Window(leaving)], from, to, view, moveMs: 30, liftAll: false);
        Is("a window leaving the view is gone if its app is quick enough", leftInTime.Flashed == 0, $"{leftInTime}");

        // --- Parked windows' moves share the app's queue: one in view goes ahead of them ---
        var last = Parked();
        last.Add(new Window(inBoth));
        var behind = Glide(last, from, to, view, moveMs: 30, liftAll: false);
        Is("a window in view lands, though it comes after the parked ones", behind.Late == 0 && behind.Flashed == 0, $"{behind}");
        var first = Parked();
        first.Insert(0, new Window(inBoth));
        var ahead = Glide(first, from, to, view, moveMs: 30, liftAll: false);
        Is("a window in view lands when it comes before them", ahead.Late == 0 && ahead.Flashed == 0, $"{ahead}");
        RectD[] mixed = [new(9000, 0, 100, 100), new(10, 10, 100, 100), new(9500, 0, 100, 100), new(20, 20, 100, 100)];
        Is("in view first, otherwise in their order", MoveWait.InViewFirst(mixed, view).SequenceEqual([1, 3, 0, 2]));
        Is("nothing to order", MoveWait.InViewFirst([], view).Length == 0);
        // A new window, opened on screen by its app, goes to its spot like any other.
        var opened = Glide([new Window(arriving)], from, to, view, moveMs: 30, liftAll: false);
        Is("the window glided to lands", opened.Late == 0 && opened.Flashed == 0, $"{opened}");

        // --- The rules themselves ---
        Is("at its spot: landed", MoveWait.Landed(atPlaced: true, inView: true, onMainScreen: true));
        Is("on its way to a spot in view: not landed", !MoveWait.Landed(atPlaced: false, inView: true, onMainScreen: true));
        Is("out of view but still on screen: not landed", !MoveWait.Landed(atPlaced: false, inView: false, onMainScreen: true));
        Is("out of view and off the screen: landed", MoveWait.Landed(atPlaced: false, inView: false, onMainScreen: false));

        var monitor = new RectD(100, 50, 1000, 800);
        Is("lift: on screen stays", MoveWait.LiftSpot(new(200, 100, 300, 200), monitor) == new Vec(200, 100));
        Is("lift: from the right, against the edge", MoveWait.LiftSpot(new(5000, 100, 300, 200), monitor) == new Vec(800, 100));
        Is("lift: from the left", MoveWait.LiftSpot(new(-5000, 100, 300, 200), monitor) == new Vec(100, 100));
        Is("lift: from above", MoveWait.LiftSpot(new(200, -5000, 300, 200), monitor) == new Vec(200, 50));
        Is("lift: from below, against the edge", MoveWait.LiftSpot(new(200, 5000, 300, 200), monitor) == new Vec(200, 650));
        Is("lift: too big to fit, from the top-left", MoveWait.LiftSpot(new(5000, 5000, 2000, 2000), monitor) == new Vec(100, 50));
    }
}
