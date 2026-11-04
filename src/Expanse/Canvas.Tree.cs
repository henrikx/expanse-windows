using Expanse.Native;
using static Expanse.Native.User32;

namespace Expanse;

/// The groups panel, zoomed out at the right: a search bar over a tree of the groups and their
/// apps (the rules are in GroupTree). A row goes to its group or app, as a click on the canvas would.
sealed partial class Canvas
{
    const int VK_F = 0x46, VK_G = 0x47, VK_TAB_KEY = 0x09, VK_ESC_KEY = 0x1B;
    readonly SearchBox searchBox = new();
    string treeQuery = "";
    double treeScroll;
    int treeHover = -1, treeHighlight = -1;
    bool othersCollapsed;
    readonly AppIcons appIcons = new();

    RectD TreePanel => settings.GroupsPanel ? GroupTree.Panel(ViewSize, UiScale) : default;

    bool TreeHasSearch => treeQuery.Trim().Length > 0;

    void WireSearchBox()
    {
        searchBox.Changed = text =>
        {
            treeQuery = text;
            treeScroll = 0;
            treeHighlight = TreeHasSearch ? GroupTree.BestRow(TreeRows(), treeQuery) : -1;
            Redraw();
        };
        searchBox.Key = SearchKey;
        searchBox.Left = () => { if (active) Redraw(); };
    }

    List<TreeRow> TreeRows()
    {
        static TreeApp App(CanvasWindow w) => new(w.Hwnd, w.AppName, w.Title);
        var inGroups = groups.Select(g => new TreeGroup(g.Id, g.Name, g.Collapsed, MembersOf(g).Select(App).ToList())).ToList();
        return GroupTree.Rows(inGroups, windows.Where(w => !InAnyGroup(w)).Select(App).ToList(), othersCollapsed, treeQuery);
    }

    CanvasGroup? GroupOf(TreeRow row) => row.Kind == TreeRowKind.Group ? groups.FirstOrDefault(g => g.Id == row.Key) : null;

    CanvasWindow? WindowOf(TreeRow row) => row.Kind == TreeRowKind.App ? windows.FirstOrDefault(w => w.Hwnd == row.Key) : null;

    // MARK: The mouse and keyboard

    void PressTree(Vec p)
    {
        double k = UiScale;
        var panel = TreePanel;
        if (GroupTree.SearchRect(panel, k).Contains(p)) { OpenSearch(); return; }
        var list = GroupTree.ListRect(panel, k);
        var rows = TreeRows();
        double rowHeight = GroupTree.RowHeight * k;
        int i = GroupTree.RowAt(p, list, rowHeight, treeScroll, rows.Count);
        if (i < 0) return;
        var row = rows[i];
        if (row.Expandable && !TreeHasSearch && GroupTree.OnExpander(p, GroupTree.RowRect(list, rowHeight, treeScroll, i), row.Depth, k))
        {
            ToggleRow(row);
            return;
        }
        ActivateRow(row);
    }

    void ToggleRow(TreeRow row)
    {
        if (GroupOf(row) is CanvasGroup g) g.Collapsed = !g.Collapsed;
        else if (row.Kind == TreeRowKind.Others) othersCollapsed = !othersCollapsed;
        ScheduleSave();
        Redraw();
    }

    /// Goes to the row's group or app; the rest of the windows' section just opens or closes.
    void ActivateRow(TreeRow row)
    {
        if (row.Kind == TreeRowKind.Others) { ToggleRow(row); return; }
        searchBox.Close();
        selected.Clear();
        if (GroupOf(row) is CanvasGroup g) FocusGroup(g);
        else if (WindowOf(row) is CanvasWindow w) Exit(w);
    }

    void OpenSearch()
    {
        if (!settings.GroupsPanel) ToggleTree();
        double k = UiScale;
        var at = GroupTree.SearchTextRect(GroupTree.SearchRect(TreePanel, k), k);
        searchBox.Open(overlay.Hwnd, (int)Math.Round(at.X) + monitor.Left, (int)Math.Round(at.Y) + monitor.Top,
            (int)Math.Round(at.W), (int)Math.Round(at.H), treeQuery, k);
        Redraw(); // the box shows what's under it, so the panel stops drawing the text
    }

    /// A character typed on the canvas that no shortcut took starts a search with it.
    void TypeIntoSearch(char c)
    {
        if (!searchBox.IsOpen) OpenSearch();
        searchBox.Type(c);
    }

    /// Esc clears the search, then leaves it; Tab leaves it; Enter goes to the highlighted row.
    void SearchKey(int key)
    {
        var rows = TreeRows();
        switch (key)
        {
            case VK_RETURN:
                int i = treeHighlight >= 0 && treeHighlight < rows.Count ? treeHighlight : GroupTree.BestRow(rows, treeQuery);
                if (i >= 0) ActivateRow(rows[i]);
                break;
            case VK_ESC_KEY when treeQuery.Length > 0:
                searchBox.SetText("");
                break;
            case VK_ESC_KEY or VK_TAB_KEY:
                searchBox.Close();
                Focus(overlay.Hwnd);
                Redraw();
                break;
            case VK_UP or VK_DOWN:
                treeHighlight = GroupTree.MoveHighlight(treeHighlight, rows.Count, key == VK_DOWN ? 1 : -1);
                if (treeHighlight >= 0)
                {
                    double k = UiScale;
                    treeScroll = GroupTree.ScrollToShow(treeHighlight, treeScroll, GroupTree.RowHeight * k, GroupTree.ListRect(TreePanel, k).H);
                }
                Redraw();
                break;
        }
    }

    void ToggleTree()
    {
        settings.GroupsPanel = !settings.GroupsPanel;
        settings.Save();
        if (!settings.GroupsPanel) searchBox.Close();
        treeHover = -1;
        Log.Info($"groups panel {(settings.GroupsPanel ? "shown" : "hidden")}");
        Redraw();
    }

    void HoverTree(Vec p)
    {
        double k = UiScale;
        var rows = TreeRows();
        int i = GroupTree.RowAt(p, GroupTree.ListRect(TreePanel, k), GroupTree.RowHeight * k, treeScroll, rows.Count);
        // The row's window or group lights up on the canvas too.
        var (w, g) = i >= 0 ? (WindowOf(rows[i]), GroupOf(rows[i])) : (null, null);
        if (i == treeHover && w == hovered && g == hoveredGroup) return;
        treeHover = i;
        hovered = w;
        hoveredGroup = g;
        Redraw();
    }

    void LeaveTree()
    {
        if (treeHover < 0) return;
        treeHover = -1;
        Redraw();
    }

    void ScrollTree(int delta)
    {
        double k = UiScale;
        treeScroll -= delta * 0.5 * k;
        treeScroll = GroupTree.ClampScroll(treeScroll, TreeRows().Count, GroupTree.RowHeight * k, GroupTree.ListRect(TreePanel, k).H);
        Redraw();
    }

    /// Everything about the panel that lasts only while the canvas is up.
    void ResetTree()
    {
        searchBox.Close();
        appIcons.Clear();
        treeQuery = "";
        treeHighlight = treeHover = -1;
    }

    // MARK: Drawing

    TreeDrawing TreeDrawingNow(double k, bool dark)
    {
        var panel = TreePanel;
        var list = GroupTree.ListRect(panel, k);
        var rows = TreeRows();
        double rowHeight = GroupTree.RowHeight * k;
        treeScroll = GroupTree.ClampScroll(treeScroll, rows.Count, rowHeight, list.H);
        var shown = new List<TreeRowDrawing>();
        int iconSize = (int)Math.Round(GroupTree.IconSize * k);
        for (int i = 0; i < rows.Count; i++)
        {
            var r = GroupTree.RowRect(list, rowHeight, treeScroll, i);
            if (!r.Intersects(list)) continue;
            var row = rows[i];
            var icon = WindowOf(row) is CanvasWindow w ? appIcons.Get(w, iconSize) : null;
            shown.Add(new TreeRowDrawing(r, row.Text, row.Depth, row.Kind != TreeRowKind.App, row.Expandable, row.Expanded,
                row.Kind == TreeRowKind.App ? "" : row.Count.ToString(), i == treeHover || i == treeHighlight, icon));
        }
        string empty = rows.Count > 0 ? "" : TreeHasSearch ? "No matches" : "Nothing on the canvas yet";
        return new TreeDrawing(panel, GroupTree.SearchRect(panel, k), list, treeQuery, shown, empty, dark, searchBox.IsOpen);
    }

    /// With DWM thumbnails, plainly on the backdrop (a thumbnail can cover it).
    void PaintTree(nint hdc, double k)
    {
        if (!settings.GroupsPanel) return;
        var d = TreeDrawingNow(k, true);
        RECT R(RectD r) => new((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Right), (int)Math.Round(r.Bottom));
        nint back = Gdi32.CreateSolidBrush(Gdi32.RGB(31, 31, 33));
        nint field = Gdi32.CreateSolidBrush(Gdi32.RGB(55, 55, 58));
        var panel = R(d.Panel);
        FillRect(hdc, ref panel, back);
        var search = R(d.Search);
        FillRect(hdc, ref search, field);
        foreach (var row in d.Rows)
        {
            var box = R(row.Rect);
            if (row.Highlighted) FillRect(hdc, ref box, field);
            box.Left += (int)((row.Depth * GroupTree.Indent + 24) * k);
            DrawText(hdc, row.Text, -1, ref box, DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS);
        }
        var text = R(GroupTree.SearchTextRect(d.Search, k));
        if (!d.Typing) DrawText(hdc, d.Query.Length > 0 ? d.Query : TreeDrawing.Placeholder, -1, ref text, DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS);
        Gdi32.DeleteObject(back);
        Gdi32.DeleteObject(field);
    }
}
