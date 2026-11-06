using System.Diagnostics;
using static Expanse.Native.User32;

namespace Expanse;

/// Picking a group's colour: a click on the circle by its label grows the wheel out of that circle.
/// Pressing or dragging on the wheel changes the group's colour as you go and letting go keeps it.
/// Esc, a click anywhere else or moving the view closes the wheel and leaves the colour as it was.
sealed partial class Canvas
{
    const double ColourWheelGrowth = 0.14;

    /// The group whose wheel is open.
    CanvasGroup? colourWheelGroup;
    GroupColour colourBefore;
    /// Where the group's box was on screen when the wheel opened: the wheel goes if it moves.
    RectD colourWheelBox;
    long colourWheelOpenedAt;
    /// How far the wheel has grown, 0 to 1.
    double colourWheelGrown;

    bool ColourWheelGrowing => colourWheelGroup != null && colourWheelGrown < 1;

    RectD ColourWheelRect(CanvasGroup g, double k) => GroupColours.WheelRect(LabelRects(g, k).swatch, View, k);

    void OpenColourWheel(CanvasGroup g)
    {
        colourWheelGroup = g;
        colourBefore = g.Colour;
        colourWheelBox = camera.ToView(g.Box);
        colourWheelOpenedAt = Stopwatch.GetTimestamp();
        colourWheelGrown = 0;
        Log.Info($"colour wheel for group \"{g.Name}\"");
    }

    /// True if the wheel was open: the press is on it, or it just closes the wheel and does nothing else.
    bool PressColourWheel(Vec p)
    {
        if (colourWheelGroup is not CanvasGroup g) return false;
        var wheel = ColourWheelRect(g, UiScale);
        if (GroupColours.OnWheel(wheel, p))
        {
            colourWheelGrown = 1;
            drag = new Drag(DragKind.ColourPick, p);
            SetCapture(overlay.Hwnd);
            g.Colour = GroupColours.ColourOnWheel(wheel, p);
        }
        else CancelColourWheel();
        Redraw();
        return true;
    }

    void DragColour(Vec p)
    {
        if (colourWheelGroup is not CanvasGroup g) return;
        g.Colour = GroupColours.ColourOnWheel(ColourWheelRect(g, UiScale), p);
        Redraw();
    }

    /// The button came up on the wheel: the colour stays. Anything else ending the drag undoes it.
    void EndColourPick(bool click)
    {
        if (!click || colourWheelGroup is not CanvasGroup g)
        {
            CancelColourWheel();
            return;
        }
        Log.Info($"group \"{g.Name}\": colour {g.Colour.Packed:X6}");
        colourWheelGroup = null;
        ScheduleSave();
    }

    void CancelColourWheel()
    {
        if (colourWheelGroup is CanvasGroup g) g.Colour = colourBefore;
        colourWheelGroup = null;
        if (drag?.What != DragKind.ColourPick) return;
        drag = null;
        ReleaseCapture();
    }

    /// The wheel to draw now, if any. It goes when the view or the group moves, or on leaving the canvas.
    ColourWheelDrawing? ColourWheelNow(double k)
    {
        if (colourWheelGroup is not CanvasGroup g) return null;
        if (!active || gliding || !groups.Contains(g) || camera.ToView(g.Box) != colourWheelBox)
        {
            CancelColourWheel();
            return null;
        }
        colourWheelGrown = Math.Max(colourWheelGrown,
            Math.Min(1, Stopwatch.GetElapsedTime(colourWheelOpenedAt).TotalSeconds / ColourWheelGrowth));
        var (swatch, _, _) = LabelRects(g, k);
        var wheel = GroupColours.WheelRect(swatch, View, k);
        return new ColourWheelDrawing(GroupColours.WheelGrowing(swatch, wheel, colourWheelGrown), wheel, GroupColours.PointOnWheel(wheel, g.Colour));
    }
}
