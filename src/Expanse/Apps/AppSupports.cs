namespace Expanse;

/// The apps with special support. One that fails is left out from then on: its windows are matched
/// as any other app's.
sealed class AppSupports
{
    readonly List<IAppSupport> all = [new FirefoxSupport()];

    public void Start(Action changed)
    {
        foreach (var s in all.ToList()) Try(s, () => { s.Start(changed); return true; });
    }

    public void Stop()
    {
        foreach (var s in all.ToList()) Try(s, () => { s.Stop(); return true; });
    }

    public IAppSupport? For(CanvasWindow w) => all.Count == 0 ? null : all.ToList().FirstOrDefault(s => Try(s, () => s.Handles(w)));

    /// What's known now of `windows`, kept with each. One no longer known keeps what it had.
    public void Refresh(IEnumerable<CanvasWindow> windows)
    {
        if (all.Count == 0) return;
        var list = windows.ToList();
        foreach (var s in all.ToList())
        {
            var its = list.Where(w => For(w) == s).ToList();
            if (its.Count == 0 || Try(s, () => s.Marks(its)) is not { } marks) continue;
            foreach (var w in its)
                if (marks.TryGetValue(w.Hwnd, out var m) && (m.Count > 0 || w.Marks == null)) w.Marks = m;
        }
    }

    T? Try<T>(IAppSupport s, Func<T> ask)
    {
        try { return ask(); }
        catch (Exception e)
        {
            Log.Info($"{s.Name} support: failed, not used from now on: {e}");
            all.Remove(s);
            return default;
        }
    }
}
