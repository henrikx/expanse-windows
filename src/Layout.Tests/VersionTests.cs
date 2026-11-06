using Expanse;

/// Comparing version numbers (Versions).
static class VersionTests
{
    public static void Run(Action<string, bool, string> expect)
    {
        void Newer(string name, string a, string than, bool want) =>
            expect($"versions: {name}", Versions.IsNewer(a, than) == want, $"IsNewer({a}, {than}) want {want}");

        Newer("0.1.10 is newer than 0.1.9", "0.1.10", "0.1.9", true);
        Newer("0.1.9 is not newer than 0.1.10", "0.1.9", "0.1.10", false);
        Newer("equal is not newer", "0.2.14", "0.2.14", false);
        Newer("older is not newer", "0.2.13", "0.2.14", false);
        Newer("a newer major wins over a bigger minor", "1.0.0", "0.9.9", true);
        Newer("0.2 against 0.2.0 is not newer", "0.2", "0.2.0", false);
        Newer("0.2.0 against 0.2 is not newer", "0.2.0", "0.2", false);
        Newer("0.2.1 is newer than 0.2", "0.2.1", "0.2", true);
        Newer("0.2 is not newer than 0.2.1", "0.2", "0.2.1", false);
        Newer("a non-numeric part counts as 0", "0.x.1", "0.0.1", false);
        Newer("a non-numeric part counts as 0, the other way", "0.0.1", "0.x.1", false);
        Newer("a non-numeric part is below a number", "0.1.1", "0.x.1", true);
    }
}
