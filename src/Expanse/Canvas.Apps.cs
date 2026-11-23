using static Expanse.Native.User32;

namespace Expanse;

/// Windows of apps with special support (IAppSupport) coming back: they wait where they opened until
/// their app's support knows which they are, then take that window's spot.
sealed partial class Canvas
{
    const nint WaitingTimer = 11;

    readonly AppSupports appSupports = new();
    readonly WaitingWindows waitingWindows = new();

    void RefreshMarks() => appSupports.Refresh(windows.Concat(departed.Values).Concat(PinnedApps));

    /// Whether `w` should be matched to a saved spot later, once it's known: its app has only just
    /// started, so its titles say little yet.
    bool ShouldWait(CanvasWindow w) =>
        w.Marks == null && w.Owner == null && JustStarted(w.Pid) && appSupports.For(w) != null && HasSpotsFor(w);

    /// Whether any saved spot could be its own, and none is for certain (by its handle).
    bool HasSpotsFor(CanvasWindow w)
    {
        var spots = savedPool.Concat(pinnedGroups.SelectMany(g => g.Waiting ?? []))
            .Where(e => string.Equals(e.App, w.AppPath, StringComparison.OrdinalIgnoreCase) && e.Class == w.Class).ToList();
        return spots.Count > 0 && spots.All(e => e.Hwnd != w.Hwnd || e.Pid != w.Pid);
    }

    /// True if it's to wait. `mayTake`: it may take any spot of its app's if its own isn't known (not with
    /// Arrange New Windows off; being known makes it one coming back, not a new one).
    bool WaitToBeKnown(CanvasWindow w, bool mayTake)
    {
        if (!ShouldWait(w)) return false;
        var support = appSupports.For(w)!;
        waitingWindows.Add(w.Hwnd, Environment.TickCount64, support.PatienceMs, mayTake);
        SetTimer(overlay.Hwnd, WaitingTimer, 500, 0);
        Log.Info($"{w}: waits for {support.Name} support to know it");
        return true;
    }

    /// A support knows more: for the windows waiting on it, and for what's saved of the rest.
    void AppsChanged()
    {
        SettleWaiting();
        ScheduleSave();
    }

    /// On WaitingTimer, and when a support knows more.
    void SettleWaiting()
    {
        if (!waitingWindows.Any)
        {
            KillTimer(overlay.Hwnd, WaitingTimer);
            return;
        }
        long now = Environment.TickCount64;
        foreach (var w in windows) waitingWindows.Saw(w.Hwnd, w.Frame, now);
        // Not in the middle of anything: the timer comes round again.
        if (paused || gliding || animation != null || pinMove != null || drag != null || UserDragging || ButtonHeld) return;
        RefreshMarks();
        var due = waitingWindows.Due(now, h => windows.Any(w => w.Hwnd == h && w.Marks != null));
        // Those most like a saved window first, so one only somewhat like it doesn't take its spot.
        var its = due.Select(d => (d, w: windows.FirstOrDefault(w => w.Hwnd == d.hwnd))).Where(x => x.w != null)
            .Select(x => (x.d, w: x.w!, like: LayoutStore.BestLikeness(x.w!, savedPool.Concat(pinnedGroups.SelectMany(g => g.Waiting ?? [])))))
            .OrderByDescending(x => x.like).ToList();
        if (its.Count == 0) return;
        var taken = new HashSet<nint>();
        bool rejoined = false;
        foreach (var (d, w, like) in its)
        {
            string how = d.known ? $"known (likeness {like:0.00})" : "not known in time";
            if (RejoinPinned(w))
            {
                Log.Info($"{w}: {how}, back in its pinned group");
                rejoined = true;
            }
            else if (TakeSavedSpot(w, OpenKeys(), knownOnly: !d.mayTake))
            {
                Log.Info($"{w}: {how}, back at its saved spot {w.Frame}");
                taken.Add(w.Hwnd);
            }
            else Log.Info($"{w}: {how}, {(d.mayTake ? "no saved spot" : "none that's its own, and new windows aren't arranged")}: stays where it opened");
        }
        // The view stayed while they waited: now, as if the one you're in had just got focus.
        if (!active && its.Any(x => x.w.Hwnd == GetForegroundWindow())) SetTimer(overlay.Hwnd, FocusTimer, 120, 0);
        if (taken.Count == 0 && !rejoined) return;
        // As one that took its spot as it opened (Arrive).
        if (taken.Count > 0) FitLayoutToScreen(tidy: false, rederiving: taken);
        if (active)
        {
            foreach (var w in windows.Where(w => taken.Contains(w.Hwnd))) PictureMoved(w);
            SettleGroups();
            Relayout();
        }
        else
        {
            Commit();
            UpdateStackBars();
            UpdatePinnedFrames();
        }
        ScheduleSave();
    }
}
