using System.Diagnostics;
using Expanse.Native;
using static Expanse.Native.User32;

namespace Expanse;

/// A named box on the canvas. Who's in it is worked out from where apps are (see Groups).
sealed class CanvasGroup(long id, string name, RectD box)
{
    public readonly long Id = id;
    public string Name = name;
    public RectD Box = box;
    /// Closed in the groups panel.
    public bool Collapsed;
}

/// Groups zoomed out: making them, the mouse on them, and bringing them back to Groups' rules when
/// something moved. Drawn behind the pictures, so a window in front covers its group's label.
sealed partial class Canvas
{
    /// The groups of the desktop you're on: everything here goes by these alone.
    readonly List<CanvasGroup> groups = new();
    /// The other desktops'.
    readonly PerDesktop<CanvasGroup> groupsAway = new();
    long nextGroupId = 1;
    CanvasGroup? hoveredGroup;
    /// Where a right press landed, until the button comes up.
    CanvasWindow? rightPressed;
    readonly RenameBox renameBox = new();
    /// The last press on a label, to tell a double click (the overlay's class doesn't ask for them).
    (long id, long at, Vec p) lastLabelPress;
    /// A window from another monitor has joined but isn't at its spot yet (see FinishDragIn).
    bool holdSettling;
    const string NewGroupText = "New Group";
    /// How far from a border (view pixels, at a display scale of 1) still grabs it.
    const double BorderGrab = 6;

    /// Groups belong to a virtual desktop, like its windows: those of the desktop you're on come
    /// out, and the others wait. Explorer says nothing when you switch, so this asks. True if
    /// they changed.
    bool FollowDesktop()
    {
        var (desktops, current) = VirtualDesktops.ReadIds();
        var from = groupsAway.Here;
        if (!groupsAway.Follow(groups, desktops, current))
            return false;
        Log.Info($"groups: desktop {from} -> {current}, {groups.Count} here, {groupsAway.Away.Count()} on others");
        renameBox.Finish(save: true, byKey: false);
        hoveredGroup = null;
        selectedGroups.Clear();
        treeScroll = 0;
        treeHighlight = -1;
        ScheduleSave();
        return true;
    }

    IReadOnlyList<RectD> OtherBoxes(CanvasGroup g) => groups.Where(o => o != g).Select(o => o.Box).ToList();

    /// The frame group rules go by. Maximised counts at the size you'd restore it to, so maximising
    /// an app doesn't disturb its group.
    RectD GroupFrame(CanvasWindow w) => w.Maximised ? w.Frame with { W = w.NormalSize.W, H = w.NormalSize.H } : w.Frame;

    RectD[] GroupFrames() => windows.Select(GroupFrame).ToArray();

    List<CanvasWindow> MembersOf(CanvasGroup g) => windows.Where(w => Groups.IsMember(g.Box, GroupFrame(w))).ToList();

    bool InAnyGroup(CanvasWindow w) => groups.Any(g => Groups.IsMember(g.Box, GroupFrame(w)));

    /// Where the rules moved an app, its window and dialogs go too.
    void ApplyGroupFrames(RectD[] before, RectD[] after)
    {
        for (int i = 0; i < windows.Count && i < after.Length; i++)
        {
            if (after[i] == before[i]) continue;
            var w = windows[i];
            var d = after[i].Origin - before[i].Origin;
            var from = w.Frame.Origin;
            w.Frame = w.Frame.Offset(d);
            foreach (var o in w.Owned) o.Frame = o.Frame.Offset(d);
            if (active) PictureMoved(w);
            NoteUserMove(w, from);
            Log.Info($"group: moved {w} to {w.Frame}");
        }
    }

    /// Back to the rules (see Groups.Settle). `dragStarts`: where the apps just dropped came from.
    void SettleGroups(IReadOnlyDictionary<int, RectD>? dragStarts = null)
    {
        FollowDesktop();
        if (groups.Count == 0 || holdSettling) return;
        var before = GroupFrames();
        var settled = Groups.Settle(groups.Select(g => g.Box).ToList(), before, dragStarts);
        for (int i = 0; i < groups.Count; i++)
        {
            if (settled.Boxes[i] == groups[i].Box) continue;
            Log.Info($"group \"{groups[i].Name}\": {groups[i].Box} -> {settled.Boxes[i]}");
            groups[i].Box = settled.Boxes[i];
        }
        ApplyGroupFrames(before, settled.Apps);
    }

    // MARK: Fitting and tidying

    /// Tidy Up, on the home layout: see Groups.Tidy. The boxes are fitted again in GroupsAfterFit.
    void TidyHome(RectD[] home, int[][] members)
    {
        var boxes = Groups.Tidy(home, groups.Select(g => g.Box).ToList(), members);
        for (int i = 0; i < groups.Count; i++) groups[i].Box = boxes[i];
    }

    /// The fit moved the windows to `frames`: the boxes follow, and groups pushed into something
    /// go as blocks (see Groups.AfterFit). Frames keep their full size for maximised windows, whose
    /// group frames are smaller, so what's carried over is how far each moved.
    void GroupsAfterFit(RectD[] before, RectD[] frames, int[][] members, bool refit)
    {
        var apps = frames.Select((f, i) => windows[i].Maximised ? f with { W = before[i].W, H = before[i].H } : f).ToArray();
        var start = apps.ToArray();
        var boxes = Groups.AfterFit(before, apps, groups.Select(g => g.Box).ToList(), members, refit);
        for (int i = 0; i < groups.Count; i++) groups[i].Box = boxes[i];
        for (int i = 0; i < frames.Length; i++) frames[i] = frames[i].Offset(apps[i].Origin - start[i].Origin);
    }

    // MARK: Hit-testing

    enum GroupPart { Label, Trash, Border, Body }

    (RectD label, RectD trash) LabelRects(CanvasGroup g, double k) =>
        Groups.LabelRects(camera.ToView(g.Box), TextWidth(g.Name, k), k);

    /// What's under `p` of the groups, windows aside. Top group first: boxes don't overlap, but a
    /// label can reach over the group above it.
    (CanvasGroup group, GroupPart part, ResizeEdges edges)? GroupHitAt(Vec p)
    {
        double k = UiScale;
        foreach (var g in groups)
        {
            var (label, trash) = LabelRects(g, k);
            if (trash.Contains(p)) return (g, GroupPart.Trash, ResizeEdges.None);
            if (label.Contains(p)) return (g, GroupPart.Label, ResizeEdges.None);
        }
        foreach (var g in groups)
        {
            var box = camera.ToView(g.Box);
            var edges = Resizing.EdgesAt(box, p, BorderGrab * k);
            if (edges != ResizeEdges.None) return (g, GroupPart.Border, edges);
            if (box.Contains(p)) return (g, GroupPart.Body, ResizeEdges.None);
        }
        return null;
    }

    /// The group a click on this window goes to instead of the window, if any.
    CanvasGroup? GroupTakingClick(CanvasWindow w) => GroupsTakeClicks ? GroupHolding(w) : null;

    /// Zoomed out this far (Group Selection Zoom, as the minimap rounds it) a group's apps are too
    /// small to aim at: a click on one goes to its group. Dragging one, Shift and its × still work.
    bool GroupsTakeClicks => Math.Round(camera.Scale * 100) <= Math.Clamp(settings.GroupClickZoom, 0, 99);

    /// Group Selection Zoom…: asks, and the boxes fade to suit straight away if the canvas is up.
    void ChooseGroupZoom()
    {
        // At most 99: 100 would take every click on a grouped app.
        if (PercentDialog.Ask("Group Selection Zoom",
            "At this zoom or lower, clicking an app in a group goes to the group, and the group's box turns dark.",
            "Zoom, 0 to 99 (0 turns it off):", Math.Clamp(settings.GroupClickZoom, 0, 99), 0, 99) is int zoom)
        {
            settings.GroupClickZoom = zoom;
            settings.Save();
            Log.Info($"group selection zoom: {zoom}%");
        }
        if (!active) return;
        Focus(overlay.Hwnd);
        Redraw();
    }

    /// How solid the groups' boxes look, from 0 (a click on an app goes to the app) to 1 (to its
    /// group). It fades, so the eye catches the zoom crossing the setting.
    double groupSolidity;
    long groupSolidityAt;
    const double GroupSolidityFade = 0.15;

    bool GroupSolidityMoving => active && !gliding && groupSolidity != (GroupsTakeClicks ? 1 : 0);

    /// One frame of the fade.
    void StepGroupSolidity()
    {
        long now = Stopwatch.GetTimestamp();
        // Not the time since the last fade, which may be long ago: at most a slow frame's worth.
        double step = Math.Min(Stopwatch.GetElapsedTime(groupSolidityAt, now).TotalSeconds, 0.033) / GroupSolidityFade;
        groupSolidityAt = now;
        groupSolidity = GroupsTakeClicks ? Math.Min(1, groupSolidity + step) : Math.Max(0, groupSolidity - step);
    }

    CanvasGroup? GroupHolding(CanvasWindow w) => groups.FirstOrDefault(g => Groups.IsMember(g.Box, GroupFrame(w)));

    /// The New Group button: where the new group's label will be, for a selection none of it in a
    /// group yet.
    RectD? NewGroupRect(double k)
    {
        if (selected.Count == 0 || selectedGroups.Count > 0 || drag != null || selected.Any(InAnyGroup)) return null;
        var picked = windows.Where(selected.Contains).Select(GroupFrame).ToList();
        if (picked.Count == 0) return null;
        return Groups.ButtonRect(camera.ToView(Groups.Fit(picked)), TextWidth(NewGroupText, k), View, k);
    }

    readonly Dictionary<(string, double), double> textWidths = new();

    /// The width of a label's text, for working out where its button goes.
    double TextWidth(string text, double k)
    {
        if (textWidths.TryGetValue((text, k), out double known)) return known;
        nint dc = GetDC(overlay.Hwnd);
        nint font = Gdi32.CreateFont(-(int)(13 * k), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        nint old = Gdi32.SelectObject(dc, font);
        var measure = new RECT(0, 0, 4000, 100);
        DrawText(dc, text, -1, ref measure, DT_SINGLELINE | DT_NOPREFIX | DT_CALCRECT);
        Gdi32.SelectObject(dc, old);
        Gdi32.DeleteObject(font);
        ReleaseDC(overlay.Hwnd, dc);
        if (textWidths.Count > 200) textWidths.Clear();
        return textWidths[(text, k)] = measure.Width;
    }

    /// What the pointer shows over a group's border or button (0: nothing special).
    nint GroupCursorAt(Vec p)
    {
        if (drag != null) return 0;
        if (NewGroupRect(UiScale)?.Contains(p) == true || OverStackBar(p)) return LoadCursor(0, IDC_HAND);
        if (TreePanel.Contains(p)) return treeHover >= 0 ? LoadCursor(0, IDC_HAND) : 0;
        if (minimap.Panel.Contains(p) || desktopsPanel.Panel.Contains(p) || WindowAt(p) != null) return 0;
        if (GroupHitAt(p) is not (CanvasGroup, GroupPart part, ResizeEdges edges)) return 0;
        return part switch
        {
            GroupPart.Trash => LoadCursor(0, IDC_HAND),
            GroupPart.Border => LoadCursor(0, ResizeCursor(edges)),
            _ => 0,
        };
    }

    static int ResizeCursor(ResizeEdges edges) => edges switch
    {
        ResizeEdges.Left or ResizeEdges.Right => IDC_SIZEWE,
        ResizeEdges.Top or ResizeEdges.Bottom => IDC_SIZENS,
        ResizeEdges.Left | ResizeEdges.Top or ResizeEdges.Right | ResizeEdges.Bottom => IDC_SIZENWSE,
        _ => IDC_SIZENESW,
    };

    void HoverGroup(Vec p, CanvasWindow? overWindow)
    {
        var over = minimap.Panel.Contains(p) || desktopsPanel.Panel.Contains(p) ? null
            : overWindow != null ? GroupTakingClick(overWindow)
            : GroupHitAt(p) is var (g, _, _) ? g : null;
        if (over == hoveredGroup) return;
        hoveredGroup = over;
        Redraw();
    }

    // MARK: The mouse

    bool CanPressRight => active && !gliding && animation == null && drag == null;

    /// The window a right press landed on, if nothing sits over it there.
    void PressRight(Vec p)
    {
        bool covered = NewGroupRect(UiScale)?.Contains(p) == true || OverStackBar(p) || TreePanel.Contains(p)
            || minimap.Panel.Contains(p) || desktopsPanel.Panel.Contains(p);
        rightPressed = CanPressRight && !covered ? WindowAt(p) : null;
    }

    /// A right click on an app in a group goes to the group, at any zoom. Elsewhere it does nothing.
    void ReleaseRight(Vec p)
    {
        var w = rightPressed;
        rightPressed = null;
        if (w == null || !CanPressRight || WindowAt(p) != w || GroupHolding(w) is not CanvasGroup g) return;
        Log.Info($"right click on {w}");
        renameBox.Finish(save: true, byKey: false);
        searchBox.Close();
        ClearSelection();
        FocusGroup(g, w);
    }

    /// The New Group button, which sits over everything. True if the press was on it.
    bool PressNewGroup(Vec p)
    {
        if (NewGroupRect(UiScale)?.Contains(p) != true) return false;
        CreateGroup();
        return true;
    }

    void CreateGroup()
    {
        var frames = GroupFrames();
        var picked = Enumerable.Range(0, windows.Count).Where(i => selected.Contains(windows[i])).ToList();
        if (Groups.Create(frames, picked, groups.Select(g => g.Box).ToList()) is not CreatedGroup made)
        {
            Log.Info("new group: refused, too near another group");
            MessageBox(overlay.Hwnd, "These apps are too close to another group. Move them apart and try again.", "New Group",
                MB_OK | MB_ICONINFORMATION);
            Focus(overlay.Hwnd);
            return;
        }
        var group = new CanvasGroup(nextGroupId++, NextGroupName(), made.Box);
        groups.Add(group);
        ApplyGroupFrames(frames, made.Apps);
        ClearSelection();
        Log.Info($"new group \"{group.Name}\" at {group.Box}");
        ScheduleSave();
        Relayout();
    }

    string NextGroupName()
    {
        for (int n = 1; ; n++)
            if (groups.All(g => g.Name != $"Group {n}")) return $"Group {n}";
    }

    /// A press with no window under it and no Ctrl or Shift. True if it started a drag.
    bool PressGroup(CanvasGroup g, GroupPart part, ResizeEdges edges, Vec p)
    {
        // A selected group's body or label drags the whole selection, unless it's a rename.
        bool together = selectedGroups.Contains(g) && part is GroupPart.Body or GroupPart.Label;
        if (!together) ClearSelection();
        switch (part)
        {
            case GroupPart.Trash:
                DeleteGroup(g);
                return false;
            case GroupPart.Label when IsDoubleClick(g, p):
                ClearSelection();
                StartRename(g);
                return false;
            case GroupPart.Border:
                drag = new Drag(DragKind.GroupResize, p) { Group = g, StartBox = g.Box, Edges = edges, Required = RequiredBox(g) };
                SetCursor(LoadCursor(0, ResizeCursor(edges)));
                return true;
            case GroupPart.Body or GroupPart.Label when together:
                drag = SelectionDrag(p);
                drag.Group = g;
                drag.FocusOnClick = part == GroupPart.Body;
                return true;
            default:
                // A press on the label can drag the group, but only a click on its body goes to it.
                drag = new Drag(DragKind.GroupMove, p)
                {
                    Group = g, StartBox = g.Box, FocusOnClick = part == GroupPart.Body,
                    Moving = MembersOf(g).Select(w => (w, w.Frame)).ToList(),
                };
                return true;
        }
    }

    RectD? RequiredBox(CanvasGroup g)
    {
        var members = MembersOf(g);
        return members.Count == 0 ? null : Groups.Fit(members.Select(GroupFrame));
    }

    bool IsDoubleClick(CanvasGroup g, Vec p)
    {
        long now = Environment.TickCount64;
        bool twice = lastLabelPress.id == g.Id && now - lastLabelPress.at <= GetDoubleClickTime() && lastLabelPress.p.DistanceTo(p) < 6;
        lastLabelPress = twice ? default : (g.Id, now, p);
        return twice;
    }

    /// The border and body drags (see DragTo).
    void DragGroup(Drag d, Vec p, bool snap)
    {
        var g = d.Group!;
        var delta = new Vec((p.X - d.StartPoint.X) / camera.Scale, (p.Y - d.StartPoint.Y) / camera.Scale);
        var others = OtherBoxes(g);
        if (d.What == DragKind.GroupResize)
        {
            d.Moved = true;
            g.Box = Groups.ResizeBox(d.StartBox, g.Box, d.Edges, delta, d.Required, others);
            Redraw();
            return;
        }
        if (!d.Moved && p.DistanceTo(d.StartPoint) < 3) return;
        d.Moved = true;
        var moving = d.Moving.Select(m => m.w).ToHashSet();
        var proposed = d.StartBox.Offset(delta);
        var (snapped, lines) = snap
            ? Layout.Snap(proposed, windows.Where(w => !moving.Contains(w)).Select(w => w.Frame).Concat(others).ToList(), camera.Scale, camera.Center(ViewSize))
            : (proposed, new List<Layout.GuideLine>());
        g.Box = Groups.Move(g.Box, snapped, others);
        var by = g.Box.Origin - d.StartBox.Origin;
        foreach (var (w, start) in d.Moving)
        {
            var from = w.Frame.Origin;
            w.Frame = start.Offset(by);
            foreach (var o in w.Owned) o.Frame = o.Frame.Offset(w.Frame.Origin - from);
            PictureMoved(w);
        }
        ShowGuides(lines);
        hovered = null;
        Relayout();
    }

    void EndGroupDrag(Drag d, bool click)
    {
        var g = d.Group!;
        if (!d.Moved)
        {
            if (click && d.FocusOnClick) FocusGroup(g);
            return;
        }
        foreach (var (w, start) in d.Moving) NoteUserMove(w, start.Origin);
        Log.Info($"group \"{g.Name}\" {(d.What == DragKind.GroupResize ? "resized" : "moved")} to {g.Box}");
        SettleGroups();
        ScheduleSave();
        Relayout();
    }

    /// Where the apps just dropped came from, in the frames group rules go by.
    Dictionary<int, RectD> DragStarts(Drag d) => d.Moving
        .Where(m => windows.Contains(m.w))
        .ToDictionary(m => windows.IndexOf(m.w), m => GroupFrame(m.w) with { X = m.start.X, Y = m.start.Y });

    /// A click on a group: back to 100% with its apps centred, as many as fit on screen, and
    /// `clicked` (the app the click landed on, if any; otherwise the one you used last) whole and
    /// focused. You put the view here, so clicking one of the others that's cut off won't send it
    /// elsewhere (see FocusSettled).
    void FocusGroup(CanvasGroup g, CanvasWindow? clicked = null)
    {
        var members = MembersOf(g);
        var focus = clicked ?? (CanvasWindowOf(previousForeground) is CanvasWindow last && members.Contains(last) ? last : members.LastOrDefault());
        if (focus != null)
        {
            BringToFront(focus);
            FitToScreen(focus);
        }
        var open = windows.Where(w => !w.Maximised).ToList();
        var area = new RectD(work.Left - monitor.Left, work.Top - monitor.Top, work.Width, work.Height);
        var target = focus?.Maximised == true ? FocusCamera(focus)
            : new Camera(Stacks.GroupOrigin(open.Select(w => w.Frame).ToList(), open.Select(w => (long)w.Hwnd).ToList(),
                members.Select(m => open.IndexOf(m)).Where(i => i >= 0).ToList(), focus == null ? -1 : open.IndexOf(focus),
                members.Where(m => m.Maximised).Select(GroupFrame).ToList(), g.Box, area, UiScale), 1);
        gliding = true;
        hovered = null;
        hoveredGroup = null;
        glideTarget = focus;
        Log.Info($"go to group \"{g.Name}\": {members.Count} apps, focus {focus?.ToString() ?? "none"}");
        Animate(target, 0.25, () => HandBack(focus));
    }

    void ClearSelection()
    {
        selected.Clear();
        selectedGroups.Clear();
    }

    /// A drag of everything selected: the windows, and the selected groups with all their apps.
    Drag SelectionDrag(Vec p)
    {
        var moving = windows.Where(w => selected.Contains(w) || selectedGroups.Any(g => Groups.IsMember(g.Box, GroupFrame(w))));
        return new Drag(DragKind.Windows, p)
        {
            Moving = moving.Select(w => (w, w.Frame)).ToList(),
            MovingGroups = groups.Where(selectedGroups.Contains).Select(g => (g, g.Box)).ToList(),
        };
    }

    void DeleteGroup(CanvasGroup g)
    {
        bool yes = MessageBox(overlay.Hwnd, $"Delete the group \"{g.Name}\"? Its apps stay open, where they are.", "Delete Group",
            MB_YESNO | MB_ICONINFORMATION) == IDYES;
        Focus(overlay.Hwnd);
        if (!yes) return;
        groups.Remove(g);
        selectedGroups.Remove(g);
        if (hoveredGroup == g) hoveredGroup = null;
        Log.Info($"deleted group \"{g.Name}\"");
        ScheduleSave();
        Redraw();
    }

    void StartRename(CanvasGroup g)
    {
        double k = UiScale;
        var (label, _) = LabelRects(g, k);
        int x = (int)Math.Round(label.X) + monitor.Left, y = (int)Math.Round(label.Y) + monitor.Top;
        int w = (int)Math.Round(Math.Max(label.W, 180 * k)), h = (int)Math.Round(Math.Max(label.H, 24 * k));
        renameBox.Show(overlay.Hwnd, x, y, w, h, g.Name, k, (name, byKey) =>
        {
            if (name != null && name.Trim().Length > 0 && name.Trim() != g.Name)
            {
                g.Name = name.Trim();
                Log.Info($"group renamed \"{g.Name}\"");
                ScheduleSave();
            }
            if (byKey && active) Focus(overlay.Hwnd);
            if (active) Redraw();
        });
    }

    // MARK: Drawing

    /// The groups in view, for the pictures to draw (or Paint, on the backdrop).
    List<GroupDrawing> GroupDrawings(double k)
    {
        var drawings = new List<GroupDrawing>(groups.Count);
        foreach (var g in groups)
        {
            var box = camera.ToView(g.Box);
            var (label, trash) = LabelRects(g, k);
            if (!box.Intersects(View) && !label.Intersects(View)) continue;
            drawings.Add(new GroupDrawing(g.Id, box, g.Name, label, trash, g == hoveredGroup || drag?.Group == g || selectedGroups.Contains(g)));
        }
        return drawings;
    }

    // MARK: Saving

    List<SavedGroup> SavedGroups()
    {
        static SavedGroup Saved(CanvasGroup g, Guid? desktop) =>
            new() { Name = g.Name, Desktop = desktop, Collapsed = g.Collapsed, X = g.Box.X, Y = g.Box.Y, W = g.Box.W, H = g.Box.H };
        return groups.Select(g => Saved(g, groupsAway.Here)).Concat(groupsAway.Away.Select(a => Saved(a.Item, a.Desktop))).ToList();
    }

    /// The groups of a saved layout. One saved before groups existed leaves the ones here alone.
    /// A group saved before groups had a desktop goes on the one you're on.
    void TakeSavedGroups(SavedLayout? layout)
    {
        if (layout?.Groups is not List<SavedGroup> list) return;
        var (_, current) = VirtualDesktops.ReadIds();
        groups.Clear();
        groupsAway.Reset(current);
        hoveredGroup = null;
        selectedGroups.Clear();
        foreach (var s in list)
        {
            var g = new CanvasGroup(nextGroupId++, s.Name, new RectD(s.X, s.Y, s.W, s.H)) { Collapsed = s.Collapsed };
            if (current != null && s.Desktop is Guid d && d != current) groupsAway.Put(d, g);
            else groups.Add(g);
        }
        othersCollapsed = layout.OthersCollapsed;
        FollowDesktop(); // one saved on a desktop since removed
    }
}
