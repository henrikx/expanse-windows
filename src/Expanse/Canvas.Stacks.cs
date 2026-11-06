using static Expanse.Native.User32;

namespace Expanse;

/// Windows stacked inside each other, zoomed out: a row of tabs below the outer one to switch
/// between them (see Stacks). They stack with the window in front, so what's in front of that covers them.
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
        var stacks = Stacks.Find(open.Select(w => w.Frame).ToList(), open.Select(w => (long)w.Hwnd).ToList(), Stacks.Tolerance * k);
        foreach (var stack in stacks)
        {
            var outer = camera.ToView(open[stack.Outer].Frame);
            if (!outer.Intersects(View)) continue;
            var members = stack.Members.Select(i => open[i]).ToList();
            var texts = members.Select(w => TabText(w, members)).ToList();
            var icons = members.Select(w => appIcons.Get(w, (int)Math.Round(GroupTree.IconSize * k))).ToList();
            // A little to spare: the tab's text is drawn by another API, which may measure it wider.
            var rects = Stacks.TabRects(outer,
                texts.Select((t, i) => TextWidth(t, k) + Math.Ceiling(2 * k) + (icons[i] != null ? Stacks.TabIconRoom * k : 0)).ToList(), View, k);
            for (int i = 0; i < members.Count; i++)
                tabs.Add(new StackTab(members[i], open[stack.Front], texts[i], rects[i], icons[i]));
        }
        return tabs;
    }

    /// The app's name, or the window's title where the stack has more of that app.
    static string TabText(CanvasWindow w, List<CanvasWindow> members) =>
        w.Title.Length > 0 && members.Count(m => m.AppName == w.AppName) > 1 ? w.Title : w.AppName;

    /// The window of the tab under `p`. The panels sit over the tabs, as does any window in front.
    CanvasWindow? TabAt(Vec p)
    {
        if (TreePanel.Contains(p) || minimap.Panel.Contains(p) || desktopsPanel.Panel.Contains(p)) return null;
        foreach (var tab in StackTabs(UiScale))
            if (tab.Rect.Contains(p) && !InFrontOf(tab.OnTop, p)) return tab.Window;
        return null;
    }

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
}
