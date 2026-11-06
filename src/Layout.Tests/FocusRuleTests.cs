using Expanse;

/// What focus on a hidden window does (FocusRules).
static class FocusRuleTests
{
    public static void Run(Action<string, bool, string> expect)
    {
        void Is(string name, bool ok, string detail = "") => expect($"focus: {name}", ok, detail);
        void Does(string name, FocusAction want, bool autoCentre, bool hidden, bool gone, bool held, bool moved)
        {
            var got = FocusRules.Decide(autoCentre, hidden, gone, held, moved);
            Is(name, got == want, $"want {want}, got {got}");
        }

        Does("nothing special: glide", FocusAction.Glide, true, true, false, false, false);
        Does("auto-centre off: none", FocusAction.None, false, true, false, false, false);
        Does("not hidden enough: none", FocusAction.None, true, false, false, false, false);
        Does("auto-centre off beats everything", FocusAction.None, false, true, true, true, true);
        Does("not hidden enough beats everything", FocusAction.None, true, false, true, true, true);
        Does("a window went away: stay", FocusAction.StayAfterWindowWentAway, true, true, true, false, false);
        Does("a window went away beats the button", FocusAction.StayAfterWindowWentAway, true, true, true, true, false);
        Does("a window went away beats moved by hand", FocusAction.StayAfterWindowWentAway, true, true, true, false, true);
        Does("a window went away beats button and moved", FocusAction.StayAfterWindowWentAway, true, true, true, true, true);
        Does("button held: wait", FocusAction.WaitForRelease, true, true, false, true, false);
        Does("button held beats moved by hand", FocusAction.WaitForRelease, true, true, false, true, true);
        Does("moved by hand: stay", FocusAction.StayMovedByHand, true, true, false, false, true);

        Is("went away at 1499 ms", FocusRules.WindowJustWentAway(11_499, 10_000, false));
        Is("not at 1500 ms", !FocusRules.WindowJustWentAway(11_500, 10_000, false));
        Is("not long after", !FocusRules.WindowJustWentAway(20_000, 10_000, false));
        Is("not with input since the grace", !FocusRules.WindowJustWentAway(10_500, 10_000, true));
        Is("just gone", FocusRules.WindowJustWentAway(10_000, 10_000, false));
        Is("the grace is 300 ms, the window 1500", FocusRules.InputGrace == 300 && FocusRules.WentAwayWindow == 1500);

        var before = new RectD(100, 100, 800, 600);
        bool Moved(bool same, long started, long changed, RectD now) => FocusRules.MovedByHand(same, started, changed, before, now);
        Is("moved: same window, started at focus", Moved(true, 5000, 5000, before with { X = 110 }));
        Is("another window: not moved", !Moved(false, 5000, 5000, before with { X = 110 }));
        Is("move 500 ms before focus: still counts", Moved(true, 4500, 5000, before with { X = 110 }));
        Is("move 501 ms before focus: too old", !Moved(true, 4499, 5000, before with { X = 110 }));
        Is("move after focus counts", Moved(true, 5200, 5000, before with { X = 110 }));
        Is("not moved", !Moved(true, 5000, 5000, before));
        Is("left edge moved", Moved(true, 5000, 5000, before with { X = 101, W = 799 }));
        Is("top edge moved", Moved(true, 5000, 5000, before with { Y = 101, H = 599 }));
        Is("right edge moved (resized)", Moved(true, 5000, 5000, before with { W = 801 }));
        Is("bottom edge moved (resized)", Moved(true, 5000, 5000, before with { H = 601 }));
        Is("moved without resizing", Moved(true, 5000, 5000, before with { X = 150, Y = 150 }));
    }
}
