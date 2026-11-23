using System.Security.Cryptography;
using System.Text;
using Expanse.Native;
using static Expanse.Native.User32;

namespace Expanse;

/// Firefox opens all the windows of the session it restores, empty and with its own name as their
/// title, and only then fills them. Its session file says which tabs each has: a window's marks are
/// keyed hashes of its tabs' addresses. The addresses themselves are kept nowhere, nor logged.
sealed class FirefoxSupport : IAppSupport
{
    public string Name => "Firefox";

    /// Firefox writes its session file 15 seconds apart.
    public long PatienceMs => 20_000;

    const int PollMs = 15_000;
    /// The only Firefox window, with a title nothing in the session has: not one that's being
    /// restored. With no page's title yet, it has longer to get one.
    const long LoneMs = 3_000, LoneBlankMs = 8_000;

    readonly object gate = new();
    readonly byte[] key = LoadKey();
    readonly Dictionary<nint, (string title, long since)> titles = new();
    Action changed = () => { };
    Timer? timer;
    // Under `gate`:
    List<FirefoxWindow>? session;
    List<(string path, DateTime written)> read = [];
    HashSet<uint> pids = [];
    Task look = Task.CompletedTask;
    long checkedAt = -PollMs;

    public bool Handles(CanvasWindow w) =>
        w.Class == "MozillaWindowClass" && Path.GetFileName(w.AppPath).Equals("firefox.exe", StringComparison.OrdinalIgnoreCase);

    public void Start(Action changed)
    {
        this.changed = changed;
        timer = new Timer(_ => Look(), null, PollMs, PollMs);
    }

    public void Stop() => timer?.Dispose();

    public IReadOnlyDictionary<nint, IReadOnlyList<string>> Marks(IReadOnlyList<CanvasWindow> windows)
    {
        long now = Environment.TickCount64;
        var marks = new Dictionary<nint, IReadOnlyList<string>>();
        List<FirefoxWindow>? known;
        lock (gate)
        {
            var its = windows.Select(w => w.Pid).ToHashSet();
            // Firefox has started since: what was read may be from before it last quit.
            if (its.Any(p => !pids.Contains(p))) { session = null; read = []; checkedAt = now - PollMs; }
            pids = its;
            known = session;
            if (now - checkedAt >= 1000) Look();
        }
        if (known == null) return marks;
        var seen = windows.Select(w =>
        {
            GetWindowRect(w.Hwnd, out RECT r);
            return new FirefoxSeen(w.Hwnd, Title(w.Hwnd), r.Left, r.Top, w.Marks);
        }).ToList();
        foreach (var (h, i) in FirefoxWindows.Assign(seen, known))
            if (known[i].Marks.Count > 0) marks[(nint)h] = known[i].Marks;
        foreach (var gone in titles.Keys.Where(h => windows.All(w => w.Hwnd != h)).ToList()) titles.Remove(gone);
        foreach (var w in seen)
            if (!titles.TryGetValue((nint)w.Hwnd, out var t) || t.title != w.Title) titles[(nint)w.Hwnd] = (w.Title, now);
        if (seen is [var lone] && !marks.ContainsKey((nint)lone.Hwnd)
            && now - titles[(nint)lone.Hwnd].since >= (FirefoxWindows.HasPage(lone.Title) ? LoneMs : LoneBlankMs)) marks[(nint)lone.Hwnd] = [];
        return marks;
    }

    public Task Look()
    {
        lock (gate)
        {
            if (!look.IsCompleted) return look;
            checkedAt = Environment.TickCount64;
            return look = Task.Run(Check);
        }
    }

    /// Reads the session files if they've changed. On a thread of its own, one at a time (Look).
    void Check()
    {
        int profiles;
        HashSet<uint> forPids;
        List<(string path, DateTime written)> last;
        lock (gate)
        {
            if (pids.Count == 0) return;
            (profiles, forPids, last) = (pids.Count, pids, read);
        }
        bool news = false;
        try
        {
            // Each running Firefox has a profile of its own: those most lately written.
            var files = Profiles().Select(NewestIn).OfType<(string path, DateTime written)>().OrderByDescending(f => f.written).Take(profiles).ToList();
            if (files.SequenceEqual(last)) return;
            var windows = new List<FirefoxWindow>();
            foreach (var f in files) windows.AddRange(FirefoxSession.Read(ReadShared(f.path), Mark));
            lock (gate)
            {
                // Firefox started meanwhile: read again.
                if (!forPids.SetEquals(pids)) return;
                (session, read, news) = (windows, files, true);
            }
            Log.Info($"Firefox support: read {windows.Count} windows from {files.Count} session files");
        }
        // Being written or moved just now, or not what we take it for: the next look may do better. The
        // reason only: a JSON error can quote what it read.
        catch (Exception e) { Log.Info($"Firefox support: couldn't read the session ({e.GetType().Name})"); }
        if (news) changed();
    }

    /// Firefox moves and replaces its files as it goes: never in its way, and open only briefly.
    static byte[] ReadShared(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var all = new byte[f.Length];
        f.ReadExactly(all);
        return all;
    }

    /// The session as last written: after it quit, while it runs, and (for a moment as it starts) as
    /// it was when it started.
    static (string, DateTime)? NewestIn(string profile)
    {
        string backups = Path.Combine(profile, "sessionstore-backups");
        var files = new[]
        {
            Path.Combine(profile, "sessionstore.jsonlz4"), Path.Combine(backups, "recovery.jsonlz4"),
            Path.Combine(backups, "recovery.baklz4"), Path.Combine(backups, "previous.jsonlz4"),
        }.Select(p => new FileInfo(p)).Where(f => f.Exists).ToList();
        return files.Count == 0 ? null : files.Select(f => (f.FullName, f.LastWriteTimeUtc)).MaxBy(f => f.LastWriteTimeUtc);
    }

    static List<string> Profiles()
    {
        string Folder(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var found = new List<string>();
        foreach (string root in new[]
        {
            Path.Combine(Folder(Environment.SpecialFolder.ApplicationData), @"Mozilla\Firefox"),
            // From the Microsoft Store.
            Path.Combine(Folder(Environment.SpecialFolder.LocalApplicationData), @"Packages\Mozilla.Firefox_n80bbvh6b1yt2\LocalCache\Roaming\Mozilla\Firefox"),
        })
        {
            string ini = Path.Combine(root, "profiles.ini");
            if (!File.Exists(ini)) continue;
            // Path= is a profile's folder: within the root, or a whole path.
            foreach (string line in File.ReadAllLines(ini))
                if (line.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
                    found.Add(Path.GetFullPath(Path.Combine(root, line[5..].Trim().Replace('/', '\\'))));
            // And those made from within Firefox, which the list may leave out.
            string own = Path.Combine(root, "Profiles");
            if (Directory.Exists(own)) found.AddRange(Directory.GetDirectories(own));
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    string Mark(string url) => Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(url))[..8]);

    /// Random, made once on this PC: without it, hashes of well-known addresses could be looked up.
    static byte[] LoadKey()
    {
        string file = Path.Combine(Log.Folder, "marks.key");
        try
        {
            if (File.Exists(file) && File.ReadAllBytes(file) is { Length: 32 } old) return old;
            var made = RandomNumberGenerator.GetBytes(32);
            Directory.CreateDirectory(Log.Folder);
            File.WriteAllBytes(file, made);
            return made;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Info($"Firefox support: no key ({e.GetType().Name}): windows won't be known after a restart of Expanse");
            return RandomNumberGenerator.GetBytes(32);
        }
    }
}
