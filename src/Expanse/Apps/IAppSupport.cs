namespace Expanse;

/// Special support for an app whose windows can't be told apart by their titles as it opens again:
/// it says what each window is (its marks, see AppMarks), which is saved with the window's spot and
/// looked for when the app comes back. Listed in AppSupports. All of it is called on the canvas's
/// thread and mustn't wait: reading files and the like is for a thread of its own.
interface IAppSupport
{
    /// For the log.
    string Name { get; }

    bool Handles(CanvasWindow w);

    /// How long a window that has just opened may wait to be known before it's matched as any other
    /// app's is (by title, then by app).
    long PatienceMs { get; }

    /// Starts watching, if there's anything to watch. `changed`: more may be known now (any thread).
    void Start(Action changed);

    /// The marks of those of its `windows` it knows; none for one it knows it has nothing to tell by,
    /// which then needn't wait. All at once, as it may take them all to tell
    /// which is which. Saved in the layout: anything private must be hashed (LayoutStore.TitleKey).
    IReadOnlyDictionary<nint, IReadOnlyList<string>> Marks(IReadOnlyList<CanvasWindow> windows);

    void Stop();
}
