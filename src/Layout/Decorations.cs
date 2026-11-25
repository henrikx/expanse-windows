namespace Expanse;

/// Decoration Size: how big what Expanse draws round your windows is (stack tabs, group names and
/// buttons, the panels), on top of the display's scaling.
public static class Decorations
{
    /// The choices, as percentages.
    public const int Smallest = 50, Largest = 200, Default = 100;

    /// `percent` as a choice there is: a settings file may hold anything.
    public static int Size(int percent) => Math.Clamp(percent, Smallest, Largest);

    /// The scale to draw decorations at on a display scaled by `display`.
    public static double Scale(double display, int percent) => display * Size(percent) / 100;
}
