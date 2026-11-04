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
    }
}
