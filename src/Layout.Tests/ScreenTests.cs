using Expanse;

/// Which screen a window counts as on (Screens).
static class ScreenTests
{
    public static void Run(Action<string, bool, string> expect)
    {
        var main = new RectD(0, 0, 1920, 1080);
        RectD[] left = [new(-1440, 0, 1440, 900)];
        void Is(string name, RectD f, bool want) => expect($"screens: {name}", Screens.IsElsewhere(f, main, left) == want, $"want {want} for {f}");

        Is("on the main screen", new(100, 100, 800, 600), false);
        Is("wholly on the other monitor", new(-1000, 100, 800, 600), true);
        Is("centre on the main screen, reaching across", new(-300, 100, 800, 600), false);
        Is("centre on the other monitor, reaching back", new(-700, 100, 800, 600), true);
        Is("too wide to fit, centre on the other monitor", new(-1400, 100, 1829, 600), true);
        Is("too wide to fit, centre on the main screen", new(-600, 100, 1829, 600), false);
        Is("centre just over the edge, on the other side", new(-402, 100, 800, 600), true);
        Is("centre exactly on the main screen's edge", new(-400, 100, 800, 600), false);
        Is("off every screen", new(-9000, 100, 800, 600), false);
        Is("past the main screen, on no monitor", new(3000, 100, 800, 600), false);
        Is("below the other monitor, reaching onto it: centre on neither", new(-1000, 800, 400, 300), true);
        Is("reaching the main screen with its centre on neither", new(-300, 880, 800, 300), false);
        expect("screens: no other monitors, nothing is elsewhere", !Screens.IsElsewhere(new(-9000, 0, 100, 100), main, []), "");
        expect("screens: centre on neither isn't a centre elsewhere", !Screens.CentreElsewhere(new(-1000, 800, 400, 300), main, left), "");

        void Hidden(string name, RectD f, int percent, bool want) =>
            expect($"hidden: {name}", Layout.HiddenEnough(f, main, percent) == want, $"want {want} at {percent}% for {f}: {Layout.HiddenShare(f, main):P2}");

        Hidden("wholly on screen isn't hidden", new(100, 100, 800, 600), 1, false);
        Hidden("exactly the percentage counts", new(-240, 100, 800, 600), 30, true);
        Hidden("just under the percentage doesn't", new(-239, 100, 800, 600), 30, false);
        Hidden("the default takes a quarter", new(-200, 100, 800, 600), 25, true);
        Hidden("the default leaves a fifth", new(-160, 100, 800, 600), 25, false);
        Hidden("off past a corner goes by area: 30% each way is 51%", new(-240, -180, 800, 600), 51, true);
        Hidden("off past a corner, not more than the area", new(-240, -180, 800, 600), 52, false);
        Hidden("100% takes a window wholly off screen", new(3000, 100, 800, 600), 100, true);
        Hidden("100% leaves a window with a sliver showing", new(1919, 100, 800, 600), 100, false);
        Hidden("too big, centred, showing all it can", new(-100, -100, 2120, 1280), 1, false);
        Hidden("too wide, showing all it can", new(-100, 100, 2120, 600), 1, false);
        Hidden("too big and off to one side counts what could fit", new(960, 0, 2120, 1280), 50, true);
        Hidden("too big, off to one side, less than the percentage", new(960, 0, 2120, 1280), 51, false);
        Hidden("a percentage below 1 is 1", new(100, 100, 800, 600), 0, false);
        expect("hidden: a window with no size has nothing hidden", Layout.HiddenShare(new(3000, 100, 0, 0), main) == 0, "");

        void Nudged(string name, RectD f, double x, double y)
        {
            var d = Layout.Nudge(f, main, 32);
            expect($"nudge: {name}", d.X == x && d.Y == y, $"want {x},{y} for {f}, got {d.X},{d.Y}");
            expect($"nudge: {name}, leaves nothing hidden", Layout.HiddenShare(f.Offset(d), main) == 0, $"{f.Offset(d)}");
            expect($"nudge: {name}, then stays", Layout.Nudge(f.Offset(d), main, 32) == new Vec(0, 0), $"{f.Offset(d)}");
        }

        Nudged("inside with room stays", new(100, 100, 800, 600), 0, 0);
        Nudged("off the left, to the padding", new(-100, 100, 800, 600), 132, 0);
        Nudged("off the right, to the padding", new(1500, 100, 800, 600), -412, 0);
        Nudged("off past a corner moves both ways", new(-100, -100, 800, 600), 132, 132);
        Nudged("wholly off screen comes just inside", new(3000, 2000, 800, 600), -1912, -1552);
        Nudged("on screen but inside the padding", new(10, 100, 800, 600), 22, 0);
        Nudged("no room for the padding gets even margins", new(-50, 100, 1900, 600), 60, 0);
        Nudged("exactly the screen's width", new(-50, 100, 1920, 600), 50, 0);
        Nudged("too wide, covering the screen", new(-100, 100, 2120, 600), 0, 0);
        Nudged("too wide, off to the right", new(100, 100, 2120, 600), -100, 0);
        Nudged("too wide, off to the left", new(-500, 100, 2120, 600), 300, 0);
        Nudged("too tall, covering the screen", new(100, -100, 800, 1280), 0, 0);
        Nudged("too tall, lower down, to its top", new(100, 100, 800, 1280), 0, -100);
        Nudged("too tall, higher up, to its top too", new(100, -500, 800, 1280), 0, 500);
    }
}
