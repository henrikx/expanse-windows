using System.Text.RegularExpressions;

namespace Expanse;

/// A Firefox window on the screen. `Marks`: what it was known by so far, if it was.
public readonly record struct FirefoxSeen(long Hwnd, string Title, double X, double Y, IReadOnlyList<string>? Marks);

/// Tells which of the session file's windows each window on the screen is. Firefox gives its windows
/// no lasting id; what both sides have is the title of the tab you're in, and roughly a position.
public static partial class FirefoxWindows
{
    /// How far apart positions may be (px), between windows with the same title.
    const double Near = 16;

    /// Handle to index in `session`. Windows it can't tell for certain are left out.
    public static Dictionary<long, int> Assign(IReadOnlyList<FirefoxSeen> windows, IReadOnlyList<FirefoxWindow> session)
    {
        var found = new Dictionary<long, int>();
        var used = new HashSet<int>();
        void Take(FirefoxSeen w, int s) { found[w.Hwnd] = s; used.Add(s); }

        // The same window as before, going by its tabs: even if its title has changed since the file
        // was written. Before any new one: the file is some seconds old, and a title may be another's now.
        var same = new List<(FirefoxSeen w, int s, double like, bool fits)>();
        foreach (var w in windows)
            for (int s = 0; s < session.Count; s++)
                if (AppMarks.Likeness(w.Marks, session[s].Marks) is double like and >= AppMarks.Same)
                    same.Add((w, s, like, Fits(w.Title, session[s].Title, loosely: false)));
        foreach (var (w, s, _, _) in same.OrderByDescending(x => x.like).ThenByDescending(x => x.fits))
            if (!found.ContainsKey(w.Hwnd) && !used.Contains(s)) Take(w, s);

        // By title; then, as a page may have changed the number in its title ("(3) Inbox"), without numbers.
        foreach (bool loosely in (ReadOnlySpan<bool>)[false, true])
        {
            var fits = new List<(FirefoxSeen w, int s)>();
            foreach (var w in windows.Where(w => !found.ContainsKey(w.Hwnd)))
            {
                var its = Enumerable.Range(0, session.Count).Where(s => Fits(w.Title, session[s].Title, loosely)).ToList();
                // "A — B — Mozilla Firefox" is the page "A — B", not "A" with something after it.
                int longest = its.Count == 0 ? 0 : its.Max(s => session[s].Title.Length);
                fits.AddRange(its.Where(s => session[s].Title.Length == longest && !used.Contains(s)).Select(s => (w, s)));
            }
            while (TakeCertain(fits) || TakeCertain(fits.Where(f => IsNear(f.w, session[f.s])).ToList()))
                fits.RemoveAll(f => found.ContainsKey(f.w.Hwnd) || used.Contains(f.s));
        }
        return found;

        // Those that fit one window only, which fits no other.
        bool TakeCertain(List<(FirefoxSeen w, int s)> fits)
        {
            var sure = fits.Where(f => fits.Count(x => x.w.Hwnd == f.w.Hwnd) == 1 && fits.Count(x => x.s == f.s) == 1).ToList();
            foreach (var (w, s) in sure) Take(w, s);
            return sure.Count > 0;
        }
    }

    static bool IsNear(FirefoxSeen w, FirefoxWindow s) => Math.Abs(w.X - s.X) <= Near && Math.Abs(w.Y - s.Y) <= Near;

    /// A window's title is its page's, " — ", perhaps the profile's name and another, then "Mozilla Firefox".
    static bool Fits(string window, string page, bool loosely)
    {
        (window, page) = (Plain(window, loosely), Plain(page, loosely));
        return page.Length > 0 && window.StartsWith(page, StringComparison.Ordinal) && window[page.Length..].StartsWith(" — ", StringComparison.Ordinal);
    }

    static string Plain(string title, bool loosely)
    {
        if (loosely) title = Numbers().Replace(title, "");
        return Spaces().Replace(title, " ").Trim();
    }

    /// Whether the title has a page's in it yet: not while Firefox is opening, or on a blank tab.
    public static bool HasPage(string title) => title.Contains(" — ", StringComparison.Ordinal);

    [GeneratedRegex(@"\(?\d+\+?\)?")] private static partial Regex Numbers();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
}
