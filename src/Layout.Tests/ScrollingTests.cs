using Expanse;

/// What wheel turns and touchpad gestures do, and when they bring the canvas up (Scrolling, ScrollStart).
static class ScrollingTests
{
    public static void Run(Action<string, bool, string> expect)
    {
        void Same<T>(string name, T want, T got) => expect($"scrolling: {name}", Equals(want, got), $"want {want}, got {got}");

        Same("plain zooms", (ScrollDoes.Zoom, -120), Scrolling.Of(false, -120, ctrl: false, shift: false));
        Same("a pinch (Ctrl) zooms", (ScrollDoes.Zoom, -7), Scrolling.Of(false, -7, ctrl: true, shift: false));
        Same("Shift moves sideways", (ScrollDoes.PanX, 120), Scrolling.Of(false, 120, ctrl: false, shift: true));
        Same("Ctrl + Shift moves up and down", (ScrollDoes.PanY, 120), Scrolling.Of(false, 120, ctrl: true, shift: true));
        Same("a sideways scroll moves the other way", (ScrollDoes.PanX, -120), Scrolling.Of(true, 120, ctrl: false, shift: false));

        Same("a notch out opens", -120, new ScrollStart().Add(ScrollDoes.Zoom, -120, 1000));
        Same("a notch in doesn't", null, new ScrollStart().Add(ScrollDoes.Zoom, 120, 1000));
        Same("a notch sideways opens", 120, new ScrollStart().Add(ScrollDoes.PanX, 120, 1000));

        var s = new ScrollStart();
        Same("a touchpad's first bit doesn't", null, s.Add(ScrollDoes.Zoom, -12, 1000));
        Same("nor its second", null, s.Add(ScrollDoes.Zoom, -12, 1010));
        Same("then it does, with all of it", -36, s.Add(ScrollDoes.Zoom, -12, 1020));
        Same("and starts over", null, s.Add(ScrollDoes.Zoom, -12, 1030));

        s = new ScrollStart();
        s.Add(ScrollDoes.Zoom, -20, 1000);
        Same("a pause starts it over", null, s.Add(ScrollDoes.Zoom, -20, 1400));

        s = new ScrollStart();
        s.Add(ScrollDoes.Zoom, 200, 1000);
        Same("turning round starts it over", null, s.Add(ScrollDoes.Zoom, -20, 1010));
        Same("and gets there", -40, s.Add(ScrollDoes.Zoom, -20, 1020));

        s = new ScrollStart();
        s.Add(ScrollDoes.Zoom, 100, 1000);
        Same("drifting sideways while scrolling up doesn't open", null, s.Add(ScrollDoes.PanX, 70, 1010));
        Same("unless it's most of it", 110, s.Add(ScrollDoes.PanX, 40, 1020));
    }
}
