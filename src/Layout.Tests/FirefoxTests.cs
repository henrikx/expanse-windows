using System.Text;
using System.Text.Json;
using Expanse;

/// Reading Firefox's session file, and telling its windows apart (FirefoxSession, FirefoxWindows).
static class FirefoxTests
{
    public static void Run(Action<string, bool, string> expect)
    {
        void Is(string name, bool ok, string detail = "") => expect($"firefox: {name}", ok, detail);
        bool Throws(Action a) { try { a(); return false; } catch (Exception e) when (e is InvalidDataException or JsonException) { return true; } }

        // LZ4
        byte[] Unpack(byte[] block, int size) { var to = new byte[size]; int n = FirefoxSession.Lz4(block, to); return to[..n]; }
        string Text(byte[] block, int size) => Encoding.ASCII.GetString(Unpack(block, size));
        Is("literals only", Text([0x50, .. "hello"u8], 5) == "hello");
        Is("a match that runs into itself", Text([0x21, .. "ab"u8, 2, 0, 0x10, .. "!"u8], 8) == "abababa!");
        var many = Enumerable.Repeat((byte)'x', 300).ToArray();
        Is("a long run of literals", Unpack([0xF0, 255, 30, .. many], 300).SequenceEqual(many));
        Is("a long match", Text([0x1F, (byte)'a', 1, 0, 255, 0, 0x00], 275) == new string('a', 275));
        Is("nothing", Unpack([], 5).Length == 0);
        Is("literals past the input", Throws(() => Unpack([0x50, (byte)'a'], 5)));
        Is("literals past the output", Throws(() => Unpack([0x50, .. "hello"u8], 4)));
        Is("a match past the output", Throws(() => Unpack([0x1F, (byte)'a', 1, 0, 10, 0x00], 8)));
        Is("a match from before the start", Throws(() => Unpack([0x10, (byte)'a', 2, 0, 0x00], 20)));
        Is("a match from nowhere", Throws(() => Unpack([0x10, (byte)'a', 0, 0, 0x00], 20)));
        Is("cut off in a match", Throws(() => Unpack([0x10, (byte)'a', 1], 20)));
        Is("cut off in a length", Throws(() => Unpack([0xF0, 255], 999)));

        // The JSON
        const string json = """
            {"version":["sessionrestore",1],"_closedWindows":[{"tabs":[{"entries":[{"url":"https://closed/"}]}]}],
             "windows":[
              {"tabs":[
                {"entries":[{"url":"https://a/1","title":"A one","children":[{"url":"https://frame/","title":"Frame"}]},{"url":"https://a/2#here","title":"A two"}],"index":1,"extData":{"index":9}},
                {"entries":[{"url":"https://b/","title":"B"},{"url":"https://b/2#x","title":"B two"}],"index":2},
                {"entries":[{"url":"about:newtab","title":"New Tab"}],"index":1},
                {"entries":[]}],
               "selected":2,"_closedTabs":[{"state":{"entries":[{"url":"https://gone/"}]}}],"screenX":-8,"screenY":12.5,"title":"Other"},
              {"tabs":[{"entries":[{"url":"https://c/"}]}],"selected":"2","screenX":null},
              {"selected":4,"tabs":[{"entries":[{"url":"https://d/","title":"D"}],"index":7}]},
              7],
             "selectedWindow":1}
            """;
        var w = FirefoxSession.Parse(Encoding.UTF8.GetBytes(json), u => $"<{u}>");
        Is("three windows, not the closed one", w.Count == 3, $"{w.Count}");
        Is("the page each tab is on, without the fragment", w[0].Marks.SequenceEqual(["<https://a/1>", "<https://b/2>"]), string.Join(" ", w[0].Marks));
        Is("the title of the selected tab", w[0].Title == "B two", w[0].Title);
        Is("the position", w[0].X == -8 && w[0].Y == 12.5);
        Is("no title: the address", w[1].Title == "https://c/", w[1].Title);
        Is("no position", double.IsNaN(w[1].X) && double.IsNaN(w[1].Y));
        Is("a selected tab that isn't there", w[2].Title == "" && w[2].Marks.SequenceEqual(["<https://d/>"]));
        var odd = FirefoxSession.Parse(Encoding.UTF8.GetBytes(@"{""windows"":[{""tabs"":[{""entries"":[{""url"":""https://e/"",""title"":""\ud83d""}]}]}]}"), u => u);
        Is("a title that isn't text", odd is [{ Title: "https://e/", Marks: ["https://e/"] }]);
        Is("no windows", FirefoxSession.Parse("{}"u8, u => u).Count == 0);
        Is("cut off", Throws(() => FirefoxSession.Parse("{\"windows\":[{\"tabs\":[{"u8, u => u)));
        Is("not an object", Throws(() => FirefoxSession.Parse("[]"u8, u => u)));

        // The file
        byte[] File(int said) => [.. "mozLz40\0"u8, .. BitConverter.GetBytes(said), 0xE0, .. "{\"windows\":[]}"u8];
        Is("a whole file", FirefoxSession.Read(File(14), u => u).Count == 0);
        Is("shorter than it says", Throws(() => FirefoxSession.Read(File(15), u => u)));
        Is("longer than it says", Throws(() => FirefoxSession.Read(File(13), u => u)));
        Is("absurdly big", Throws(() => FirefoxSession.Read(File(int.MaxValue), u => u)));
        Is("another file", Throws(() => FirefoxSession.Read("something else"u8, u => u)));
        Is("an empty file", Throws(() => FirefoxSession.Read([], u => u)));

        // Which window is which
        FirefoxSeen Seen(long h, string title, double x = 0, double y = 0, params string[] marks) =>
            new(h, title.Length == 0 ? "Mozilla Firefox" : $"{title} — Mozilla Firefox", x, y, marks.Length == 0 ? null : marks);
        FirefoxWindow Win(string title, double x = 0, double y = 0, params string[] marks) => new(title, marks, x, y);
        string Who(FirefoxSeen[] seen, params FirefoxWindow[] session) =>
            string.Join(" ", FirefoxWindows.Assign(seen, session).OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value}"));
        void Pairs(string name, string want, FirefoxSeen[] seen, params FirefoxWindow[] session)
        {
            string got = Who(seen, session);
            Is(name, got == want, $"want '{want}', got '{got}'");
        }
        Pairs("by title, whatever the order", "1:1 2:0", [Seen(1, "A"), Seen(2, "B")], Win("B"), Win("A"));
        Pairs("not while still empty", "", [Seen(1, ""), Seen(2, "")], Win("A"), Win("B"));
        Pairs("a profile's name in between", "1:0", [Seen(1, "A — Work")], Win("A"));
        Pairs("the longest title that fits", "1:1", [Seen(1, "A — B")], Win("A"), Win("A — B"));
        Pairs("not part of a word", "", [Seen(1, "Apple")], Win("App"));
        Pairs("spaces don't matter", "1:0", [Seen(1, "A  B")], Win(" A B\n"));
        Pairs("a number that changed", "1:0", [Seen(1, "(4) Inbox")], Win("(3) Inbox"));
        Pairs("a number that came", "1:0", [Seen(1, "(1) Inbox")], Win("Inbox"));
        Pairs("the exact number first", "1:1 2:0", [Seen(1, "Page 2"), Seen(2, "Page 1")], Win("Page 1"), Win("Page 2"));
        Pairs("a title of only numbers isn't loosened away", "", [Seen(1, "12")], Win("34"));
        Pairs("a page with no title fits nothing", "", [Seen(1, "A")], Win(""));
        Pairs("same titles: can't tell", "", [Seen(1, "New Tab", 500), Seen(2, "New Tab", 900)], Win("New Tab"), Win("New Tab", 50));
        Pairs("same titles: by position", "1:1 2:0", [Seen(1, "T", 500, 10), Seen(2, "T", 900, 10)], Win("T", 905, 10), Win("T", 500, 5));
        Pairs("the one left over", "1:1 2:0", [Seen(1, "T", 500), Seen(2, "T", 9999)], Win("T", 900), Win("T", 500));
        Pairs("two on one window", "", [Seen(1, "T", 500), Seen(2, "T", 700)], Win("T", 900));
        Pairs("known by its tabs, its title changed", "1:1", [Seen(1, "Z", 0, 0, "a", "b")], Win("A", 0, 0, "x"), Win("B", 0, 0, "a", "b", "c"));
        Pairs("its tabs before another's title", "1:0 2:1", [Seen(1, "B", 0, 0, "a"), Seen(2, "Z", 0, 0, "b")], Win("A", 0, 0, "a"), Win("B", 0, 0, "b"));
        Pairs("the most alike first", "1:0 2:1", [Seen(1, "Z", 0, 0, "a", "b"), Seen(2, "Z", 0, 0, "a", "b", "c")], Win("X", 0, 0, "a", "b"), Win("Y", 0, 0, "a", "b", "c"));
        Pairs("the same tabs: by title", "1:1 2:0", [Seen(1, "B", 0, 0, "a"), Seen(2, "A", 0, 0, "a")], Win("A", 0, 0, "a"), Win("B", 0, 0, "a"));
        Pairs("all its tabs changed: by title", "1:0", [Seen(1, "A", 0, 0, "old")], Win("A", 0, 0, "new"));
        Pairs("a new window doesn't take a known one's", "2:0", [Seen(1, "A"), Seen(2, "A", 0, 0, "a")], Win("A", 0, 0, "a"));
        Pairs("nothing in the session", "", [Seen(1, "A")]);
        Is("has a page's title", FirefoxWindows.HasPage("A — Mozilla Firefox") && !FirefoxWindows.HasPage("Mozilla Firefox"));
    }
}
