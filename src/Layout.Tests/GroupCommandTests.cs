using Expanse;

/// A group's command drawer (GroupCommands).
static class GroupCommandTests
{
    public static void Run(Action<string, bool, string> expect)
    {
        void Is(string name, bool ok, string detail = "") => expect($"commands: {name}", ok, detail);
        const double P = Groups.Padding, G = Groups.Gap;

        // --- The drawer ---
        var view = new RectD(0, 0, 1920, 1080);
        var button = new RectD(400, 200, 22, 22);
        foreach (double k in new[] { 1.0, 1.25, 1.5, 2 })
            foreach (int count in new[] { 0, 1, 4, 5, 14, 15, GroupCommands.MaxCommands })
            {
                var d = GroupCommands.Tiles(button, view, count, errors: true, k);
                string what = $"{count} at {k}";
                var parts = d.Tiles.Select(t => t.Rect).Append(d.Add).ToList();
                Is($"a tile each ({what})", d.Tiles.Count == count);
                Is($"all on the panel ({what})", parts.All(d.Panel.Contains) && (count == 0 || d.Panel.Contains(d.RunAll)), $"{d.Panel}");
                Is($"none over another ({what})", parts.SelectMany((a, i) => parts.Skip(i + 1).Select(b => (a, b))).All(p => !p.a.Intersects(p.b)));
                Is($"Run all clear of them ({what})", count == 0 || parts.All(p => !p.Intersects(d.RunAll)));
                Is($"on the screen ({what})", view.Contains(d.Panel), $"{d.Panel}");
                Is($"what's on a tile is inside it ({what})", d.Tiles.All(t => t.Rect.Contains(t.Icon) && t.Rect.Contains(t.Name) && t.Rect.Contains(t.Error) && t.Rect.Contains(t.Edit)));
            }
        var two = GroupCommands.Tiles(button, view, 2, errors: false, 1);
        Is("below the button", two.Panel.Y >= button.Bottom && two.Panel.X == button.X);
        Is("shorter without errors", two.Panel.H < GroupCommands.Tiles(button, view, 2, errors: true, 1).Panel.H);
        var low = GroupCommands.Tiles(new(1900, 1050, 22, 22), view, 2, errors: false, 1);
        Is("above it at the bottom of the screen", low.Panel.Bottom <= 1050 && view.Contains(low.Panel));
        Is("nothing to run in an empty one", GroupCommands.Tiles(button, view, 0, false, 1).RunAll.W == 0);
        Is("at: a tile", two.At(two.Tiles[1].Icon.Center) == (DrawerPart.Tile, 1));
        Is("at: its pencil", two.At(two.Tiles[1].Edit.Center) == (DrawerPart.Edit, 1));
        Is("at: the + tile", two.At(two.Add.Center) == (DrawerPart.Add, -1));
        Is("at: Run all", two.At(two.RunAll.Center) == (DrawerPart.RunAll, -1));
        Is("at: between them", two.At(new(two.Panel.X + 1, two.Panel.Y + 1)) == (DrawerPart.Panel, -1));
        Is("at: outside", two.At(new(10, 10)) == (DrawerPart.None, -1));

        var edit = GroupCommands.Editor(button, view, existing: true, 1);
        var e = edit.Editor!;
        RectD[] fields = [e.NameLabel, e.Name, e.LineLabel, e.Line, e.Save, e.Delete, e.Cancel];
        Is("editor: all on the panel", fields.All(edit.Panel.Contains));
        Is("editor: none over another", fields.SelectMany((a, i) => fields.Skip(i + 1).Select(b => (a, b))).All(p => !p.a.Intersects(p.b)));
        Is("editor: its buttons", edit.At(e.Save.Center).part == DrawerPart.Save && edit.At(e.Delete.Center).part == DrawerPart.Delete && edit.At(e.Cancel.Center).part == DrawerPart.Cancel);
        var fresh = GroupCommands.Editor(button, view, existing: false, 1);
        Is("editor: a new one can't be deleted", fresh.Editor!.Delete.W == 0 && fresh.At(fresh.Editor.Cancel.Center).part == DrawerPart.Cancel);

        // --- Command lines ---
        void Split(string line, string program, string arguments, params string[] files)
        {
            var got = GroupCommands.Split(line, files.Contains);
            Is($"split: {line}", got == (program, arguments), $"{got}");
        }
        Split("code", "code", "");
        Split("code \"C:\\src\\kvalitet\"", "code", "\"C:\\src\\kvalitet\"");
        Split("  firefox -P Work -no-remote ", "firefox", "-P Work -no-remote");
        Split("\"C:\\Program Files\\A\\a.exe\" -x \"y z\"", @"C:\Program Files\A\a.exe", "-x \"y z\"");
        Split("\"C:\\Program Files\\A\\a.exe", @"C:\Program Files\A\a.exe", "");
        Split(@"C:\Program Files\A\a.exe -x", @"C:\Program Files\A\a.exe", "-x", @"C:\Program Files\A\a.exe");
        Split(@"C:\My Docs\plan.xlsx", @"C:\My Docs\plan.xlsx", "", @"C:\My Docs\plan.xlsx");
        Split("", "", "");
        Is("reason: not found", GroupCommands.Reason(2) == "not found");
        Is("reason: any other error has its number", GroupCommands.Reason(4711).Contains("4711"));

        // --- Which window ---
        const string win = @"C:\Windows", code = @"C:\Apps\VS Code\Code.exe", fox = @"C:\Apps\Firefox\firefox.exe";
        Is("program: itself, whatever the case", GroupCommands.SameProgram(code, code.ToUpperInvariant(), win));
        Is("program: not another's", !GroupCommands.SameProgram(code, fox, win));
        Is("program: one of the same name elsewhere", GroupCommands.SameProgram(@"C:\Windows\System32\notepad.exe", @"C:\Program Files\WindowsApps\N\Notepad.exe", win));
        Is("program: another in its folder", GroupCommands.SameProgram(@"C:\Apps\T\wt.exe", @"C:\Apps\T\WindowsTerminal.exe", win));
        Is("program: not another in Windows' folder", !GroupCommands.SameProgram(@"C:\Windows\System32\cmd.exe", @"C:\Windows\System32\mmc.exe", win));
        Is("program: a script in the app's bin folder", GroupCommands.SameProgram(@"C:\Apps\VS Code\bin\code.cmd", code, win));
        Is("program: no program", !GroupCommands.SameProgram("", code, win) && !GroupCommands.SameProgram(code, "", win));

        int Claim(CommandClaim[] claims, long now, uint pid, string exe, params uint[] ancestors) =>
            GroupCommands.Claim(claims, now, pid, ancestors, exe, win);
        CommandClaim[] pair = [new(2000, 20, code), new(1000, 10, code)];
        Is("claim: its own process", Claim(pair, 3000, 20, code) == 0);
        Is("claim: a process its own started", Claim(pair, 3000, 99, fox, 98, 10) == 1);
        Is("claim: otherwise the oldest of the program", Claim(pair, 3000, 99, code) == 1);
        Is("claim: not another program", Claim(pair, 3000, 99, fox) == -1);
        Is("claim: up to its time", Claim([new(1000, 10, code)], 1000 + GroupCommands.ClaimMs, 10, code) == 0);
        Is("claim: not after", Claim([new(1000, 10, code)], 1001 + GroupCommands.ClaimMs, 10, code) == -1);
        Is("claim: an unknown process matches none by it", Claim([new(1000, 0, "")], 2000, 0, code, 0) == -1);
        Is("claim: none", Claim([], 0, 1, code) == -1);
        const string terminal = @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1\WindowsTerminal.exe", cmd = @"C:\Windows\System32\cmd.exe";
        Is("claim: a console program's window is a terminal's", Claim([new(1000, 10, cmd, true)], 2000, 99, terminal) == 0);
        Is("claim: the oldest console program's", Claim([new(1500, 10, cmd, true), new(1000, 11, cmd, true)], 2000, 99, terminal) == 1);
        Is("claim: another program's window isn't", Claim([new(1000, 10, cmd, true)], 2000, 99, fox) == -1);
        Is("claim: nor a terminal's another program's", Claim([new(1000, 10, code)], 2000, 99, terminal) == -1);
        Is("claim: a terminal run by name comes first", Claim([new(1000, 10, cmd, true), new(1500, 11, terminal)], 2000, 99, terminal) == 1);

        byte[] Exe(byte subsystem)
        {
            var file = new byte[0x200];
            (file[0], file[1], file[0x3C], file[0x80], file[0x81], file[0x80 + 92]) = ((byte)'M', (byte)'Z', 0x80, (byte)'P', (byte)'E', subsystem);
            return file;
        }
        Is("terminal: a console program", GroupCommands.OpensTerminal(cmd, Exe(3)));
        Is("terminal: not a window program", !GroupCommands.OpensTerminal(code, Exe(2)));
        Is("terminal: a script, unread", GroupCommands.OpensTerminal(@"C:\x\start.cmd", []));
        Is("terminal: wt, unread", GroupCommands.OpensTerminal(@"C:\x\WT.EXE", []));
        Is("terminal: not an unread program", !GroupCommands.OpensTerminal(code, []));
        Is("terminal: not a cut-off file", !GroupCommands.OpensTerminal(cmd, Exe(3).AsSpan(0, 0x80 + 93)));

        // --- Where it goes ---
        void Place(string name, RectD want, RectD box, SizeD size, params RectD[] members)
        {
            var got = GroupCommands.FreePlace(box, size, members);
            Is($"place: {name}", got == want, $"want {want}, got {got}");
        }
        var box = new RectD(100, 100, 1000, 600);
        var small = new SizeD(200, 150);
        var first = new RectD(100 + P, 100 + P, 200, 150);
        Place("an empty group's corner", first, box, small);
        Place("beside the one there", first with { X = first.Right + G }, box, small, first);
        Place("the next row when the first is full", first with { Y = first.Bottom + G }, box, small, first with { W = 900 });
        Place("too big for an empty group", new(100 + P, 100 + P, 3000, 3000), box, new(3000, 3000));
        Place("a full wide group grows the least to the right", new(1100 - P + G, 100 + P, 200, 150), box, small, box.Inset(P, P));
        Place("a full tall one below", new(100 + P, 1100 - P + G, 200, 150), new(100, 100, 600, 1000), small, new RectD(100, 100, 600, 1000).Inset(P, P));
        foreach (var member in new[] { new RectD(130, 130, 300, 200), new(500, 300, 400, 300), new(124, 400, 600, 100) })
        {
            var r = GroupCommands.FreePlace(box, small, [member]);
            Is($"place: in the box and clear of {member}", box.Inset(P, P).Contains(r) && !r.Intersects(member), $"{r}");
        }
        var (same, stays) = GroupCommands.Hold(box, first, []);
        Is("hold: one inside changes nothing", same == box && stays == first);
        var outside = new RectD(1200, 100 + P, 300, 200);
        var (grown, held) = GroupCommands.Hold(box, outside, []);
        Is("hold: grows round it", grown.Contains(outside.Inset(-P, -P)) && held == outside);
        var other = new RectD(1150, 0, 500, 900);
        var (stopped, slid) = GroupCommands.Hold(box, outside, [other]);
        Is("hold: stops at another group", !stopped.Intersects(other) && stopped.Inset(P, P).Contains(slid), $"{stopped} {slid}");
        Is("hold: so it's the group's", Groups.IsMember(stopped, slid));
    }
}
