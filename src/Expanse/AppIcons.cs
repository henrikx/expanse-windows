using System.Runtime.InteropServices;
using Expanse.Native;
using static Expanse.Native.User32;

namespace Expanse;

/// An app's icon as premultiplied BGRA, `Size` pixels square.
sealed class IconImage(int size, byte[] pixels)
{
    public readonly int Size = size;
    public readonly byte[] Pixels = pixels;
}

/// Windows' icons for the groups panel: the one the window shows, otherwise its class's, otherwise
/// its exe's. Asked for once per window while the canvas is up (see Clear), so a hung app costs at
/// most one short wait, and an icon an app changes is right the next time.
sealed class AppIcons
{
    const uint WM_GETICON = 0x7F, SMTO_ABORTIFHUNG = 2, SHGFI_ICON = 0x100, SHGFI_SMALLICON = 1, DI_MASK = 1, DI_NORMAL = 3;
    const int ICON_SMALL = 0, ICON_BIG = 1, ICON_SMALL2 = 2, GCLP_HICONSM = -34, GCLP_HICON = -14, AskMs = 50;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint SendMessageTimeout(nint hwnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] static extern nint GetClassLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int w, int h, uint step, nint brush, uint flags);
    [DllImport("gdi32.dll")] static extern nint CreateDIBSection(nint dc, ref BitmapHeader header, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern nint SHGetFileInfo(string path, uint attributes, ref FileInfo info, uint size, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPels, YPels;
        public uint ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct FileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    readonly Dictionary<(nint hwnd, int size), IconImage?> known = new();

    public IconImage? Get(CanvasWindow w, int size)
    {
        if (known.TryGetValue((w.Hwnd, size), out var icon)) return icon;
        icon = FromWindow(w.Hwnd, size) ?? FromFile(w.AppPath, size);
        if (icon == null) Log.Info($"icon: none found for {w}");
        return known[(w.Hwnd, size)] = icon;
    }

    public void Clear() => known.Clear();

    static IconImage? FromWindow(nint hwnd, int size)
    {
        // The nearest size first: it's scaled to fit.
        int[] order = size > 24 ? [ICON_BIG, ICON_SMALL2, ICON_SMALL] : [ICON_SMALL2, ICON_SMALL, ICON_BIG];
        if (!IsHungAppWindow(hwnd))
            foreach (int which in order)
                if (SendMessageTimeout(hwnd, WM_GETICON, which, 0, SMTO_ABORTIFHUNG, AskMs, out nint h) != 0 && h != 0 && Render(h, size) is IconImage asked)
                    return asked;
        foreach (int index in size > 24 ? new[] { GCLP_HICON, GCLP_HICONSM } : [GCLP_HICONSM, GCLP_HICON])
            if (GetClassLongPtr(hwnd, index) is var h && h != 0 && Render(h, size) is IconImage ofClass)
                return ofClass;
        return null;
    }

    static IconImage? FromFile(string path, int size)
    {
        if (path.Length == 0) return null;
        var info = new FileInfo();
        uint flags = SHGFI_ICON | (size > 24 ? 0 : SHGFI_SMALLICON);
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<FileInfo>(), flags) == 0 || info.Icon == 0) return null;
        try { return Render(info.Icon, size); }
        finally { DestroyIcon(info.Icon); }
    }

    static IconImage? Render(nint icon, int size)
    {
        byte[]? colour = Draw(icon, size, DI_NORMAL);
        if (colour == null) return null;
        byte[]? pixels = IconPixels.Finish(colour, Draw(icon, size, DI_MASK));
        return pixels == null ? null : new IconImage(size, pixels);
    }

    /// The icon on a clear 32-bit bitmap.
    static byte[]? Draw(nint icon, int size, uint flags)
    {
        var header = new BitmapHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapHeader>(), Width = size, Height = -size, // negative: top row first
            Planes = 1, BitCount = 32,
        };
        nint dc = Gdi32.CreateCompatibleDC(0);
        nint bitmap = CreateDIBSection(dc, ref header, 0, out nint bits, 0, 0);
        if (bitmap == 0) { Gdi32.DeleteDC(dc); return null; }
        nint old = Gdi32.SelectObject(dc, bitmap);
        try
        {
            if (!DrawIconEx(dc, 0, 0, icon, size, size, 0, 0, flags)) return null;
            GdiFlush();
            var pixels = new byte[size * size * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return pixels;
        }
        finally
        {
            Gdi32.SelectObject(dc, old);
            Gdi32.DeleteObject(bitmap);
            Gdi32.DeleteDC(dc);
        }
    }
}
