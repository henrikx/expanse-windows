using Expanse;

/// The groups panel: what the tree shows, what a search leaves, and where things are (GroupTree).
static class TreeTests
{
    public static void Run(Action<string, bool, string> expect)
    {
        static string Show(IEnumerable<TreeRow> rows) => string.Join(" | ", rows.Select(r => $"{r.Depth}:{r.Text}"));
        void Rows(string name, List<TreeRow> got, string want) => expect($"tree: {name}", Show(got) == want, $"want [{want}], got [{Show(got)}]");
        void Is(string name, bool ok) => expect($"tree: {name}", ok, "not so");

        TreeApp edge = new(1, "Edge", "Budget 2026"), code = new(2, "Code", "Groups.cs"), mail = new(3, "Outlook", "Inbox"),
            term = new(4, "Terminal", "Terminal"), notes = new(5, "Notepad", "");
        TreeGroup work = new(10, "Work", false, [edge, code]), home = new(11, "Home", false, [mail]), empty = new(12, "Ideas", false, []);
        List<TreeGroup> groups = [work, home, empty];
        List<TreeApp> others = [term, notes];

        // --- The whole tree ---
        Rows("groups by name, apps by name, the rest last", GroupTree.Rows(groups, others, false, ""),
            "0:Home | 1:Outlook — Inbox | 0:Ideas | 0:Work | 1:Code — Groups.cs | 1:Edge — Budget 2026 | 0:Other apps | 1:Notepad | 1:Terminal");
        Rows("no ungrouped apps, no section", GroupTree.Rows([home], [], false, ""), "0:Home | 1:Outlook — Inbox");
        Rows("no groups: just the apps", GroupTree.Rows([], [notes], false, ""), "0:Other apps | 1:Notepad");
        Rows("nothing at all", GroupTree.Rows([], [], false, ""), "");
        Rows("a collapsed group hides its apps", GroupTree.Rows([work with { Collapsed = true }], [], false, ""), "0:Work");
        Rows("collapsed others too", GroupTree.Rows([], others, true, ""), "0:Other apps");
        var rows = GroupTree.Rows(groups, others, false, "");
        var workRow = rows.First(r => r.Text == "Work");
        Is("a group row says what it is", workRow.Kind == TreeRowKind.Group && workRow.Key == 10 && workRow.Expandable && workRow.Expanded && workRow.Count == 2);
        Is("an empty group has nothing to open", !rows.First(r => r.Text == "Ideas").Expandable);
        var collapsedRow = GroupTree.Rows([work with { Collapsed = true }], [], false, "")[0];
        Is("a collapsed group still counts its apps", !collapsedRow.Expanded && collapsedRow.Count == 2);
        var appRow = rows.First(r => r.Key == 1);
        Is("an app row says what it is", appRow.Kind == TreeRowKind.App && !appRow.Expandable);
        Is("the rest are a section", rows.First(r => r.Text == "Other apps").Kind == TreeRowKind.Others);

        // --- Searching the whole tree ---
        Rows("a group's name: the group and all its apps", GroupTree.Rows(groups, others, false, "work"), "0:Work | 1:Code — Groups.cs | 1:Edge — Budget 2026");
        Rows("an app's name: only that app, under its group", GroupTree.Rows(groups, others, false, "edge"), "0:Work | 1:Edge — Budget 2026");
        Rows("a window's title", GroupTree.Rows(groups, others, false, "budget"), "0:Work | 1:Edge — Budget 2026");
        Rows("any case", GroupTree.Rows(groups, others, false, "OUTLOOK"), "0:Home | 1:Outlook — Inbox");
        Rows("part of a word", GroupTree.Rows(groups, others, false, "inb"), "0:Home | 1:Outlook — Inbox");
        Rows("an ungrouped app, under its section", GroupTree.Rows(groups, others, false, "note"), "0:Other apps | 1:Notepad");
        Rows("words may match across group and app", GroupTree.Rows(groups, others, false, "work code"), "0:Work | 1:Code — Groups.cs");
        Rows("every word has to match", GroupTree.Rows(groups, others, false, "work inbox"), "");
        Rows("an empty group by its name", GroupTree.Rows(groups, others, false, "idea"), "0:Ideas");
        Rows("a collapsed group opens for what's found", GroupTree.Rows([work with { Collapsed = true }], [], false, "edge"), "0:Work | 1:Edge — Budget 2026");
        Rows("collapsed others open too", GroupTree.Rows([], others, true, "note"), "0:Other apps | 1:Notepad");
        Rows("no matches", GroupTree.Rows(groups, others, false, "zzz"), "");
        Rows("spaces alone are no search", GroupTree.Rows([home], [], false, "   "), "0:Home | 1:Outlook — Inbox");
        Rows("letters beyond English", GroupTree.Rows([new(20, "Økonomi", false, [])], [], false, "øko"), "0:Økonomi");
        var found = GroupTree.Rows(groups, others, false, "edge")[0];
        Is("a search counts what it found", found.Count == 1);

        // --- Where Enter goes ---
        int Best(string q) => GroupTree.BestRow(GroupTree.Rows(groups, others, false, q), q);
        expect("tree: best: the group, for its name", Best("work") == 0, $"{Best("work")}");
        expect("tree: best: the app, not its group", Best("edge") == 1, $"{Best("edge")}");
        expect("tree: best: words across both, the app", Best("work code") == 1, $"{Best("work code")}");
        expect("tree: best: nothing found", Best("zzz") == -1, $"{Best("zzz")}");

        // --- Icons ---
        byte[] alpha = [10, 20, 30, 40, 0, 0, 0, 0];
        expect("icon: one with alpha is used as it is", IconPixels.Finish(alpha, [0, 0, 0, 0, 0, 0, 0, 0]) == alpha, "");
        var old = IconPixels.Finish([10, 20, 30, 0, 40, 50, 60, 0], [0, 0, 0, 0, 255, 255, 255, 0]);
        expect("icon: an old one is opaque where its mask is black, clear elsewhere", old != null && old.SequenceEqual(new byte[] { 10, 20, 30, 255, 0, 0, 0, 0 }), "");
        expect("icon: nothing showing is no icon", IconPixels.Finish([0, 0, 0, 0], [255, 255, 255, 0]) == null, "");
        expect("icon: no alpha and no mask is no icon", IconPixels.Finish([1, 2, 3, 0], null) == null, "");

        // --- App names ---
        expect("tree: a title the same as the app's name isn't repeated", GroupTree.AppText("Terminal", "Terminal") == "Terminal", GroupTree.AppText("Terminal", "Terminal"));
        expect("tree: no title, just the name", GroupTree.AppText("Notepad", "") == "Notepad", GroupTree.AppText("Notepad", ""));

        // --- Where things are ---
        var view = new Vec(1920, 1080);
        var panel = GroupTree.Panel(view, 1);
        Is("panel: on the right, with its margin", panel.Right == 1920 - GroupTree.Margin && panel.W == GroupTree.Width);
        Is("panel: stops above the minimap", panel.Bottom == 1080 - GroupTree.Margin - GroupTree.BottomReserve);
        Is("panel: at the top", panel.Y == GroupTree.Margin);
        Is("panel: follows the display's scale", GroupTree.Panel(view, 2).W == 2 * GroupTree.Width);
        var search = GroupTree.SearchRect(panel, 1);
        Is("search: at the top of the panel", panel.Contains(search) && search.Y - panel.Y < 20);
        var list = GroupTree.ListRect(panel, 1);
        Is("list: below the search", list.Y >= search.Bottom && list.Bottom <= panel.Bottom && list.W > 0);

        var area = new RectD(0, 100, 200, 280); // 10 rows of 28
        expect("hit: the first row", GroupTree.RowAt(new(5, 101), area, 28, 0, 20) == 0, "");
        expect("hit: further down", GroupTree.RowAt(new(5, 100 + 28 * 3 + 1), area, 28, 0, 20) == 3, "");
        expect("hit: scrolled", GroupTree.RowAt(new(5, 101), area, 28, 56, 20) == 2, "");
        expect("hit: past the last row", GroupTree.RowAt(new(5, 100 + 28 * 2 + 1), area, 28, 0, 2) == -1, "");
        expect("hit: outside the list", GroupTree.RowAt(new(5, 99), area, 28, 56, 20) == -1, "");
        var rowRect = GroupTree.RowRect(area, 28, 56, 2);
        Is("row: where it's drawn", rowRect == new RectD(0, 100, 200, 28));

        expect("scroll: not above the top", GroupTree.ClampScroll(-50, 20, 28, 280) == 0, "");
        expect("scroll: not past the end", GroupTree.ClampScroll(9999, 20, 28, 280) == 280, "");
        expect("scroll: short lists don't scroll", GroupTree.ClampScroll(50, 3, 28, 280) == 0, "");
        expect("show: already in view", GroupTree.ScrollToShow(2, 0, 28, 280) == 0, "");
        expect("show: below brings it up", GroupTree.ScrollToShow(15, 0, 28, 280) == 16 * 28 - 280, "");
        expect("show: above brings it down", GroupTree.ScrollToShow(1, 200, 28, 280) == 28, "");

        expect("expander: on the arrow", GroupTree.OnExpander(new(10, 110), new RectD(0, 100, 200, 28), 0, 1), "");
        expect("expander: not on the name", !GroupTree.OnExpander(new(100, 110), new RectD(0, 100, 200, 28), 0, 1), "");
        expect("highlight: down", GroupTree.MoveHighlight(0, 5, 1) == 1, "");
        expect("highlight: from nothing, the first", GroupTree.MoveHighlight(-1, 5, 1) == 0, "");
        expect("highlight: stays at the ends", GroupTree.MoveHighlight(4, 5, 1) == 4 && GroupTree.MoveHighlight(0, 5, -1) == 0, "");
        expect("highlight: an empty list has none", GroupTree.MoveHighlight(0, 0, 1) == -1, "");
    }
}
