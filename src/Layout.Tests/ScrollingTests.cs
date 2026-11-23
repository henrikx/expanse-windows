using Expanse;

/// What wheel turns and touchpad gestures do, and when they bring the canvas up (Scrolling, ScrollStart).
static class ScrollingTests
{
    public static void Run(Action<string, bool, string> expect)
    {
        void Same<T>(string name, T want, T got) => expect($"scrolling: {name}", Equals(want, got), $"want {want}, got {got}");

        Same("plain zooms", (ScrollDoes.Zoom, -120), Scrolling.Of(false, -120, ctrl: false, shift: false, touchpad: false));
        Same("a pinch (Ctrl) zooms", (ScrollDoes.Zoom, -7), Scrolling.Of(false, -7, ctrl: true, shift: false, touchpad: false));
        Same("Shift moves sideways", (ScrollDoes.PanX, 120), Scrolling.Of(false, 120, ctrl: false, shift: true, touchpad: false));
        Same("Ctrl + Shift moves up and down", (ScrollDoes.PanY, 120), Scrolling.Of(false, 120, ctrl: true, shift: true, touchpad: false));
        Same("a sideways scroll moves the other way", (ScrollDoes.PanX, -120), Scrolling.Of(true, 120, ctrl: false, shift: false, touchpad: false));

        Same("a touchpad's scroll moves up and down", (ScrollDoes.PanY, -120), Scrolling.Of(false, -120, ctrl: false, shift: false, touchpad: true));
        Same("its pinch zooms", (ScrollDoes.Zoom, -120), Scrolling.Of(false, -120, ctrl: true, shift: false, touchpad: true));
        Same("sideways, sideways", (ScrollDoes.PanX, -120), Scrolling.Of(true, 120, ctrl: false, shift: false, touchpad: true));
        Same("with Shift, sideways", (ScrollDoes.PanX, 120), Scrolling.Of(false, 120, ctrl: false, shift: true, touchpad: true));

        var t = new TouchpadTurns();
        Same("no touchpad: a wheel's", false, t.Turn(1000));
        t.Fingers(1, 1000);
        Same("one finger moves the pointer: a wheel's", false, t.Turn(1010));
        t.Fingers(2, 2000);
        Same("two fingers: the touchpad's", true, t.Turn(2010));
        t.Fingers(1, 2020);
        t.Fingers(0, 2030);
        Same("a flick, first turn after lifting: still", true, t.Turn(2300));
        Same("gliding on, far apart: still", true, t.Turn(2700));
        Same("and on", true, t.Turn(3150));
        Same("after a pause: a wheel's", false, t.Turn(3700));
        Same("and stays one", false, t.Turn(3710));
        t.Fingers(2, 5000);
        for (long at = 5000; at <= 9000; at += 100) t.Turn(at);
        Same("a glide doesn't go on for good", false, t.Turn(9100));

        Same("a notch out opens", -120, new ScrollStart().Add(ScrollDoes.Zoom, -120, 1000));
        Same("a notch in doesn't", null, new ScrollStart().Add(ScrollDoes.Zoom, 120, 1000));
        Same("a notch sideways opens", 120, new ScrollStart().Add(ScrollDoes.PanX, 120, 1000));

        foreach (int way in new[] { 1, -1 })
        {
            var start = new ScrollStart();
            Same($"a touchpad's scroll doesn't at once ({way})", null, start.Add(ScrollDoes.PanY, 50 * way, 1000));
            Same($"then it does, either way ({way})", 70 * way, start.Add(ScrollDoes.PanY, 20 * way, 1010));
        }

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
