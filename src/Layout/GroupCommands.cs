namespace Expanse;

/// What's under the pointer in a group's command drawer. `Panel`: none of the parts on it.
public enum DrawerPart { None, Panel, Tile, Edit, Add, RunAll, Save, Delete, Cancel }

/// One command in the drawer, in view space. `Error` is empty where the drawer shows none.
public sealed record DrawerTile(RectD Rect, RectD Icon, RectD Name, RectD Error, RectD Edit);

/// The two fields a command is edited in, with their names and buttons. `Delete` is empty for a new one.
public sealed record DrawerEditor(RectD NameLabel, RectD Name, RectD LineLabel, RectD Line, RectD Save, RectD Delete, RectD Cancel);

/// A group's command drawer in view space: tiles in rows, or the editor alone. `RunAll` is empty
/// where there's nothing to run.
public sealed record DrawerLayout(RectD Panel, IReadOnlyList<DrawerTile> Tiles, RectD Add, RectD AddIcon, RectD RunAll, DrawerEditor? Editor)
{
    /// What's at `p`, and which tile it's of (-1: none).
    public (DrawerPart part, int tile) At(Vec p)
    {
        if (!Panel.Contains(p)) return (DrawerPart.None, -1);
        if (Editor is DrawerEditor e)
            return (e.Save.Contains(p) ? DrawerPart.Save : e.Delete.Contains(p) ? DrawerPart.Delete
                : e.Cancel.Contains(p) ? DrawerPart.Cancel : DrawerPart.Panel, -1);
        for (int i = 0; i < Tiles.Count; i++)
        {
            if (Tiles[i].Edit.Contains(p)) return (DrawerPart.Edit, i);
            if (Tiles[i].Rect.Contains(p)) return (DrawerPart.Tile, i);
        }
        return (Add.Contains(p) ? DrawerPart.Add : RunAll.Contains(p) ? DrawerPart.RunAll : DrawerPart.Panel, -1);
    }
}

/// A command started, whose window hasn't come yet. `Pid` is 0 until it's known (or if it stays
/// unknown), and `Program` the file it runs. `At` is when it was started (Environment.TickCount64).
/// `Console`: its window can be a terminal's.
public readonly record struct CommandClaim(long At, uint Pid, string Program, bool Console = false);

/// A group's command lines: the drawer they're started from, which window is whose, and where it goes.
public static class GroupCommands
{
    /// At a display scale of 1.
    public const double TileWidth = 84, TilePad = 6, IconSize = 32, NameHeight = 18, ErrorHeight = 16, EditSize = 20,
        Pad = 10, Gap = 4, ButtonWidth = 76, ButtonHeight = 26, FieldHeight = 26, FieldLabel = 44, EditorWidth = 380, Margin = 8;
    /// Tiles to a row, and for a long list, so that it fits on the screen.
    public const int Columns = 5, ManyColumns = 9;
    public const int MaxCommands = 40;
    /// How long a command's window may take to come.
    public const long ClaimMs = 15_000;

    /// The drawer under `button` (the one that opens it), or above it where there's no room below,
    /// and always on the screen. `errors`: some command failed, so tiles have a line for it.
    public static DrawerLayout Tiles(RectD button, RectD view, int count, bool errors, double k)
    {
        double S(double v) => Math.Round(v * k);
        int columns = Math.Min(count + 1, count + 1 > 3 * Columns ? ManyColumns : Columns);
        int rows = (count + columns) / columns; // with the + tile
        double tileH = S(TilePad) * 2 + S(IconSize) + S(NameHeight) + (errors ? S(ErrorHeight) : 0);
        double runH = count > 0 ? S(Gap) + S(ButtonHeight) : 0;
        double w = S(Pad) * 2 + columns * S(TileWidth) + (columns - 1) * S(Gap);
        double h = S(Pad) * 2 + rows * tileH + (rows - 1) * S(Gap) + runH;
        var panel = PanelAt(button, view, w, h, k);
        RectD Cell(int i) => new(panel.X + S(Pad) + i % columns * (S(TileWidth) + S(Gap)),
            panel.Y + S(Pad) + i / columns * (tileH + S(Gap)), S(TileWidth), tileH);
        RectD IconIn(RectD r) => new(r.X + Math.Floor((r.W - S(IconSize)) / 2), r.Y + S(TilePad), S(IconSize), S(IconSize));
        var tiles = new List<DrawerTile>(count);
        for (int i = 0; i < count; i++)
        {
            var r = Cell(i);
            var name = new RectD(r.X + S(2), r.Y + S(TilePad) + S(IconSize), r.W - 2 * S(2), S(NameHeight));
            tiles.Add(new DrawerTile(r, IconIn(r), name, errors ? name with { Y = name.Bottom, H = S(ErrorHeight) } : default,
                new RectD(r.Right - S(EditSize) - S(2), r.Y + S(2), S(EditSize), S(EditSize))));
        }
        var add = Cell(count);
        var run = count == 0 ? default : new RectD(panel.Right - S(Pad) - S(ButtonWidth), panel.Bottom - S(Pad) - S(ButtonHeight), S(ButtonWidth), S(ButtonHeight));
        return new DrawerLayout(panel, tiles, add, IconIn(add), run, null);
    }

    /// The drawer while a command is edited (`existing`), or a new one made.
    public static DrawerLayout Editor(RectD button, RectD view, bool existing, double k)
    {
        double S(double v) => Math.Round(v * k);
        double field = S(FieldHeight), w = Math.Min(S(EditorWidth), view.W - 2 * S(Margin));
        var panel = PanelAt(button, view, w, S(Pad) * 2 + field * 2 + S(Gap) * 3 + S(ButtonHeight), k);
        double x = panel.X + S(Pad), y = panel.Y + S(Pad), fieldX = x + S(FieldLabel);
        RectD Label(double top) => new(x, top, S(FieldLabel), field);
        RectD Field(double top) => new(fieldX, top, panel.Right - S(Pad) - fieldX, field);
        double second = y + field + S(Gap), buttons = panel.Bottom - S(Pad) - S(ButtonHeight);
        RectD Button(int i) => new(fieldX + i * (S(ButtonWidth) + S(Gap)), buttons, S(ButtonWidth), S(ButtonHeight));
        return new DrawerLayout(panel, [], default, default, default,
            new DrawerEditor(Label(y), Field(y), Label(second), Field(second), Button(0), existing ? Button(1) : default, Button(existing ? 2 : 1)));
    }

    static RectD PanelAt(RectD button, RectD view, double w, double h, double k)
    {
        double margin = Math.Round(Margin * k), apart = Math.Round(Gap * k);
        double y = button.Bottom + apart;
        if (y + h > view.Bottom - margin && button.Y - apart - h >= view.Y + margin) y = button.Y - apart - h;
        return new RectD(Math.Max(view.X + margin, Math.Min(Math.Round(button.X), view.Right - margin - w)),
            Math.Max(view.Y + margin, Math.Min(Math.Round(y), view.Bottom - margin - h)), w, h);
    }

    // MARK: Command lines

    /// A command line's program and the rest of it, as written. An unquoted program with spaces in
    /// its path is the shortest one `exists` knows; otherwise the program ends at the first space.
    public static (string program, string arguments) Split(string line, Func<string, bool> exists)
    {
        line = line.Trim();
        if (line.StartsWith('"'))
        {
            int end = line.IndexOf('"', 1);
            return end < 0 ? (line[1..].Trim(), "") : (line[1..end], line[(end + 1)..].Trim());
        }
        int first = line.IndexOf(' ');
        if (first < 0) return (line, "");
        for (int space = first; space >= 0; space = line.IndexOf(' ', space + 1))
            if (exists(line[..space])) return (line[..space], line[(space + 1)..].Trim());
        return exists(line) ? (line, "") : (line[..first], line[(first + 1)..].Trim());
    }

    /// Why a command didn't start, short enough for its tile (a Windows error code).
    public static string Reason(int error) => error switch
    {
        2 or 3 => "not found",
        5 => "access denied",
        31 or 1155 => "no app opens it",
        193 => "not a program",
        740 => "needs admin",
        1223 => "you said no",
        _ => $"error {error}",
    };

    // MARK: Which window is a command's

    static string FolderOf(string path) => path.LastIndexOf('\\') is var i && i > 0 ? path[..i] : "";
    static string NameOf(string path) => path[(path.LastIndexOf('\\') + 1)..];
    static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    static bool IsScript(string path) =>
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    /// Whether a window of `exe` can be what running `program` opened: the same exe (one that hands
    /// over to the one already running), one of that name elsewhere (Windows' Notepad starts the
    /// Store's), or another in its folder (a launcher), though not in Windows' own, where everything
    /// is. A script in an app's `bin` folder starts the app above it.
    public static bool SameProgram(string program, string exe, string windowsFolder)
    {
        if (program.Length == 0 || exe.Length == 0) return false;
        if (Same(program, exe) || Same(NameOf(program), NameOf(exe))) return true;
        string folder = FolderOf(program), other = FolderOf(exe);
        if (folder.Length == 0 || folder.StartsWith(windowsFolder, StringComparison.OrdinalIgnoreCase)) return false;
        return Same(folder, other) || (IsScript(program) && Same(NameOf(folder), "bin") && Same(FolderOf(folder), other));
    }

    /// Windows Terminal, whose windows show the console programs' (as Windows 11 has it) and not theirs.
    public static bool IsTerminal(string exe) => Same(NameOf(exe), "WindowsTerminal.exe");

    /// Whether running `program` can open a terminal's window: a console program, going by its file
    /// (its first 4 KB, or as much as there is), a script, or the command for a terminal.
    public static bool OpensTerminal(string program, ReadOnlySpan<byte> file)
    {
        if (IsScript(program) || Same(NameOf(program), "wt.exe")) return true;
        // The PE header: its offset at 0x3C, and the subsystem 92 bytes into it (3: console).
        if (file.Length < 0x40 || file[0] != 'M' || file[1] != 'Z') return false;
        int header = BitConverter.ToInt32(file[0x3C..]);
        return header >= 0 && header <= file.Length - 94 && file[header] == 'P' && file[header + 1] == 'E' && BitConverter.ToUInt16(file[(header + 92)..]) == 3;
    }

    /// The claim a new window answers (-1: none): the one whose process it's from, or started it
    /// (`ancestors`), and otherwise the oldest one for the same program, or if it's a terminal's, the
    /// oldest one for a console program. Not one too old.
    public static int Claim(IReadOnlyList<CommandClaim> claims, long now, uint pid, IReadOnlyCollection<uint> ancestors, string exe, string windowsFolder)
    {
        int program = -1, terminal = -1;
        for (int i = 0; i < claims.Count; i++)
        {
            var c = claims[i];
            if (now - c.At > ClaimMs) continue;
            if (c.Pid != 0 && (c.Pid == pid || ancestors.Contains(c.Pid))) return i;
            if ((program < 0 || c.At < claims[program].At) && SameProgram(c.Program, exe, windowsFolder)) program = i;
            if ((terminal < 0 || c.At < claims[terminal].At) && c.Console && IsTerminal(exe)) terminal = i;
        }
        return program >= 0 ? program : terminal;
    }

    // MARK: Where its window goes

    /// A free place in the group's box for a window of `size`, the first along its rows; beside what's
    /// there, where the box has to grow the least, if there's none.
    public static RectD FreePlace(RectD box, SizeD size, IReadOnlyList<RectD> members)
    {
        var inside = box.Inset(Groups.Padding, Groups.Padding);
        var xs = members.Select(m => m.Right + Groups.Gap).Prepend(inside.X).ToList();
        var ys = members.Select(m => m.Bottom + Groups.Gap).Prepend(inside.Y).Order().ToList();
        xs.Sort();
        foreach (double y in ys)
            foreach (double x in xs)
            {
                var r = new RectD(x, y, size.W, size.H);
                if (inside.Contains(r) && !members.Any(m => m.Inset(-Groups.Gap, -Groups.Gap).Intersects(r))) return r;
            }
        if (members.Count == 0) return new RectD(inside.X, inside.Y, size.W, size.H);
        var all = members.Aggregate((a, b) => a.Union(b));
        var right = new RectD(all.Right + Groups.Gap, all.Y, size.W, size.H);
        var below = new RectD(all.X, all.Bottom + Groups.Gap, size.W, size.H);
        double Grown(RectD r) => box.Union(Groups.Fit([r])) is var u ? u.W * u.H : 0;
        return Grown(right) <= Grown(below) ? right : below;
    }

    /// The group's box grown to hold `window`, as far as the other groups let it, and the window inside it.
    public static (RectD box, RectD window) Hold(RectD box, RectD window, IReadOnlyList<RectD> others)
    {
        box = Groups.Grow(box, box.Union(Groups.Fit([window])), others);
        var inside = box.Inset(Groups.Padding, Groups.Padding);
        double X() => window.W > inside.W ? box.MidX - window.W / 2 : Math.Clamp(window.X, inside.X, inside.Right - window.W);
        double Y() => window.H > inside.H ? box.MidY - window.H / 2 : Math.Clamp(window.Y, inside.Y, inside.Bottom - window.H);
        return (box, window with { X = Layout.Round(X()), Y = Layout.Round(Y()) });
    }
}
