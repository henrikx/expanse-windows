namespace Expanse;

/// What to do when a canvas window gets focus and is hidden enough to matter.
public enum FocusAction { None, StayAfterWindowWentAway, WaitForRelease, StayMovedByHand, Glide }

public static class FocusRules
{
    /// How long after a window went away focus landing elsewhere is still the system's doing (ms).
    public const long WentAwayWindow = 1500;
    /// The key that closed it is still being let go for this long: only input after it counts (ms).
    public const long InputGrace = 300;
    /// A press that began a move this long before focus changed (ms) still belongs to the focus.
    const long MoveLead = 500;

    public static FocusAction Decide(bool autoCentre, bool hiddenEnough, bool windowJustWentAway, bool buttonHeld, bool movedByHand)
    {
        if (!autoCentre || !hiddenEnough) return FocusAction.None;
        if (windowJustWentAway) return FocusAction.StayAfterWindowWentAway;
        if (buttonHeld) return FocusAction.WaitForRelease;
        if (movedByHand) return FocusAction.StayMovedByHand;
        return FocusAction.Glide;
    }

    /// `inputSinceGrace`: a click or key since `goneAt` + InputGrace.
    public static bool WindowJustWentAway(long now, long goneAt, bool inputSinceGrace) =>
        now - goneAt < WentAwayWindow && !inputSinceGrace;

    /// The press that gave a window focus went on to move or resize it: the move began about when
    /// focus changed, and the window ended up somewhere else.
    public static bool MovedByHand(bool sameWindow, long moveStartedAt, long focusChangedAt, RectD before, RectD now)
    {
        if (!sameWindow || moveStartedAt < focusChangedAt - MoveLead) return false;
        return before.X != now.X || before.Y != now.Y || before.Right != now.Right || before.Bottom != now.Bottom;
    }
}
