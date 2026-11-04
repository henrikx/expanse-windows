namespace Expanse;

/// Expanse moves windows asynchronously, so a hung app can't stall it, and a busy app can take a
/// while to act on that. Until it does, the window is at a spot Expanse knows about: where it was
/// when asked to move, or where an earlier move sends it. Found at one of those, it hasn't been
/// moved by anyone else. After a while, it's taken at its word: an app that won't go there
/// (a window that snaps back, say) shouldn't be fought over.
public sealed class MovesUnderway
{
    public const long PatienceMs = 5000;

    readonly HashSet<(int X, int Y)> spots = new();
    long sentAt;

    /// Whether any move hasn't been seen to land. One may still land, however long ago it was sent.
    public bool Any => spots.Count > 0;

    /// A move was sent: the window is at (fromX, fromY), going to (toX, toY). `now` in ms.
    public void Sent(int fromX, int fromY, int toX, int toY, long now)
    {
        spots.Add((fromX, fromY));
        spots.Add((toX, toY));
        sentAt = now;
    }

    /// The window is where it was last sent: nothing is on its way.
    public void Landed() => spots.Clear();

    /// Whether a window found at (x, y) is explained by moves still on their way.
    public bool Explains(int x, int y, long now) => now - sentAt < PatienceMs && spots.Contains((x, y));
}
