namespace Expanse;

/// How an app's special support (IAppSupport) tells its windows apart: by marks, a set of short
/// strings that mostly stay the same when the app is closed and opened again (one per tab, say, or
/// a single id).
public static class AppMarks
{
    /// How alike two windows' marks must be to be the same window's.
    public const double Same = 0.5;

    /// 0 to 1: how many of the marks either has both have. 0 if either has none.
    public static double Likeness(IReadOnlyCollection<string>? a, IReadOnlyCollection<string>? b)
    {
        if (a is not { Count: > 0 } || b is not { Count: > 0 }) return 0;
        var one = a.ToHashSet();
        var other = b.ToHashSet();
        int both = one.Count(other.Contains);
        return (double)both / (one.Count + other.Count - both);
    }
}

/// Windows that have opened but that their app's support doesn't know yet: they aren't matched to
/// saved spots until it does, or its patience runs out.
public sealed class WaitingWindows
{
    /// How long none of them may have changed before any is matched (ms): an app opening its windows
    /// moves and sizes them as it goes, and would undo being placed.
    public const long QuietMs = 1500;

    readonly record struct Waiting(long Until, bool MayTake);
    readonly Dictionary<long, RectD> frames = new();
    long changedAt;
    readonly Dictionary<long, Waiting> waiting = new();

    public bool Any => waiting.Count > 0;
    public bool Contains(long hwnd) => waiting.ContainsKey(hwnd);

    /// `mayTake`: it may take a spot that isn't for certain its own.
    public void Add(long hwnd, long now, long patience, bool mayTake)
    {
        if (waiting.TryAdd(hwnd, new(now + patience, mayTake))) changedAt = now;
    }

    /// Where a window is now; any that isn't waiting is ignored.
    public void Saw(long hwnd, RectD frame, long now)
    {
        if (!waiting.ContainsKey(hwnd) || frames.TryGetValue(hwnd, out var was) && was == frame) return;
        frames[hwnd] = frame;
        changedAt = now;
    }

    /// You moved it, or it has gone: it isn't waiting any more.
    public bool Forget(long hwnd)
    {
        frames.Remove(hwnd);
        return waiting.Remove(hwnd);
    }

    /// Takes out the ones to match now: those that are known, once all are quiet, and those out of patience.
    public List<(long hwnd, bool mayTake, bool known)> Due(long now, Func<long, bool> isKnown)
    {
        var due = new List<(long, bool, bool)>();
        foreach (var (hwnd, w) in waiting.ToList())
        {
            bool known = isKnown(hwnd);
            if ((!known || now - changedAt < QuietMs) && now < w.Until) continue;
            Forget(hwnd);
            due.Add((hwnd, w.MayTake, known));
        }
        return due;
    }
}
