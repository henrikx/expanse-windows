namespace Expanse;

/// Turning an icon as Windows draws it into pixels with real transparency.
public static class IconPixels
{
    /// `colour`: 32-bit BGRA as DrawIconEx left it on a clear bitmap, already premultiplied when
    /// the icon has an alpha channel. An old icon has none, so every alpha comes out 0, and `mask`
    /// (its mask drawn the same way: black where the icon is) says where it is. Null if nothing
    /// shows, so the caller can try another source.
    public static byte[]? Finish(byte[] colour, byte[]? mask)
    {
        bool hasAlpha = false;
        for (int i = 3; i < colour.Length; i += 4)
            if (colour[i] != 0) { hasAlpha = true; break; }
        if (hasAlpha) return colour;
        if (mask == null || mask.Length != colour.Length) return null;
        var result = new byte[colour.Length];
        bool any = false;
        for (int i = 0; i < colour.Length; i += 4)
        {
            if (mask[i] != 0 || mask[i + 1] != 0 || mask[i + 2] != 0) continue;
            (result[i], result[i + 1], result[i + 2], result[i + 3]) = (colour[i], colour[i + 1], colour[i + 2], 255);
            any = true;
        }
        return any ? result : null;
    }
}
