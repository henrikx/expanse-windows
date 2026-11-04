using Expanse;

static class PerDesktopTests
{
    public static void Run(Action<string, bool, string> expect)
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        Guid[] all = [a, b, c];
        void Is(string name, List<string> got, params string[] want) =>
            expect($"per desktop: {name}", got.SequenceEqual(want), $"want [{string.Join(", ", want)}], got [{string.Join(", ", got)}]");
        void Flag(string name, bool got, bool want) => expect($"per desktop: {name}", got == want, $"want {want}");

        // Before the desktop is known, what's in the list stays, and is the first known desktop's.
        var d = new PerDesktop<string>();
        var items = new List<string> { "one" };
        Flag("unknown desktop: no change", d.Follow(items, all, null), false);
        Is("unknown desktop: the list stays", items, "one");
        Flag("first known desktop: no change", d.Follow(items, all, a), false);
        Is("first known desktop: it's that desktop's", items, "one");
        Flag("first known desktop: remembered", d.Here == a, true);

        Flag("same desktop: no change", d.Follow(items, all, a), false);
        Flag("to an empty desktop: changed", d.Follow(items, all, b), true);
        Is("to an empty desktop: nothing here", items);
        Flag("to an empty desktop: the others are kept", d.Away.SequenceEqual([(a, "one")]), true);

        items.Add("two");
        Flag("unknown again: no change", d.Follow(items, all, null), false);
        Is("unknown again: the list stays", items, "two");
        Flag("back: changed", d.Follow(items, all, a), true);
        Is("back: its own again", items, "one");
        Flag("back: the other's kept", d.Away.SequenceEqual([(b, "two")]), true);
        Flag("between empty desktops: no change", new PerDesktop<string>().Follow(new(), all, c), false);

        // A desktop removed: what was on it comes to the one you're on.
        Flag("a desktop gone: changed", d.Follow(items, [a, c], a), true);
        Is("a desktop gone: its things come here", items, "one", "two");
        Flag("a desktop gone: nothing left of it", !d.Away.Any(), true);

        // The one you were on removed, as you land on another.
        var e = new PerDesktop<string>();
        var here = new List<string> { "x" };
        e.Follow(here, all, a);
        e.Put(b, "y");
        Flag("the desktop you were on gone: changed", e.Follow(here, [b, c], b), true);
        Is("the desktop you were on gone: both here", here, "y", "x");

        e.Reset(c);
        Flag("reset: nothing kept", !e.Away.Any() && e.Here == c, true);
    }
}
