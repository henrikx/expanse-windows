using static Expanse.Native.User32;

namespace Expanse;

/// Dragging a window's border on the zoomed-out canvas. An outline shows where it's going and the
/// real window changes when you let go, so a slow app can't hold up the drag. In a stack, every
/// window goes with it. Only above Group Selection Zoom, where a window is big enough to aim at.
sealed partial class Canvas
{
    /// The window whose border is under `p`, and which of its edges. The fit button, tabs and × come
    /// first. Just outside a window's picture, a group's border or label does too; the window's
    /// border can still be grabbed from just inside it.
    (CanvasWindow w, ResizeEdges edges)? WindowEdgeAt(Vec p)
    {
        if (GroupsTakeClicks || OverPanel(p) || OverStackBar(p)) return null;
        double k = UiScale;
        var over = WindowAt(p);
        if (over != null && CloseShown(over, over, k) && Marks.CloseRect(camera.ToView(over.Frame), k).Contains(p)) return null;
        if (over == null && GroupHitAt(p) is (CanvasGroup, GroupPart part, ResizeEdges) && part != GroupPart.Body) return null;
        var grabbable = windows.Select(w => new GrabbableWindow(camera.ToView(w.Frame), CanBeResized(w),
            w.Owned.Select(o => camera.ToView(o.Frame)).ToList())).ToList();
        return Resizing.WindowEdgeAt(grabbable, p, k) is (int i, ResizeEdges edges) ? (windows[i], edges) : null;
    }

    /// Windows without a sizing border say they can't be resized. Maximised ones and ones that size
    /// themselves have no say in it, and a hung one wouldn't answer.
    static bool CanBeResized(CanvasWindow w) =>
        !w.Maximised && !w.SizesItself && (Style(w.Hwnd) & WS_THICKFRAME) != 0 && !IsHungAppWindow(w.Hwnd);

    void PressWindowEdge(CanvasWindow w, ResizeEdges edges, Vec p)
    {
        ClearSelection();
        drag = new Drag(DragKind.WindowResize, p)
        {
            // Pressed a little inside the picture without moving, it's still a click on what's there.
            Clicked = WindowAt(p), StartBox = w.Frame, Edges = edges,
            Moving = ResizingWith(w).Select(m => (m, m.Frame)).ToList(),
        };
        SetCursor(LoadCursor(0, ResizeCursor(edges)));
    }

    /// `w` and the rest of its stack, if it's in one.
    List<CanvasWindow> ResizingWith(CanvasWindow w)
    {
        var open = windows.Where(x => !x.Maximised).ToList();
        int at = open.IndexOf(w);
        var stack = Stacks.Find(open.Select(x => x.Frame).ToList(), open.Select(x => (long)x.Hwnd).ToList())
            .FirstOrDefault(s => s.Members.Contains(at));
        return stack == null ? [w] : stack.Members.Select(i => open[i]).ToList();
    }

    /// Snaps to the other windows as a move does, unless Ctrl is held. The rest of a stack going
    /// along isn't one of them.
    void DragWindowEdge(Drag d, Vec p, bool snap)
    {
        if (!d.Moved && p.DistanceTo(d.StartPoint) < 3) return;
        d.Moved = true;
        var delta = new Vec((p.X - d.StartPoint.X) / camera.Scale, (p.Y - d.StartPoint.Y) / camera.Scale);
        var to = Resizing.ResizeWindow(d.StartBox, d.Edges, delta, Resizing.WindowMinimum, WorkSize);
        var lines = new List<Layout.GuideLine>();
        if (snap)
        {
            var going = d.Moving.Select(m => m.w).ToHashSet();
            (to, lines) = Layout.SnapEdges(to, d.StartBox, d.Edges, windows.Where(w => !going.Contains(w)).Select(w => w.Frame).ToList(),
                camera.Scale, Resizing.WindowMinimum, WorkSize);
        }
        d.ResizeTo = to;
        ShowGuides(lines);
        Redraw();
    }

    /// Only a drag let go of counts: losing the mouse to something else leaves the windows as they were.
    void EndWindowEdgeDrag(Drag d, bool click)
    {
        if (!click) return;
        if (d.ResizeTo is not RectD to)
        {
            if (d.Clicked is CanvasWindow clicked && windows.Contains(clicked)) ClickWindow(clicked);
            return;
        }
        foreach (var (w, _) in d.Moving)
        {
            if (!windows.Contains(w) || !IsWindow(w.Hwnd)) continue;
            var from = w.Frame;
            var actual = ResizeReal(w, to.Size, remakePicture: false);
            if (!w.SizesItself) w.Preferred = actual;
            w.Frame = Resizing.Anchored(to, d.Edges, actual);
            // Made from the frame, so only now that it has the new size.
            if (active) pictures.Resized(w);
            var by = w.Frame.Origin - from.Origin;
            foreach (var o in w.Owned) o.Frame = o.Frame.Offset(by);
            PictureMoved(w);
            NoteUserMove(w, from.Origin);
            Log.Info($"resized: {w} {from.W:0}x{from.H:0} -> {actual.W:0}x{actual.H:0}, asked {to.W:0}x{to.H:0}");
        }
        SettleGroups();
        ScheduleSave();
        Relayout();
    }
}
