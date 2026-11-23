using Expanse.Native;
using static Expanse.Native.User32;

namespace Expanse;

/// A command line started from its group (see GroupCommands).
sealed class GroupCommand(string name, string line)
{
    public string Name = name, Line = line;
    /// Where its window goes, and how big it was: relative to the group's box. Null until it's had one.
    public RectD? Spot;
    /// The window it opened, while that's open, and its process.
    public nint Window;
    public uint Pid;
    /// Why it didn't start, until it does or is edited.
    public string? Error;
    /// The file whose icon it shows, once `LookedUp` (the line that was looked up) is the line.
    public string? IconFile, LookedUp;
}

/// A group's command drawer: the >_ by its label opens it, and a tile starts a command, whose window
/// goes into the group. It closes on Esc, a click outside it, or leaving the canvas.
sealed partial class Canvas
{
    /// A command started, and the window it's waiting for.
    sealed class PendingCommand(CanvasGroup group, GroupCommand command, HashSet<nint> before)
    {
        public readonly CanvasGroup Group = group;
        public readonly GroupCommand Command = command;
        /// The windows there were already: none of them is its.
        public readonly HashSet<nint> Before = before;
        /// Set by the thread starting it, as it's known. `Started`: Windows has started it (it can ask you first).
        public volatile string Program = "";
        public volatile bool Console;
        public uint Pid;
        public long At;
        public bool Started;
        /// The window it got. If that closes at once (a splash screen), the next is its.
        public nint Window;
    }

    /// For this long after its window comes, focus on it doesn't take you out of the canvas.
    const long CommandFocusMs = 5000;

    CanvasGroup? drawerGroup;
    /// The command in the drawer's editor. Null with `drawerAdding`: a new one.
    GroupCommand? drawerEditing;
    bool drawerAdding;
    (DrawerPart part, int tile) drawerHover;
    readonly CommandBox commandBox = new();
    readonly List<PendingCommand> pendingCommands = new();
    /// Windows placed for a command that Arrive hasn't seen yet.
    readonly HashSet<nint> placedForCommand = new();
    readonly Dictionary<nint, long> commandWindows = new();

    bool DrawerEditing => drawerAdding || drawerEditing != null;

    // MARK: The drawer

    void OpenDrawer(CanvasGroup g)
    {
        drawerGroup = g;
        drawerHover = default;
        LookUpIcons(g);
        Log.Info($"command drawer of group \"{g.Name}\": {g.Commands.Count} commands");
    }

    /// `save`: what's in the editor is kept, as clicking away from a rename keeps the name.
    void CloseDrawer(bool save)
    {
        if (drawerGroup == null) return;
        if (DrawerEditing) EndCommandEdit(save);
        drawerGroup = null;
    }

    DrawerLayout? DrawerNow(double k)
    {
        if (drawerGroup is not CanvasGroup g) return null;
        var button = LabelRects(g, k).commands;
        return DrawerEditing ? GroupCommands.Editor(button, View, drawerEditing != null, k)
            : GroupCommands.Tiles(button, View, g.Commands.Count, g.Commands.Any(c => c.Error != null), k);
    }

    /// What's under `p` of the drawer, if it's open.
    (DrawerPart part, int tile) DrawerAt(Vec p) => DrawerNow(UiScale)?.At(p) ?? (DrawerPart.None, -1);

    /// Closes the drawer when its group is gone or out of view, or you've left the canvas, and keeps the
    /// editor's text boxes over their fields. Before drawing.
    void KeepDrawer(double k)
    {
        if (drawerGroup is not CanvasGroup g) return;
        bool here = active && !gliding && (groups.Contains(g) || pinnedGroups.Contains(g));
        if (!here || !(ViewBox(g).Intersects(View) || LabelRects(g, k).commands.Intersects(View)))
        {
            CloseDrawer(save: true);
            return;
        }
        if (DrawerNow(k)?.Editor is DrawerEditor e) commandBox.MoveTo(OnScreen(e.Name), OnScreen(e.Line));
    }

    RECT OnScreen(RectD r) => new((int)r.X + monitor.Left, (int)r.Y + monitor.Top, (int)r.Right + monitor.Left, (int)r.Bottom + monitor.Top);

    /// True if the drawer was open: the press is on it, or it just closes it and does nothing else.
    bool PressDrawer(Vec p)
    {
        if (drawerGroup is not CanvasGroup g) return false;
        var (part, tile) = DrawerAt(p);
        switch (part)
        {
            case DrawerPart.None: CloseDrawer(save: true); break;
            case DrawerPart.Panel: commandBox.Refocus(); break; // or Enter and Esc would be the canvas's
            case DrawerPart.Tile: RunCommand(g, g.Commands[tile]); break;
            case DrawerPart.Edit: EditCommand(g.Commands[tile]); break;
            case DrawerPart.Add when g.Commands.Count < GroupCommands.MaxCommands: EditCommand(null); break;
            case DrawerPart.RunAll: foreach (var c in g.Commands.ToList()) RunCommand(g, c); break;
            case DrawerPart.Save: EndCommandEdit(save: true); break;
            case DrawerPart.Cancel: EndCommandEdit(save: false); break;
            case DrawerPart.Delete when drawerEditing is GroupCommand gone:
                EndCommandEdit(save: false);
                g.Commands.Remove(gone);
                pendingCommands.RemoveAll(pending => pending.Command == gone);
                Log.Info($"group \"{g.Name}\": command deleted, {g.Commands.Count} left");
                ScheduleSave();
                break;
        }
        drawerHover = DrawerAt(p);
        Redraw();
        return true;
    }

    /// True if the pointer is over the drawer, which hides what's under it.
    bool HoverDrawer(Vec p)
    {
        if (drawerGroup == null) return false;
        var over = DrawerAt(p);
        if (over != drawerHover)
        {
            drawerHover = over;
            Redraw();
        }
        if (over.part == DrawerPart.None) return false;
        if (hovered == null && hoveredGroup == null) return true;
        hovered = null;
        hoveredGroup = null;
        Redraw();
        return true;
    }

    // MARK: Editing

    void EditCommand(GroupCommand? c)
    {
        drawerEditing = c;
        drawerAdding = c == null;
        double k = UiScale;
        if (DrawerNow(k)?.Editor is not DrawerEditor e) return;
        commandBox.Ended = save =>
        {
            EndCommandEdit(save);
            Focus(overlay.Hwnd);
            Redraw();
        };
        commandBox.Open(overlay.Hwnd, OnScreen(e.Name), OnScreen(e.Line), c?.Name ?? "", c?.Line ?? "", k);
    }

    /// Back to the tiles. A command with no line isn't one, so it isn't kept (or changed).
    void EndCommandEdit(bool save)
    {
        if (drawerGroup is not CanvasGroup g || !DrawerEditing) return;
        var (name, line) = commandBox.Texts;
        var c = drawerEditing;
        drawerEditing = null;
        drawerAdding = false;
        commandBox.Close();
        if (!save || line.Length == 0) return;
        if (name.Length == 0) name = NameFor(line);
        if (c == null) g.Commands.Add(c = new GroupCommand(name, line));
        else if (c.Name == name && c.Line == line) return;
        (c.Name, c.Line, c.Error) = (name, line, null);
        Log.Info($"group \"{g.Name}\": command saved, {g.Commands.Count} in all");
        LookUpIcons(g);
        ScheduleSave();
    }

    /// A command you gave no name has its program's.
    static string NameFor(string line)
    {
        string program = GroupCommands.Split(line, _ => false).program;
        string name = Path.GetFileNameWithoutExtension(program.TrimEnd('\\', '/'));
        return name.Length > 0 ? name : program;
    }

    /// Finds the commands' programs, for their icons. Not here: it can wait on the disk.
    void LookUpIcons(CanvasGroup g)
    {
        foreach (var c in g.Commands.Where(c => c.LookedUp != c.Line))
        {
            string line = c.Line;
            Task.Run(() =>
            {
                var target = Launcher.Find(line);
                Post(() =>
                {
                    if (c.Line != line) return;
                    c.LookedUp = line;
                    c.IconFile = target.Found ? target.File : null;
                    if (drawerGroup == g) Redraw();
                });
            });
        }
    }

    // MARK: Running

    void RunCommand(CanvasGroup g, GroupCommand c)
    {
        c.Error = null;
        var before = new HashSet<nint>();
        EnumWindows((h, _) => { if (IsWindowVisible(h)) before.Add(h); return true; }, 0);
        var pending = new PendingCommand(g, c, before);
        pendingCommands.Add(pending);
        string line = c.Line;
        Log.Info($"group \"{g.Name}\": starting command {g.Commands.IndexOf(c) + 1}");
        var thread = new Thread(() =>
        {
            uint pid = 0;
            string? failed;
            try
            {
                var target = Launcher.Find(line);
                pending.Program = target.Program ?? "";
                pending.Console = Launcher.OpensTerminal(pending.Program);
                (pid, int error) = Launcher.Start(target);
                failed = error == 0 ? null : GroupCommands.Reason(error);
                // A document's or a link's, or a program started by a name Windows knows.
                if (error == 0 && pending.Program.Length == 0 && pid != 0) pending.Program = Kernel32.ProcessPath(pid) ?? "";
            }
            catch (Exception e)
            {
                failed = "couldn't start";
                Log.Info($"command: threw {e.GetType().Name}");
            }
            Post(() => CommandStarted(pending, pid, failed));
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); // the shell's
        thread.Start();
    }

    void CommandStarted(PendingCommand pending, uint pid, string? error)
    {
        pending.Pid = pid;
        pending.At = Environment.TickCount64;
        pending.Started = true;
        if (error != null) pendingCommands.Remove(pending);
        pending.Command.Error = error;
        Log.Info($"group \"{pending.Group.Name}\": command {(error ?? $"started, process {pid}")}");
        if (drawerGroup == pending.Group) Redraw();
    }

    // MARK: Its window

    /// The command a window that has just come is from, if any.
    PendingCommand? CommandOpening(nint h)
    {
        if (pendingCommands.Count == 0) return null;
        long now = Environment.TickCount64;
        pendingCommands.RemoveAll(p => p.Started && now - p.At > GroupCommands.ClaimMs);
        var open = pendingCommands.Where(p => !p.Before.Contains(h) && (p.Window == 0 || !IsWindow(p.Window))
            && (groups.Contains(p.Group) || (pinnedGroups.Contains(p.Group) && p.Group.SlideHome == null && !IsZoomed(h)))).ToList();
        if (open.Count == 0) return null;
        GetWindowThreadProcessId(h, out uint pid);
        var claims = open.Select(p => new CommandClaim(p.Started ? p.At : now, p.Pid, p.Program, p.Console)).ToList();
        int i = GroupCommands.Claim(claims, now, pid, Processes.Ancestors(pid), Kernel32.ProcessPath(pid) ?? "",
            Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (i < 0) return null;
        open[i].Window = h;
        return open[i];
    }

    /// Notes whose window it is, and where it goes in its group's box: the command's spot, or a free
    /// place, the first time and while the command's own window is still at its spot.
    RectD CommandPlace(PendingCommand pending, CanvasWindow w, RectD frame, IReadOnlyList<RectD> members)
    {
        var (g, c) = (pending.Group, pending.Command);
        commandWindows[w.Hwnd] = Environment.TickCount64;
        bool taken = c.Window != 0 && c.Window != w.Hwnd && IsWindowOf(c.Window, c.Pid);
        if (!taken) (c.Window, c.Pid) = (w.Hwnd, w.Pid);
        if (c.Spot is not RectD spot || taken) return GroupCommands.FreePlace(g.Box, frame.Size, members);
        if (!w.Maximised && !w.SizesItself && spot.Size != frame.Size)
        {
            var actual = ResizeReal(w, Layout.Cap(spot.Size, WorkSize), remakePicture: false);
            w.Preferred = spot.Size;
            frame = frame with { W = actual.W, H = actual.H };
        }
        return new RectD(g.Box.X + spot.X, g.Box.Y + spot.Y, frame.W, frame.H);
    }

    static bool IsWindowOf(nint hwnd, uint pid)
    {
        if (!IsWindow(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out uint owner);
        return owner == pid;
    }

    /// A command's window, on the canvas: into its group. From there its spot is like one you gave it.
    void PlaceForCommand(CanvasWindow w, PendingCommand pending)
    {
        var g = pending.Group;
        var before = w.Frame.Origin;
        var members = MembersOf(g).Where(m => m != w).Select(GroupFrame).ToList();
        var frame = CommandPlace(pending, w, GroupFrame(w), members);
        (g.Box, frame) = GroupCommands.Hold(g.Box, frame, OtherBoxes(g));
        w.Frame = w.Maximised ? w.Frame with { X = frame.X, Y = frame.Y } : frame;
        NoteUserMove(w, before);
        // Where its window was when it closed: the same place, so it's not for another to take.
        savedPool.RemoveAll(e => e.App == w.AppPath && Math.Abs(e.X - frame.X) <= 1 && Math.Abs(e.Y - frame.Y) <= 1);
        placedForCommand.Add(w.Hwnd);
        Log.Info($"{w}: opened by a command of group \"{g.Name}\", at {w.Frame}");
        if (!active) Place(w);
        ScheduleSave();
    }

    /// The same for a pinned group, whose apps aren't the canvas's.
    void JoinForCommand(nint h, PendingCommand pending)
    {
        var g = pending.Group;
        var w = new CanvasWindow(h);
        w.Frame = ViewFrame(w.ReadScreenFrame());
        w.Preferred = w.Frame.Size;
        elsewhere.Remove(h);
        var frame = CommandPlace(pending, w, w.Frame, g.Pinned!.Select(m => m.Frame).ToList());
        PlacePinned(w, Pinning.OnScreen(frame, PinnedRoom));
        JoinPinned(g, w, w.Frame);
        if (!active || gliding) return;
        // After this sync.
        Post(() =>
        {
            if (!active || gliding || PinnedGroupOf(w) == null) return;
            pictures.Add(w);
            pictures.Pin(w, true);
            pictures.Moved(w, camera, View);
            Relayout();
        });
    }

    /// Where a command's window is now is its spot, while it's in the group. Not a brief window's: a
    /// splash screen's size isn't its app's. One you moved counts at once.
    void NoteCommandSpot(CanvasWindow w)
    {
        if (suspendTracking || (!w.MovedByYou && Environment.TickCount64 - w.SeenAt < BriefWindowMs)) return;
        foreach (var g in groups.Concat(pinnedGroups))
            foreach (var c in g.Commands.Where(c => c.Window == w.Hwnd && c.Pid == w.Pid))
            {
                var f = g.Pinned != null ? w.Frame : GroupFrame(w);
                if (g.Pinned != null ? !g.Pinned.Contains(w) : !Groups.IsMember(g.Box, f)) continue;
                var size = !w.Maximised && w.Preferred.W > 0 ? w.Preferred : f.Size;
                c.Spot = new RectD(f.X - g.Box.X, f.Y - g.Box.Y, size.W, size.H);
            }
    }

    void NoteCommandSpots()
    {
        foreach (var w in windows.Concat(PinnedApps)) NoteCommandSpot(w);
    }

    /// Zoomed out, a command's window took focus as it opened: the canvas has it back, as you're still
    /// on it, and Esc and the rest are its.
    bool KeepCanvasFocus(nint hwnd)
    {
        if (!active || gliding || commandWindows.Count == 0) return false;
        long now = Environment.TickCount64;
        foreach (nint old in commandWindows.Where(c => now - c.Value > CommandFocusMs).Select(c => c.Key).ToList()) commandWindows.Remove(old);
        nint root = CanvasWindow.RootOwner(hwnd);
        if (!commandWindows.ContainsKey(root)) return false;
        Log.Info($"{CanvasWindow.Describe(hwnd)} (opened by a command) got focus zoomed out: {Focus(overlay.Hwnd)}");
        return true;
    }

    // MARK: Drawing

    DrawerDrawing? DrawerDrawingNow(double k, bool dark)
    {
        if (drawerGroup is not CanvasGroup g || DrawerNow(k) is not DrawerLayout layout) return null;
        int size = (int)Math.Round(GroupCommands.IconSize * k);
        var tiles = DrawerEditing ? [] : g.Commands.Select(c =>
            new DrawerTileDrawing(c.Name, c.Error ?? "", c.IconFile is string file ? appIcons.OfFile(file, size) : null)).ToList();
        return new DrawerDrawing(layout, tiles, drawerHover.part, drawerHover.tile, dark);
    }

    /// On the GDI backdrop, plainly (see Paint): a list of names.
    void PaintDrawer(nint hdc, double k)
    {
        if (DrawerDrawingNow(k, true) is not DrawerDrawing d) return;
        RECT R(RectD r) => new((int)r.X, (int)r.Y, (int)r.Right, (int)r.Bottom);
        const uint centred = DT_SINGLELINE | DT_VCENTER | DT_CENTER | DT_NOPREFIX | DT_END_ELLIPSIS;
        nint back = Gdi32.CreateSolidBrush(Gdi32.RGB(31, 31, 33)), field = Gdi32.CreateSolidBrush(Gdi32.RGB(55, 55, 58)), red = Gdi32.CreateSolidBrush(Gdi32.RGB(140, 40, 40));
        var panel = R(d.Layout.Panel);
        FillRect(hdc, ref panel, back);
        void Button(RectD at, string text, nint fill)
        {
            if (at.W <= 0) return;
            var r = R(at);
            FillRect(hdc, ref r, fill);
            DrawText(hdc, text, -1, ref r, centred);
        }
        for (int i = 0; i < d.Tiles.Count; i++)
        {
            Button(d.Layout.Tiles[i].Rect, d.Tiles[i].Name, d.Tiles[i].Error.Length > 0 ? red : field);
            Button(d.Layout.Tiles[i].Edit, "...", back);
        }
        Button(d.Layout.Add, "+", field);
        Button(d.Layout.RunAll, DrawerDrawing.RunAllText, field);
        if (d.Layout.Editor is DrawerEditor e)
        {
            Button(e.NameLabel, DrawerDrawing.NameText, back);
            Button(e.LineLabel, DrawerDrawing.LineText, back);
            Button(e.Save, DrawerDrawing.SaveText, field);
            Button(e.Delete, DrawerDrawing.DeleteText, field);
            Button(e.Cancel, DrawerDrawing.CancelText, field);
        }
        foreach (nint brush in new[] { back, field, red }) Gdi32.DeleteObject(brush);
    }

    // MARK: Saving

    static List<SavedCommand> SavedCommands(CanvasGroup g) => g.Commands.Select(c => new SavedCommand
    {
        Name = c.Name, Line = c.Line, X = c.Spot?.X, Y = c.Spot?.Y, W = c.Spot?.W, H = c.Spot?.H, Hwnd = c.Window, Pid = c.Pid,
    }).ToList();

    static List<GroupCommand> TakeSavedCommands(IEnumerable<SavedCommand> saved) => saved.Select(s =>
    {
        var c = new GroupCommand(s.Name, s.Line);
        if (s.X is double x && s.Y is double y && s.W is double w && s.H is double h) c.Spot = new RectD(x, y, w, h);
        if (IsWindowOf((nint)s.Hwnd, s.Pid)) (c.Window, c.Pid) = ((nint)s.Hwnd, s.Pid); // Expanse restarted
        return c;
    }).ToList();
}
