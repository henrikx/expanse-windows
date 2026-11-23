using System.Diagnostics;
using System.Runtime.InteropServices;
using Expanse.Native;
using static Expanse.Native.User32;

namespace Expanse;

/// Pinned groups (see Pinning): a group taken off the canvas and kept at a spot on the screen, at
/// 100%, while you move around the canvas under it. Its apps are real windows Expanse leaves where
/// they are, as it does windows on another monitor, kept always-on-top so the canvas goes by under
/// them; a faint frame of the group goes just behind them (PinnedFrames). With the overlay up, which
/// covers them, their pictures are drawn over the canvas's but under the panels, where the windows
/// are. A pinned group's box is in view space, and its apps are listed (CanvasGroup.Pinned), not
/// worked out from where they are: the canvas moving under the box mustn't take anything in.
sealed partial class Canvas
{
    /// This desktop's pinned groups.
    readonly List<CanvasGroup> pinnedGroups = new();
    /// The other desktops'.
    readonly PerDesktop<CanvasGroup> pinnedAway = new();
    /// Whether each pinned app was always-on-top before it was pinned: unpinned, it goes back to
    /// that. Read before Expanse first puts it on top (RememberTopmost), or it would read its own doing.
    readonly Dictionary<nint, bool> pinnedWereTopmost = new();
    /// The pinned app to give focus to when the canvas goes down, instead of a canvas window.
    CanvasWindow? pinnedFocus;

    /// Pinning or unpinning zoomed out: the apps' pictures grow from where the canvas shows them to
    /// their spot on the screen, or shrink back.
    sealed class PinMove
    {
        public required CanvasGroup Group;
        public required bool Pinning;
        /// View space. Unpinning, where they go is read from their frames as it goes.
        public required RectD FromBox, ToBox;
        public required List<(CanvasWindow w, RectD from, RectD to)> Apps;
        public readonly long Start = Stopwatch.GetTimestamp();
    }
    PinMove? pinMove;
    const double PinMoveSeconds = 0.22;

    IEnumerable<CanvasWindow> PinnedApps => pinnedGroups.SelectMany(g => g.Pinned!);

    CanvasWindow? PinnedApp(nint hwnd) => hwnd == 0 ? null : PinnedApps.FirstOrDefault(w => w.Hwnd == hwnd);

    /// The pinned app `hwnd` is, or belongs to (its dialog).
    CanvasWindow? PinnedAppOf(nint hwnd) => hwnd == 0 ? null : PinnedApp(hwnd) ?? PinnedApp(CanvasWindow.RootOwner(hwnd));

    CanvasGroup? PinnedGroupOf(CanvasWindow w) => pinnedGroups.FirstOrDefault(g => g.Pinned!.Contains(w));

    CanvasGroup? PinnedGroupAt(Vec p) => pinnedGroups.FirstOrDefault(g => g.Box.Contains(p));

    /// A group's box on the screen.
    RectD ViewBox(CanvasGroup g) => g.Pinned != null ? g.Box : camera.ToView(g.Box);

    /// Where going to a window puts it: the work area, less the strips pinned groups at its edges take.
    /// -1 where its middle is in the left half of the screen, 1 in the right: the side its dock button points to.
    int DockSide(CanvasGroup g) => g.Box.MidX < WorkView.MidX ? -1 : 1;

    /// The dock button: whether going to a window keeps clear of this group (where it's at a side of the screen).
    void ToggleKeepClear(CanvasGroup g)
    {
        g.KeepClear = !g.KeepClear;
        Log.Info($"pinned group \"{g.Name}\": going to a window {(g.KeepClear ? "keeps clear of it" : "goes under it")}");
        ScheduleSave();
        if (active) Redraw();
        else UpdatePinnedFrames();
    }

    /// A group slid out takes only the strip of it still on the screen.
    RectD GlideArea => Pinning.Reserve(WorkView, pinnedGroups.Where(g => g.KeepClear || g.SlideHome != null).Select(g => g.SlideHome != null ? Pinning.Visible(g.Box, WorkView) : g.Box), UiScale);

    /// Where a pinned group's box may be: the work area, less the room its name row takes above it.
    RectD PinnedRoom
    {
        get
        {
            double row = Math.Round((Groups.LabelHeight + Groups.LabelLift) * UiScale);
            var w = WorkView;
            return new RectD(w.X, w.Y + row, w.W, w.H - row);
        }
    }

    RectD ViewFrame(RECT f) => new(f.Left - monitor.Left, f.Top - monitor.Top, f.Width, f.Height);

    // MARK: The real windows

    /// Not for a hung app: waiting on it would stall us.
    uint Async(CanvasWindow w) => IsHungAppWindow(w.Hwnd) ? SWP_ASYNCWINDOWPOS : 0;

    void SetOnTop(CanvasWindow w, bool on)
    {
        const uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE;
        if (on)
        {
            RememberTopmost(w);
            uint async = Async(w);
            bool asked = SetWindowPos(w.Hwnd, HWND_TOPMOST, 0, 0, 0, 0, flags | async);
            int error = asked ? 0 : System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            // Nothing says so when it doesn't take: some apps answer by staying as they were. Told once.
            bool did = (ExStyle(w.Hwnd) & WS_EX_TOPMOST) != 0;
            if (did || !wontGoOnTop.Contains(w.Hwnd))
                Log.Info($"on top: {w}: always-on-top {did}, asked {asked}{(error != 0 ? $" (error {error})" : "")}{(async != 0 ? ", hung" : "")}" +
                    (did ? "" : $", admin {Elevation.AboveUs(w.Hwnd)}, owner {CanvasWindow.Describe(CanvasWindow.RootOwner(w.Hwnd))}, style {ExStyle(w.Hwnd):X}"));
            // That put it over the overlay, where its picture is drawn instead.
            if (active) SetWindowPos(overlay.Hwnd, HWND_TOPMOST, 0, 0, 0, 0, flags);
        }
        else
        {
            wontGoOnTop.Remove(w.Hwnd);
            bool was = pinnedWereTopmost.Remove(w.Hwnd, out bool before) && before;
            if (!was && IsWindow(w.Hwnd)) SetWindowPos(w.Hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, flags | Async(w));
        }
    }

    /// Whether `w` is always-on-top before it's pinned, the first time only. One Expanse raised over a
    /// slid group (it was on another monitor) is no longer among those: lowered with them, it would go
    /// behind the canvas's windows.
    void RememberTopmost(CanvasWindow w)
    {
        bool raised = raisedOthers.Remove(w.Hwnd, out bool before);
        pinnedWereTopmost.TryAdd(w.Hwnd, raised ? before : (ExStyle(w.Hwnd) & WS_EX_TOPMOST) != 0);
    }

    /// Pinned apps that couldn't be put in front: still asked each time, but not logged again, nor taken
    /// off always-on-top first (which would show).
    readonly HashSet<nint> wontGoOnTop = new();

    /// A pinned app can end up behind a canvas window: some apps clear always-on-top as they come to the
    /// front, taking it off a window takes it off its owner too, and a canvas window that's always-on-top
    /// itself comes over it when you go into it. Then the pinned apps are all put in front again, in the
    /// order they're in.
    void KeepPinnedOnTop()
    {
        if (paused || active || pinnedGroups.Count == 0) return;
        var behind = PinnedBehind();
        wontGoOnTop.RemoveWhere(h => behind.All(b => b.app.Hwnd != h));
        var told = behind.Where(b => !wontGoOnTop.Contains(b.app.Hwnd)).ToList();
        if (behind.Count > 0)
        {
            foreach (var (app, over, topmost) in told)
                Log.Info($"pinned {app} (always-on-top {topmost}) is behind {(over == 0 ? "nothing yet" : $"{CanvasWindow.Describe(over)} (always-on-top {(ExStyle(over) & WS_EX_TOPMOST) != 0})")}");
            RaisePinned(again: false);
            behind = PinnedBehind();
            if (behind.Any(b => told.Any(t => t.app == b.app)))
            {
                // Has the style without being in front: off and on again.
                RaisePinned(again: true);
                behind = PinnedBehind();
            }
            foreach (var b in behind) wontGoOnTop.Add(b.app.Hwnd);
            if (told.Count > 0 || behind.Count == 0) Log.Info(behind.Count == 0 ? "pinned apps: in front again"
                : $"pinned apps: still behind: {string.Join(", ", behind.Select(b => $"{b.app} behind {CanvasWindow.Describe(b.over)}"))}");
            UpdatePinnedFrames();
        }
        LogPinnedStack();
    }

    /// This desktop's pinned apps that aren't always-on-top, or are behind a canvas window lying over
    /// them (`over`; 0: none).
    List<(CanvasWindow app, nint over, bool topmost)> PinnedBehind()
    {
        var canvas = windows.Select(w => w.Hwnd).ToHashSet();
        var above = new List<(nint hwnd, RECT frame)>();
        var found = new List<(CanvasWindow, nint, bool)>();
        EnumWindows((h, _) =>
        {
            bool isCanvas = canvas.Contains(h);
            var app = isCanvas ? null : PinnedApp(h);
            if ((!isCanvas && app == null) || !IsWindowVisible(h) || IsIconic(h) || Dwm.IsCloaked(h)) return true;
            var f = Dwm.FrameBounds(h);
            if (app == null)
            {
                above.Add((h, f));
                return true;
            }
            nint over = above.FirstOrDefault(a => Overlaps(a.frame, f)).hwnd;
            bool topmost = (ExStyle(h) & WS_EX_TOPMOST) != 0;
            if ((over != 0 || !topmost) && !IsHungAppWindow(h)) found.Add((app, over, topmost));
            return true;
        }, 0);
        return found;
    }

    /// Every pinned app in front of everything, the lowest first, so they keep their order. `again`: asking
    /// wasn't enough (one that has the style but isn't in front), so it's taken off first.
    void RaisePinned(bool again)
    {
        var order = new List<CanvasWindow>();
        EnumWindows((h, _) =>
        {
            if (PinnedApp(h) is CanvasWindow w && IsWindowVisible(h) && !IsIconic(h) && !IsHungAppWindow(h)) order.Add(w);
            return true;
        }, 0);
        order.Reverse();
        const uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE;
        foreach (var w in order)
        {
            if (again) SetWindowPos(w.Hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, flags);
            SetOnTop(w, true);
        }
    }

    string lastPinnedStack = "";

    /// With Detailed Logging: what's on the main screen from the top down to the lowest pinned app, when
    /// that's changed.
    void LogPinnedStack()
    {
        if (!Log.ToFile) return;
        var lines = new List<string>();
        int keep = 0;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h) || Dwm.IsCloaked(h)) return true;
            var f = Dwm.FrameBounds(h);
            if (f.Width < 40 || f.Height < 30 || !OnMainScreen(f)) return true;
            bool pinned = PinnedApp(h) != null;
            string kind = pinned ? "pinned" : pinnedFrames.Hwnds.Contains(h) ? "frame" : windows.Any(w => w.Hwnd == h) ? "canvas" : "other";
            lines.Add($"{CanvasWindow.Describe(h)} {kind}{((ExStyle(h) & WS_EX_TOPMOST) != 0 ? " topmost" : "")}{(IsHungAppWindow(h) ? " hung" : "")}");
            if (pinned) keep = lines.Count;
            return true;
        }, 0);
        string stack = string.Join(" > ", lines.Take(keep + 1));
        if (stack == lastPinnedStack) return;
        lastPinnedStack = stack;
        Log.Info($"stack: {stack}");
    }

    /// A pinned app's real window to `view` (and its picture, with the overlay up). Where it is in
    /// the stack stays: SetOnTop says that.
    void PlacePinned(CanvasWindow w, RectD view)
    {
        w.Frame = view;
        int x = (int)Math.Round(view.X) + monitor.Left - w.Insets.Left, y = (int)Math.Round(view.Y) + monitor.Top - w.Insets.Top;
        w.Placed = new POINT(x + w.Insets.Left, y + w.Insets.Top);
        SetWindowPos(w.Hwnd, 0, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
        if (active && pinMove?.Apps.Any(a => a.w == w) != true) pictures.Moved(w, camera, View);
    }

    /// Pinned apps to their spots, and a pinned group's frame (`frameId`) by `frameBy`, all in one go
    /// so they move together, not one behind the other. A hung app is moved on its own, not waited on.
    void PlaceTogether(IReadOnlyList<(CanvasWindow w, RectD view)> apps, long frameId, Vec frameBy)
    {
        var live = apps.Where(a => IsWindow(a.w.Hwnd) && !IsHungAppWindow(a.w.Hwnd)).ToList();
        nint batch = BeginDeferWindowPos(live.Count + 1);
        foreach (var (w, view) in apps)
        {
            w.Frame = view;
            int x = (int)Math.Round(view.X) + monitor.Left - w.Insets.Left, y = (int)Math.Round(view.Y) + monitor.Top - w.Insets.Top;
            w.Placed = new POINT(x + w.Insets.Left, y + w.Insets.Top);
            const uint flags = SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE;
            if (batch != 0 && live.Any(a => a.w == w)) batch = DeferWindowPos(batch, w.Hwnd, 0, x, y, 0, 0, flags);
            else SetWindowPos(w.Hwnd, 0, x, y, 0, 0, flags | SWP_ASYNCWINDOWPOS);
            if (active && pinMove?.Apps.Any(a => a.w == w) != true) pictures.Moved(w, camera, View);
        }
        if (frameId != 0) batch = pinnedFrames.MoveBy(frameId, frameBy, batch);
        if (batch != 0) EndDeferWindowPos(batch);
    }

    /// The other monitors' windows put over a slid-out group, and whether each was always-on-top already.
    readonly Dictionary<nint, bool> raisedOthers = new();

    /// A slid-out group's apps and frame reach onto the monitor beside the screen's side, over its windows
    /// (they're always-on-top). Canvas windows there go behind that monitor's own; these, being on top
    /// of everything, can't, so those windows go always-on-top too, raised after them: above the group.
    /// They go back once nothing of theirs is under a slid group. Not while the overlay is up, which
    /// covers the main screen only, where the apps are drawn as pictures.
    void KeepOthersOverSlid()
    {
        var spill = new List<RectD>();
        if (!paused && !active)
            foreach (var g in pinnedGroups.Where(g => g.SlideHome != null || slideMove?.Group == g))
            {
                // On its way, either way: all it passes over.
                var at = slideMove is SlideMove m && m.Group == g ? m.From.Union(m.To) : g.Box;
                var box = at.Offset(new Vec(monitor.Left, monitor.Top));
                if (box.X < monitor.Left || box.Right > monitor.Right || box.Y < monitor.Top || box.Bottom > monitor.Bottom) spill.Add(box);
            }
        // All of the windows on the monitor a group reaches, not only the ones it covers: one beside a
        // raised one can't be brought above it by a click, if it isn't raised too.
        var reached = otherMonitors.Where(m => spill.Any(s => s.Intersects(m.ToRectD()))).Select(m => m.ToRectD()).ToList();
        var over = new List<nint>();
        if (reached.Count > 0)
            EnumWindows((h, _) =>
            {
                if (!elsewhere.Contains(h) || PinnedAppOf(h) != null || !IsWindowVisible(h) || IsIconic(h) || Dwm.IsCloaked(h) || IsHungAppWindow(h)) return true;
                var f = Dwm.FrameBounds(h).ToRectD();
                if (reached.Any(r => r.Intersects(f))) over.Add(h); // top first
                return true;
            }, 0);
        const uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE;
        var done = raisedOthers.Keys.Where(h => !over.Contains(h)).ToHashSet();
        if (done.Count > 0)
        {
            // Each goes on top of the ordinary windows: the lowest first, so they keep their order.
            var down = new List<nint>();
            EnumWindows((h, _) => { if (done.Contains(h)) down.Add(h); return true; }, 0);
            down.Reverse();
            foreach (nint h in down)
                if (!raisedOthers[h]) SetWindowPos(h, HWND_NOTOPMOST, 0, 0, 0, 0, flags);
            foreach (nint h in done) raisedOthers.Remove(h);
        }
        if (over.Count == 0) return;
        // Once raised, they keep their order among themselves as you click about (Windows sees to it), so
        // nothing's done unless a window is new to them, or a pinned app or frame has come over one. Then
        // all at once, in one go: one by one, each showed on top for a moment, and they flashed.
        var fresh = over.Where(h => !raisedOthers.ContainsKey(h)).ToList();
        if (fresh.Count == 0 && !PinnedOverRaised()) return;
        nint front = GetForegroundWindow();
        // The top one first and each behind the one before, so none shows over one it was behind.
        nint before = HWND_TOPMOST;
        foreach (nint h in fresh)
        {
            raisedOthers[h] = (ExStyle(h) & WS_EX_TOPMOST) != 0;
            SetWindowPos(h, before, 0, 0, 0, 0, flags);
            // Behind one that's always-on-top, it's that too; if not, the plain way.
            if ((ExStyle(h) & WS_EX_TOPMOST) == 0) SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, flags);
            if ((ExStyle(h) & WS_EX_TOPMOST) != 0) before = h;
        }
        // Their order as it was, but one you've just chosen that wasn't among them goes on top.
        if (fresh.Contains(front) && over.Remove(front)) over.Insert(0, front);
        nint batch = BeginDeferWindowPos(over.Count);
        for (int i = 0; i < over.Count && batch != 0; i++)
            batch = DeferWindowPos(batch, over[i], i == 0 ? HWND_TOPMOST : over[i - 1], 0, 0, 0, 0, flags);
        if (batch != 0) EndDeferWindowPos(batch);
    }

    /// A pinned app or frame is above one of the windows raised over slid groups, and over it (or
    /// will be on its way, sliding).
    bool PinnedOverRaised()
    {
        var sliding = new HashSet<nint>();
        Vec ahead = default;
        if (slideMove is SlideMove m)
        {
            sliding = m.Apps.Select(a => a.w.Hwnd).Append(pinnedFrames.HwndOf(m.Group.Id)).ToHashSet();
            ahead = m.To.Origin - m.Group.Box.Origin;
        }
        var pinned = PinnedApps.Select(w => w.Hwnd).Concat(pinnedFrames.Hwnds).ToHashSet();
        var seen = new List<RectD>();
        bool covered = false;
        EnumWindows((h, _) =>
        {
            if (pinned.Contains(h) && IsWindowVisible(h) && !IsIconic(h))
            {
                var f = Dwm.FrameBounds(h).ToRectD();
                seen.Add(sliding.Contains(h) ? f.Union(f.Offset(ahead)) : f);
            }
            else if (raisedOthers.ContainsKey(h) && seen.Any(r => r.Intersects(Dwm.FrameBounds(h).ToRectD())))
            {
                covered = true;
                return false;
            }
            return true;
        }, 0);
        return covered;
    }

    /// Back to ordinary windows, for quitting, pausing or handing over: the next run pins them again
    /// (ClaimForPinned).
    void LetGoOfPinned()
    {
        SlideAllIn();
        KeepOthersOverSlid();
        foreach (var w in PinnedApps.Concat(pinnedAway.Away.SelectMany(a => a.Item.Pinned!)).ToList()) SetOnTop(w, false);
    }

    /// The pictures to show: the canvas's, then the pinned apps' that are showing, over them.
    bool AttachPictures()
    {
        var shown = PinnedApps.Where(w => IsWindowVisible(w.Hwnd) && !IsIconic(w.Hwnd)).ToList();
        bool ok = pictures.Attach(Stack().Concat(shown));
        foreach (var w in shown) pictures.Pin(w, true);
        return ok;
    }

    /// The desktop changed while zoomed out: the pictures of the pinned apps that went with the old one
    /// (`before`) go, and those of the new one's come.
    void ShowPinnedPictures(IReadOnlyList<CanvasWindow> before)
    {
        var now = PinnedApps.ToList();
        foreach (var w in before.Except(now))
        {
            pictures.Remove(w);
            if (hovered == w) hovered = null;
        }
        foreach (var w in now.Except(before).Where(w => IsWindowVisible(w.Hwnd) && !IsIconic(w.Hwnd)))
        {
            pictures.Add(w);
            pictures.Pin(w, true);
            pictures.Moved(w, camera, View);
        }
    }

    // MARK: Sync

    /// Seen by Sync: its title and where it is. Nothing else changes here; a drag of it is settled
    /// when it ends (PinnedAppMoved).
    void KeepPinned(CanvasWindow w)
    {
        w.Title = Title(w.Hwnd);
        if (pinMove?.Apps.Any(a => a.w == w) == true || slideMove?.Apps.Any(a => a.w == w) == true) return; // still getting there
        w.Frame = ViewFrame(w.ReadScreenFrame());
    }

    /// A window that was in a pinned group when the layout was saved (CanvasGroup.Waiting) joins it
    /// again, where it was: one still open since (Expanse restarted) by its handle, or the same app's
    /// as it opens again (the first sync after launch, or an app started in the last minute), as
    /// saved spots on the canvas are matched (LayoutStore.TakeMatch). Only those: any other window
    /// that happens to be under a pinned group's box is the canvas's.
    bool ClaimForPinned(nint h)
    {
        if (IsZoomed(h) || !pinnedGroups.Any(g => g.Waiting is { Count: > 0 })) return false;
        // Cheaply first: every sync sees the other monitors' windows as new.
        GetWindowThreadProcessId(h, out uint pid);
        if (initialSyncDone && !JustStarted(pid)) return false;
        var w = new CanvasWindow(h);
        w.Frame = ViewFrame(w.ReadScreenFrame());
        var open = OpenKeys();
        open.UnionWith(PinnedApps.Select(a => ((long)a.Hwnd, a.Pid)));
        foreach (var g in pinnedGroups)
        {
            if (g.Waiting is not { Count: > 0 } waiting || LayoutStore.TakeMatch(w, open, waiting) is not SavedWindow e) continue;
            w.Preferred = w.Frame.Size;
            elsewhere.Remove(h);
            g.Pinned!.Add(w);
            PlacePinned(w, Pinning.OnScreen(new RectD(e.X, e.Y, w.Frame.W, w.Frame.H), PinnedRoom));
            SetOnTop(w, true);
            g.Box = g.Box.Union(Groups.Fit([w.Frame]));
            Log.Info($"window {w} at {w.Frame}: back in pinned group \"{g.Name}\"");
            return true;
        }
        return false;
    }

    /// Since when apps of another desktop's pinned groups have been showing here (0: none are).
    long awayPinnedSince;

    /// `h` is in a pinned group of another desktop, so it's not taken onto this one's canvas: just after
    /// switching, its group may not be here yet (Windows shows the windows before it says which desktop
    /// you're on). One you show on every desktop, or moved to this one, stays in its group too.
    bool PinnedElsewhere(nint h) => pinnedAway.Away.Any(a => a.Item.Pinned!.Any(w => w.Hwnd == h));

    /// After each sync: while such apps have only just come, the desktop is asked again shortly, for their
    /// groups to come too. Not for good: one shown on every desktop is always here.
    void AwaitPinnedDesktop(bool appsHere)
    {
        if (!appsHere)
        {
            awayPinnedSince = 0;
            return;
        }
        long now = Environment.TickCount64;
        if (awayPinnedSince == 0) awayPinnedSince = now;
        if (now - awayPinnedSince < 1500) ScheduleSync();
    }

    /// Closed apps leave their pinned groups. Minimised or hidden ones stay: they come back to it.
    void PrunePinned()
    {
        foreach (var g in pinnedGroups)
            foreach (var gone in g.Pinned!.Where(w => !IsWindow(w.Hwnd)).ToList())
            {
                g.Pinned!.Remove(gone);
                pinnedWereTopmost.Remove(gone.Hwnd);
                wontGoOnTop.Remove(gone.Hwnd);
                Log.Info($"pinned group \"{g.Name}\": {gone} closed");
            }
    }

    /// A drag of a canvas window by its title bar, at 100%, ended with its centre in a pinned
    /// group's box: it joins it (Sync).
    CanvasGroup? PinnedGroupTaking(CanvasWindow w, RECT f) =>
        !w.Maximised && w.Owner == null && userSized.Contains(w.Hwnd) ? PinnedGroupAt(ViewFrame(f).Center) : null;

    void JoinPinned(CanvasGroup g, CanvasWindow w, RectD view)
    {
        w.Frame = view;
        w.Owned.Clear();
        g.Pinned!.Add(w);
        SetOnTop(w, true);
        g.Box = g.Box.Union(Groups.Fit([view]));
        Log.Info($"{w} joined pinned group \"{g.Name}\", now {g.Box}");
        ScheduleSave();
    }

    /// You dragged a pinned app by its title bar and let go. Still in its group's box, the box
    /// grows to hold it if it must; out of it, it leaves the group for the canvas where you dropped
    /// it, or for the other monitor it's now on.
    void PinnedAppMoved(CanvasWindow w)
    {
        var g = PinnedGroupOf(w)!;
        var f = w.ReadScreenFrame();
        var view = ViewFrame(f);
        // Slid out through the screen's side, it's mostly off the screen, on the monitor there maybe: resized
        // or moved within its box, it's still in the group.
        if (g.SlideHome is RectD home && g.Box.Contains(view.Center))
        {
            w.Frame = view;
            var by = home.Origin - g.Box.Origin;
            g.Box = g.Box.Union(Groups.Fit([view]));
            g.SlideHome = home.Union(Groups.Fit([view.Offset(by)]));
            ScheduleSave();
            // You worked in it, so it has focus: the group comes back, as it does for a click.
            SlideIn(g);
            return;
        }
        if (Screens.CentreElsewhere(f.ToRectD(), monitor.ToRectD(), OtherMonitorRects))
        {
            g.Pinned!.Remove(w);
            SetOnTop(w, false);
            elsewhere.Add(w.Hwnd);
            Log.Info($"{w} left pinned group \"{g.Name}\" for another monitor");
            ScheduleSave();
            if (active) Redraw();
            return;
        }
        if (g.Box.Contains(view.Center))
        {
            w.Frame = view;
            g.Box = g.Box.Union(Groups.Fit([view]));
            ScheduleSave();
            if (active) Redraw();
            return;
        }
        LeaveForCanvas(g, w, view);
        if (active) lastOverlayFocus = Focus(overlay.Hwnd);
        else UpdatePinnedFrames();
    }

    /// Out of its pinned group onto the canvas, its top-left where it is on the screen now (`view`),
    /// at whatever zoom (as FinishDragIn does).
    void LeaveForCanvas(CanvasGroup g, CanvasWindow w, RectD view)
    {
        g.Pinned!.Remove(w);
        SetOnTop(w, false);
        var at = camera.ToCanvas(view.Origin);
        w.Frame = new RectD(Layout.Round(at.X), Layout.Round(at.Y), view.W, view.H);
        w.Placed = null;
        windows.Add(w);
        NoteUserMove(w, w.Frame.Origin);
        Log.Info($"{w} left pinned group \"{g.Name}\" for the canvas, at {w.Frame}");
        if (active)
        {
            pictures.Add(w);
            pictures.Pin(w, false);
            pictures.Moved(w, camera, View);
            SettleGroups();
            Relayout();
        }
        else SettleGroups();
        ScheduleSave();
    }

    /// Zoomed out, a window's picture dropped with its centre in a pinned group's box: its real
    /// window goes there at 100%, round where you dropped it, and joins the group. True if it did.
    bool DropIntoPinned(CanvasWindow w, RectD startFrame)
    {
        var shown = camera.ToView(w.Frame);
        if (w.Maximised || PinnedGroupAt(shown.Center) is not CanvasGroup g) return false;
        var view = Pinning.OnScreen(new RectD(shown.MidX - w.Frame.W / 2, shown.MidY - w.Frame.H / 2, w.Frame.W, w.Frame.H), View);
        TakeOffCanvas(w);
        PlacePinned(w, view);
        JoinPinned(g, w, view);
        pictures.Pin(w, true);
        Relayout();
        return true;
    }

    /// Out of the canvas's lists and its pictures' (its own stays, until it's where it's going).
    void TakeOffCanvas(CanvasWindow w)
    {
        windows.Remove(w);
        selected.Remove(w);
        if (hovered == w) hovered = null;
        foreach (var o in w.Owned) pictures.Remove(o);
        w.Owned.Clear();
        w.Placed = null;
    }

    // MARK: Sliding out of the screen's side

    /// A pinned group snapped against the left or right of the screen sliding out through it, until
    /// about a tenth of its apps shows, or back in (a click on the strip along that side, on what's
    /// left of it, or on one of its apps).
    sealed class SlideMove
    {
        public required CanvasGroup Group;
        public required RectD From, To;
        public required bool Out;
        public required List<(CanvasWindow w, RectD start)> Apps;
        public long Start = Stopwatch.GetTimestamp();
    }
    SlideMove? slideMove;
    /// Apps of a slid group the user is resizing or moving: focusing them mustn't bring the group in.
    readonly HashSet<nint> slidDragged = new();
    const double SlideSeconds = 0.22;

    void SlideOut(CanvasGroup g)
    {
        if (g.Pinned == null || g.SlideHome != null || pinMove != null) return;
        FinishSlide();
        int side = Pinning.SnappedSide(g.Box, WorkView, UiScale);
        if (side == 0) return;
        var to = Pinning.Slid(g.Box, side, WorkView);
        Log.Info($"pinned group \"{g.Name}\" slides out {(side < 0 ? "left" : "right")}");
        g.SlideHome = g.Box;
        slideMove = new SlideMove { Group = g, From = g.Box, To = to, Out = true, Apps = g.Pinned.Select(w => (w, w.Frame)).ToList() };
        // Before it moves, not once it's there: it would pass over the monitor's windows, then go behind them.
        KeepOthersOverSlid();
        slideMove.Start = Stopwatch.GetTimestamp();
        StepSlide();
        LeaveSlidFocus(g);
    }

    /// Focus out of a group sliding away, to the canvas window in front on the screen: left in one of its
    /// apps, a click on that app wouldn't be a change of focus, and the group wouldn't come back.
    void LeaveSlidFocus(CanvasGroup g)
    {
        if (PinnedAppOf(GetForegroundWindow()) is not CanvasWindow app || !g.Pinned!.Contains(app)) return;
        var view = monitor.ToRectD();
        nint next = 0;
        EnumWindows((h, _) =>
        {
            if (CanvasWindowOf(h) is CanvasWindow w && w.Hwnd == h && IsWindowVisible(h) && !IsIconic(h)
                && Dwm.FrameBounds(h).ToRectD().Intersects(view))
            {
                next = h;
                return false;
            }
            return true;
        }, 0);
        if (next == 0) return;
        // Ours: the view stays where it is (FocusSettled).
        lastOwnFocus = (next, Stopwatch.GetTimestamp());
        lastFocusedWindow = next;
        Log.Info($"focus out of slid group \"{g.Name}\": {Focus(next)}");
    }

    void SlideIn(CanvasGroup g, bool now = false)
    {
        if (g.SlideHome is not RectD home) return;
        // Any slide still going, this group's or another's, ends where it was going first: replaced, it
        // would stop half way.
        FinishSlide();
        slidDragged.Clear();
        Log.Info($"pinned group \"{g.Name}\" slides back in");
        g.SlideHome = null;
        slideMove = new SlideMove { Group = g, From = g.Box, To = home, Out = false, Apps = g.Pinned!.Select(w => (w, w.Frame)).ToList() };
        if (now) FinishSlide();
        else StepSlide();
    }

    bool Sliding => slideMove != null;

    void StepSlide()
    {
        if (slideMove is not SlideMove m) return;
        double t = Math.Min(1, Stopwatch.GetElapsedTime(m.Start).TotalSeconds / SlideSeconds);
        MoveSlide(m, Pinning.Between(m.From, m.To, Easing.Ease(t)));
        if (t >= 1) FinishSlide();
    }

    void MoveSlide(SlideMove m, RectD box)
    {
        var g = m.Group;
        var by = box.Origin - m.From.Origin;
        var moved = box.Origin - g.Box.Origin;
        g.Box = box;
        PlaceTogether(m.Apps.Select(a => (a.w, a.start.Offset(by))).ToList(), active ? 0 : g.Id, moved);
        if (active) Redraw();
    }

    /// The slide done now: where it was going, and its frame drawn again for how it stands.
    void FinishSlide()
    {
        if (slideMove is not SlideMove m) return;
        slideMove = null;
        MoveSlide(m, m.To);
        ScheduleSave();
        if (!active) UpdatePinnedFrames();
        KeepOthersOverSlid();
    }

    /// Every slid group back where it was, at once: before the apps are let go of, so none is left off the screen.
    void SlideAllIn()
    {
        FinishSlide();
        foreach (var g in pinnedGroups.Concat(pinnedAway.Away.Select(a => a.Item)).ToList()) SlideIn(g, now: true);
    }

    // MARK: Pinning and unpinning

    /// The pin button on a group on the canvas: it comes off the canvas and stays on the screen at
    /// 100%, where it is now (zoomed out, it grows there round its centre). Maximised apps stay on
    /// the canvas: a maximised window is held to the screen by Windows.
    void PinGroup(CanvasGroup g)
    {
        FinishPinMove();
        var members = MembersOf(g).Where(w => !w.Maximised).ToList();
        var shown = camera.ToView(g.Box);
        var spot = Math.Abs(camera.Scale - 1) < 0.001 ? Pinning.OnScreen(shown, PinnedRoom) : Pinning.Grown(shown, g.Box.Size, PinnedRoom);
        var apps = members.Select(w => (w, from: camera.ToView(w.Frame),
            to: new RectD(spot.X + (w.Frame.X - g.Box.X), spot.Y + (w.Frame.Y - g.Box.Y), w.Frame.W, w.Frame.H))).ToList();
        groups.Remove(g);
        selectedGroups.Remove(g);
        foreach (var w in members)
        {
            TakeOffCanvas(w);
            RememberTopmost(w);
        }
        Originals.Save(windows);
        g.Pinned = members;
        g.Box = shown;
        pinnedGroups.Add(g);
        foreach (var w in members) pictures.Raise(w);
        Log.Info($"pinned group \"{g.Name}\" at {spot} with {members.Count} apps");
        pinMove = new PinMove { Group = g, Pinning = true, FromBox = shown, ToBox = spot, Apps = apps };
        StepPinMove();
        ScheduleSave();
    }

    /// The pin button on a pinned group: back onto the canvas, under where it is on the screen (or
    /// the nearest clear spot), at the zoom of the moment.
    void UnpinGroup(CanvasGroup g)
    {
        FinishPinMove();
        SlideIn(g, now: true);
        var spot = g.Box;
        var c = camera.ToCanvas(spot.Center);
        var box = Pinning.Land(new RectD(Layout.Round(c.X - spot.W / 2), Layout.Round(c.Y - spot.H / 2), spot.W, spot.H),
            groups.Select(o => o.Box).ToList(), windows.Select(GroupFrame).ToList());
        var apps = g.Pinned!.Where(w => IsWindow(w.Hwnd)).ToList();
        pinnedGroups.Remove(g);
        g.Pinned = null;
        g.Box = box;
        groups.Add(g);
        var from = new List<(CanvasWindow w, RectD from, RectD to)>();
        foreach (var w in apps)
        {
            var view = w.Frame;
            w.Frame = new RectD(box.X + (view.X - spot.X), box.Y + (view.Y - spot.Y), view.W, view.H);
            w.Placed = null;
            windows.Add(w);
            NoteUserMove(w, w.Frame.Origin);
            if (active)
            {
                // Its picture back among the canvas's, from where the window is.
                pictures.Add(w);
                pictures.Pin(w, false);
                pictures.Float(w, view, camera);
                from.Add((w, view, view));
            }
            SetOnTop(w, false);
        }
        Log.Info($"unpinned group \"{g.Name}\": on the canvas at {box}");
        SettleGroups();
        if (active && from.Count > 0)
        {
            pinMove = new PinMove { Group = g, Pinning = false, FromBox = spot, ToBox = spot, Apps = from };
            StepPinMove();
        }
        else
        {
            foreach (var w in apps) Place(w);
            if (active) Relayout();
        }
        ScheduleSave();
    }

    /// One frame of a pin or unpin.
    void StepPinMove()
    {
        if (pinMove is not PinMove m) return;
        double t = Math.Min(1, Stopwatch.GetElapsedTime(m.Start).TotalSeconds / PinMoveSeconds);
        double e = Easing.Ease(t);
        if (m.Pinning) m.Group.Box = Pinning.Between(m.FromBox, m.ToBox, e);
        foreach (var (w, from, to) in m.Apps)
            pictures.Float(w, Pinning.Between(from, m.Pinning ? to : camera.ToView(w.Frame), e), camera);
        if (t < 1)
        {
            Redraw();
            return;
        }
        FinishPinMove();
        if (active) Relayout();
    }

    /// The pin or unpin done now. Pinning, the real windows go to their spots (under the overlay,
    /// if it's up), and their pictures over the canvas's.
    void FinishPinMove()
    {
        if (pinMove is not PinMove m) return;
        pinMove = null;
        foreach (var (w, _, _) in m.Apps) pictures.Float(w, null, camera);
        if (!m.Pinning) return;
        m.Group.Box = m.ToBox;
        foreach (var (w, _, to) in m.Apps)
        {
            PlacePinned(w, to);
            SetOnTop(w, true);
            pictures.Pin(w, true);
        }
    }

    /// A pinned group moved (its body dragged zoomed out): its apps go with it.
    void MovePinned(Drag d, Vec p)
    {
        var g = d.Group!;
        var others = pinnedGroups.Where(o => o != g).Select(o => o.Box).ToList();
        var delta = p - d.StartPoint;
        if (d.What == DragKind.GroupResize)
        {
            d.Moved = true;
            g.Box = Groups.ResizeBox(d.StartBox, g.Box, d.Edges, delta, d.Required, others);
            Redraw();
            return;
        }
        if (!d.Moved && p.DistanceTo(d.StartPoint) < 3) return;
        d.Moved = true;
        g.Box = Pinning.OnScreen(Groups.Move(g.Box, d.StartBox.Offset(delta), others), PinnedRoom);
        var by = g.Box.Origin - d.StartBox.Origin;
        PlaceTogether(d.Moving.Select(m => (m.w, m.start.Offset(by))).ToList(), 0, default);
        Redraw();
    }

    /// The pinned app whose picture is under `p`, zoomed out: the top one.
    CanvasWindow? PinnedAppAt(Vec p)
    {
        foreach (var g in Enumerable.Reverse(pinnedGroups))
            foreach (var w in Enumerable.Reverse(g.Pinned!))
                if (w.Frame.Contains(p) && IsWindowVisible(w.Hwnd) && !IsIconic(w.Hwnd)) return w;
        return null;
    }

    /// Zoomed out, a pinned app's picture dragged: it follows, at 100%. Its window follows when it's let go.
    void DragPinnedApp(Drag d, Vec p)
    {
        if (!d.Moved && p.DistanceTo(d.StartPoint) < 3) return;
        var (w, start) = d.Moving[0];
        if (!d.Moved) pictures.Raise(w);
        d.Moved = true;
        w.Frame = start.Offset(p - d.StartPoint);
        pictures.Moved(w, camera, View);
        Redraw();
    }

    /// A click goes back to 100% in it. Dropped in its group's box it stays (the box grows to hold it
    /// if it must); dropped outside, it goes onto the canvas there.
    void EndPinnedAppDrag(Drag d, bool click)
    {
        var (w, _) = d.Moving[0];
        if (PinnedGroupOf(w) is not CanvasGroup g) return;
        if (!d.Moved)
        {
            if (click) FocusPinned(g, w);
            return;
        }
        var view = w.Frame;
        if (!g.Box.Contains(view.Center))
        {
            LeaveForCanvas(g, w, view);
            return;
        }
        PlacePinned(w, view);
        g.Box = g.Box.Union(Groups.Fit([view]));
        Log.Info($"{w} moved in pinned group \"{g.Name}\" to {view}");
        ScheduleSave();
        Relayout();
    }

    // MARK: At 100%

    readonly PinnedFrames pinnedFrames = new();
    /// A pinned group being dragged by its frame at 100%: where the pointer and it started.
    (CanvasGroup g, Vec from, RectD box, List<(CanvasWindow w, RectD start)> apps)? frameDrag;

    /// A pinned group's border being dragged at 100%: the pointer and the box where it started, the edges, and the
    /// box round its apps, which it can't shrink past.
    (CanvasGroup g, Vec from, RectD box, ResizeEdges edges, RectD? required)? frameResize;

    /// Which edges of the group's box a screen point is on.
    ResizeEdges PinnedFrameEdges(long id, Vec screenPoint)
    {
        if (paused || active || pinnedGroups.FirstOrDefault(g => g.Id == id) is not CanvasGroup g || g.SlideHome != null) return ResizeEdges.None;
        return Resizing.EdgesAt(g.Box, screenPoint - new Vec(monitor.Left, monitor.Top), BorderGrab * UiScale);
    }

    void WirePinnedFrames()
    {
        pinnedFrames.EdgesAt = PinnedFrameEdges;
        pinnedFrames.Pressed = PressPinnedFrame;
        pinnedFrames.Dragged = p =>
        {
            if (frameResize is var (rg, rfrom, rbox, redges, required) && pinnedGroups.Contains(rg))
            {
                var rOthers = pinnedGroups.Where(o => o != rg).Select(o => o.Box).ToList();
                var resized = Groups.ResizeBox(rbox, rg.Box, redges, p - rfrom, required, rOthers);
                if (resized == rg.Box) return;
                rg.Box = resized;
                UpdatePinnedFrames();
                return;
            }
            if (frameDrag is not var (g, from, box, apps) || !pinnedGroups.Contains(g)) return;
            var others = pinnedGroups.Where(o => o != g).Select(o => o.Box).ToList();
            var moved = Pinning.OnScreen(Groups.Move(g.Box, box.Offset(p - from), others), PinnedRoom);
            var by = moved.Origin - g.Box.Origin;
            if (by == default) return;
            g.Box = moved;
            PlaceTogether(apps.Select(a => (a.w, a.start.Offset(moved.Origin - box.Origin))).ToList(), g.Id, by);
        };
        pinnedFrames.Released = () =>
        {
            if (frameResize is var (resized, _, startBox, _, _))
            {
                frameResize = null;
                if (resized.Box == startBox) return;
                Log.Info($"pinned group \"{resized.Name}\" resized at 100% to {resized.Box}");
                ScheduleSave();
                UpdatePinnedFrames();
                return;
            }
            if (frameDrag is not var (g, _, box, _)) return;
            frameDrag = null;
            if (g.Box == box) return;
            Log.Info($"pinned group \"{g.Name}\" moved at 100% to {g.Box}");
            ScheduleSave();
            UpdatePinnedFrames();
        };
    }

    /// A press on a pinned group's frame at 100%: its buttons as zoomed out; the colour circle opens
    /// the canvas with the colour wheel; the name or the box's edge starts a drag.
    void PressPinnedFrame(long id, PinnedFramePart part, Vec at)
    {
        if (paused || active || pinnedGroups.FirstOrDefault(g => g.Id == id) is not CanvasGroup g) return;
        Log.Info($"pinned group \"{g.Name}\": {part} pressed at 100%");
        // What's left of a slid group, wherever it's pressed, brings it back.
        if (g.SlideHome != null)
        {
            SlideIn(g);
            return;
        }
        switch (part)
        {
            case PinnedFramePart.Edge:
                SlideOut(g);
                break;
            case PinnedFramePart.Dock:
                ToggleKeepClear(g);
                break;
            case PinnedFramePart.Border:
                frameResize = (g, at, g.Box, PinnedFrameEdges(g.Id, at),
                    g.Pinned!.Count == 0 ? null : Groups.Fit(g.Pinned.Select(w => w.Frame)));
                break;
            case PinnedFramePart.Pin:
                UnpinGroup(g);
                UpdatePinnedFrames();
                break;
            case PinnedFramePart.Trash:
                DeleteGroup(g);
                break;
            case PinnedFramePart.Colour:
                Enter(zoomOut: true);
                if (active && pictures.DrawsDecorations) OpenColourWheel(g);
                break;
            default:
                frameDrag = (g, at, g.Box, g.Pinned!.Select(w => (w, w.Frame)).ToList());
                break;
        }
    }

    /// The frames at 100% for this desktop's pinned groups, each just behind its lowest app.
    void UpdatePinnedFrames()
    {
        if (paused)
        {
            pinnedFrames.Hide();
            return;
        }
        double k = UiScale;
        var screen = new Vec(monitor.Left, monitor.Top);
        var wanted = new List<(PinnedFrameLook, nint)>();
        foreach (var g in pinnedGroups)
        {
            var (swatch, label, pin, dock, trash) = LabelRects(g, k);
            int side = g.SlideHome == null ? Pinning.SnappedSide(g.Box, WorkView, k) : 0;
            var handle = side == 0 ? default : Pinning.Handle(g.Box, side, k).Offset(screen);
            wanted.Add((new PinnedFrameLook(g.Id, g.Box.Offset(screen), g.Name, swatch.Offset(screen), label.Offset(screen),
                pin.Offset(screen), trash.Offset(screen), g.Colour, handle, side, dock.Offset(screen), g.KeepClear, DockSide(g) > 0), LowestPinned(g)));
        }
        pinnedFrames.Show(wanted, k);
    }

    /// The group's app lowest in the stack that's showing (0: none).
    nint LowestPinned(CanvasGroup g)
    {
        var mine = g.Pinned!.Select(w => w.Hwnd).ToHashSet();
        nint lowest = 0;
        EnumWindows((h, _) =>
        {
            if (mine.Contains(h) && IsWindowVisible(h) && !IsIconic(h)) lowest = h;
            return true;
        }, 0);
        return lowest;
    }

    /// A pinned group was clicked, or one of its apps chosen in the groups panel: back to 100% where
    /// you are, in that app (they're all on screen already).
    void FocusPinned(CanvasGroup g, CanvasWindow? app = null)
    {
        pinnedFocus = app ?? g.Pinned!.LastOrDefault(w => IsWindow(w.Hwnd));
        Log.Info($"go to pinned group \"{g.Name}\": {pinnedFocus?.ToString() ?? "no apps"}");
        Exit(null);
    }

    /// Zoomed out, a pinned app got focus (Alt-Tab, say): back to 100% where you are, staying in it.
    /// Not while you're dragging it about, or if you did.
    bool PinnedFocusZoomedOut(nint hwnd)
    {
        if (!active || gliding || animation != null || drag != null || PinnedAppOf(hwnd) is not CanvasWindow app) return false;
        if (ButtonHeld)
        {
            SetTimer(overlay.Hwnd, FocusTimer, 30, 0);
            return true;
        }
        if (MovedSinceFocus(hwnd)) return true;
        Log.Info($"{app} (pinned) got focus zoomed out: back to 100%");
        pinnedFocus = app;
        Exit(null);
        return true;
    }

    /// The screen changed size: pinned groups come back onto it.
    void KeepPinnedOnScreen()
    {
        foreach (var g in pinnedGroups.Where(g => g.SlideHome == null))
        {
            var moved = Pinning.OnScreen(g.Box, PinnedRoom);
            if (moved == g.Box) continue;
            var by = moved.Origin - g.Box.Origin;
            g.Box = moved;
            foreach (var w in g.Pinned!.Where(w => IsWindow(w.Hwnd))) PlacePinned(w, w.Frame.Offset(by));
            Log.Info($"pinned group \"{g.Name}\" brought onto the screen at {moved}");
        }
    }

    // MARK: Hit-testing and drawing

    /// What's under `p` of the pinned groups: they're over everything else on the canvas.
    (CanvasGroup group, GroupPart part, ResizeEdges edges)? PinnedHitAt(Vec p)
    {
        double k = UiScale;
        foreach (var g in pinnedGroups)
        {
            var (swatch, label, pin, dock, trash) = LabelRects(g, k);
            if (swatch.Contains(p)) return (g, GroupPart.Colour, ResizeEdges.None);
            if (pin.Contains(p)) return (g, GroupPart.Pin, ResizeEdges.None);
            if (dock.Contains(p)) return (g, GroupPart.Dock, ResizeEdges.None);
            if (trash.Contains(p)) return (g, GroupPart.Trash, ResizeEdges.None);
            if (label.Contains(p)) return (g, GroupPart.Label, ResizeEdges.None);
        }
        foreach (var g in pinnedGroups)
        {
            var edges = Resizing.EdgesAt(g.Box, p, BorderGrab * k);
            if (edges != ResizeEdges.None) return (g, GroupPart.Border, edges);
            if (g.Box.Contains(p)) return (g, GroupPart.Body, ResizeEdges.None);
        }
        return null;
    }

    void AddPinnedDrawings(List<GroupDrawing> drawings, double k)
    {
        foreach (var g in pinnedGroups)
        {
            var (swatch, label, pin, dock, trash) = LabelRects(g, k);
            // Growing to its spot, its apps are still pictures: its box stays under them till they're there.
            drawings.Add(new GroupDrawing(g.Id, g.Box, g.Name, swatch, label, pin, trash, g == hoveredGroup || drag?.Group == g, g.Colour, Pinned: pinMove?.Group != g,
                Dock: dock, KeepClear: g.KeepClear, DockRight: DockSide(g) > 0));
        }
    }
}
