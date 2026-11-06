namespace Expanse;

public static class Versions
{
    /// "0.1.10" is newer than "0.1.9".
    public static bool IsNewer(string a, string than)
    {
        var x = a.Split('.').Select(p => int.TryParse(p, out int n) ? n : 0).ToArray();
        var y = than.Split('.').Select(p => int.TryParse(p, out int n) ? n : 0).ToArray();
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0;
            if (p != q) return p > q;
        }
        return false;
    }
}
