using static Expanse.Native.User32;

namespace Expanse;

/// Windows stacked inside each other, zoomed out: a row of tabs below them to switch
/// between them (see Stacks). They stack with the window in front, so what's in front of that covers them.
/// At 100%, where there's no overlay, they're small windows of their own (StackBars), while you're in
/// the stack or in another window of its canvas group.
sealed partial class Canvas
{
    readonly record struct StackTab(CanvasWindow Window, CanvasWindow OnTop, string Text, RectD Rect, IconImage? Icon);

    /// Not under a drag: where a window lands decides its stack.
    List<StackTab> StackTabs(double k)
    {
        var tabs = new List<StackTab>();
        if (!active || gliding || drag != null) return tabs;
        // A maximised window is meant to cover what's behind it.
        var open = windows.Where(w => !w.Maximised).ToList();
        var stacks = Stacks.Find(open.Select(w => w.Frame).ToList(), open.Select(w => (long)w.Hwnd).ToList());
        foreach (var stack in stacks)
        {
            tabs.AddRange(TabsOf(stack, open, View, k));
        }
        return tabs;
    }

    /// The tabs of a stack among `open`, kept on `view`. None if it's out of that altogether.
    List<StackTab> TabsOf(WindowStack stack, List<CanvasWindow> open, RectD view, double k)
    {
        var tabs = new List<StackTab>();
        var members = stack.Members.Select(i => open[i]).ToList();
        var bounds = camera.ToView(members.Select(w => w.Frame).Aggregate((a, b) => a.Union(b)));
        if (!bounds.Intersects(view)) return tabs;
        var texts = members.Select(w => TabText(w, members)).ToList();
        var icons = members.Select(w => appIcons.Get(w, (int)Math.Round(GroupTree.IconSize * k))).ToList();
        // A little to spare: the tab's text is drawn by another API, which may measure it wider.
        var rects = Stacks.TabRects(bounds,
            texts.Select((t, i) => TextWidth(t, k) + Math.Ceiling(2 * k) + (icons[i] != null ? Stacks.TabIconRoom * k : 0)).ToList(), view, k, Stacks.FitRoom(k));
        for (int i = 0; i < members.Count; i++)
            tabs.Add(new StackTab(members[i], open[stack.Front], texts[i], rects[i], icons[i]));
        return tabs;
    }

    // MARK: At 100%

    StackBars? stackBars;

    /// Brings the tabs at 100% up to date: on a change of focus, after a sync, and when you start or
    /// stop dragging a window (they'd trail behind it, so they wait until you let go). The real order
    /// of the windows counts here: the list only catches up at the next sync.
    void UpdateStackBars()
    {
        if (paused || active || gliding || userMoving != 0 || !initialSyncDone || CanvasWindowOf(GetForegroundWindow()) is not CanvasWindow focused)
        {
            stackBars?.Hide();
            return;
        }
        var rank = new Dictionary<nint, int>();
        EnumWindows((h, _) => { rank[h] = rank.Count; return true; }, 0); // topmost first
        var open = windows.Where(w => !w.Maximised).OrderByDescending(w => rank.GetValueOrDefault(w.Hwnd, int.MaxValue)).ToList();
        int GroupIndex(CanvasWindow w) => groups.FindIndex(g => Groups.IsMember(g.Box, GroupFrame(w)));
        var stacks = Stacks.Find(open.Select(w => w.Frame).ToList(), open.Select(w => (long)w.Hwnd).ToList());
        var showing = Stacks.ShowingAt100(stacks, open.IndexOf(focused), open.Select(GroupIndex).ToList(), GroupIndex(focused));
        double k = UiScale;
        var wanted = new List<(nint key, nint above, List<BarTab> tabs)>();
        foreach (var stack in showing)
        {
            var tabs = TabsOf(stack, open, WorkView, k);
            if (tabs.Count == 0) continue;
            var toScreen = new Vec(monitor.Left, monitor.Top);
            var onScreen = tabs.Select(t => new BarTab(t.Window.Hwnd, t.Text, t.Rect.Offset(toScreen), t.Window == t.OnTop, t.Icon)).ToList();
            onScreen.Add(new BarTab(tabs[0].OnTop.Hwnd, "", Stacks.FitRect(tabs[0].Rect, k).Offset(toScreen), false, null, Fit: true));
            wanted.Add((open[stack.Outer].Hwnd, tabs[0].OnTop.Hwnd, onScreen));
        }
        if (wanted.Count == 0) { stackBars?.Hide(); return; }
        stackBars ??= new StackBars(TabClickedAt100, FitClickedAt100);
        stackBars.Show(wanted, k);
    }

    /// A click on a tab at 100%: you're going to that window, so it takes focus. Like any window you
    /// switch to, the view then moves to it if enough of it is hidden (FocusSettled).
    void TabClickedAt100(nint hwnd)
    {
        if (paused || active || windows.FirstOrDefault(x => x.Hwnd == hwnd) is not CanvasWindow w || !IsWindow(hwnd)) return;
        nint popup = GetLastActivePopup(hwnd);
        nint to = popup != 0 && IsWindowVisible(popup) && IsWindowEnabled(popup) ? popup : hwnd;
        Log.Info($"tab at 100%: {w}, focus {Focus(to)}");
        BringToFront(w);
        UpdateStackBars();
    }

    /// A click on the fit button at 100%. Focus stays where it is.
    void FitClickedAt100(nint front, nint[] members)
    {
        if (paused || active || windows.FirstOrDefault(x => x.Hwnd == front) is not CanvasWindow w) return;
        FitStack(w, windows.Where(x => members.Contains(x.Hwnd) && IsWindow(x.Hwnd)).ToList());
    }

    /// The app's name, or the window's title where the stack has more of that app.
    static string TabText(CanvasWindow w, List<CanvasWindow> members) =>
        w.Title.Length > 0 && members.Count(m => m.AppName == w.AppName) > 1 ? w.Title : w.AppName;

    /// The window of the tab under `p`. The panels sit over the tabs, as does any window in front.
    CanvasWindow? TabAt(Vec p)
    {
        if (OverPanel(p)) return null;
        foreach (var tab in StackTabs(UiScale))
            if (tab.Rect.Contains(p) && !InFrontOf(tab.OnTop, p)) return tab.Window;
        return null;
    }

    /// The stack whose fit button is under `p`: its front window and all its windows.
    (CanvasWindow front, List<CanvasWindow> members)? FitAt(Vec p)
    {
        if (OverPanel(p)) return null;
        double k = UiScale;
        foreach (var stack in StackTabs(k).GroupBy(t => t.OnTop))
            if (Stacks.FitRect(stack.First().Rect, k).Contains(p) && !InFrontOf(stack.Key, p))
                return (stack.Key, stack.Select(t => t.Window).ToList());
        return null;
    }

    bool OverPanel(Vec p) => TreePanel.Contains(p) || minimap.Panel.Contains(p) || desktopsPanel.Panel.Contains(p);

    bool OverStackBar(Vec p) => TabAt(p) != null || FitAt(p) != null;

    bool InFrontOf(CanvasWindow w, Vec p) =>
        windows.Skip(windows.IndexOf(w) + 1).SelectMany(f => f.WithOwned()).Concat(w.Owned).Any(f => camera.ToView(f.Frame).Contains(p));

    /// A click on a tab: that window comes to the front, here and for real, as a sync reads the
    /// order back from the real windows. The view stays where it is.
    void RaiseStacked(CanvasWindow w)
    {
        Log.Info($"tab: {w} to the front");
        SetWindowPos(w.Hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
        BringToFront(w);
        Redraw();
    }

    /// The fit button: every window of the stack to the front window's size and spot, so switching
    /// tabs swaps what's there in place. It's the size you chose for them from now on. An app that
    /// won't take the size keeps as close as it goes, at the same corner.
    void FitStack(CanvasWindow front, List<CanvasWindow> members)
    {
        var to = front.Frame;
        foreach (var w in members.Where(m => m != front))
        {
            var from = w.Frame;
            var actual = ResizeReal(w, to.Size);
            if (!w.SizesItself) w.Preferred = actual;
            w.Frame = Stacks.Fitted(to, actual);
            var by = w.Frame.Origin - from.Origin;
            foreach (var o in w.Owned) o.Frame = o.Frame.Offset(by);
            if (active) PictureMoved(w);
            else Place(w);
            NoteUserMove(w, from.Origin);
            Log.Info($"fit: {w} {from.W:0}x{from.H:0} -> {actual.W:0}x{actual.H:0}, asked {to.W:0}x{to.H:0}");
        }
        SettleGroups();
        ScheduleSave();
        if (active) Relayout();
        UpdateStackBars();
    }
}
