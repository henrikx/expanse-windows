using System.Numerics;
using System.Runtime.InteropServices;
using Expanse.Native;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Expanse.Native.User32;

namespace Expanse;

/// A tab at 100%: `Rect` is in screen coordinates. `Fit`: the stack's fit button, not a tab; `Hwnd`
/// is then its front window.
readonly record struct BarTab(nint Hwnd, string Text, RectD Rect, bool Front, IconImage? Icon, bool Fit = false);

/// The tabs of stacks at 100%, where there's no overlay to draw them on: a small window for each
/// stack that shows, which never takes focus and isn't in the taskbar or Alt-Tab. Each goes directly
/// above the stack's front window, so whatever is in front of that covers it. A bar no longer wanted
/// is destroyed, not hidden: a window belongs to the virtual desktop it was made on.
sealed class StackBars
{
    sealed class Bar(nint hwnd)
    {
        public readonly nint Hwnd = hwnd;
        public List<BarTab> Tabs = new();
        public RectD Box;
        public string Drawn = "";
    }

    const string ClassName = "ExpanseStackBar";
    const uint GW_HWNDPREV = 3, ULW_ALPHA = 2;

    readonly Dictionary<nint, Bar> byKey = new();
    readonly WndProc proc; // kept alive: Windows holds a pointer to it
    readonly nint instance;
    readonly Action<nint> clicked;
    readonly Action<nint, nint[]> fitClicked;
    readonly ID2D1Factory factory = D2D1.D2D1CreateFactory<ID2D1Factory>();
    readonly IDWriteFactory dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
    readonly ID2D1DCRenderTarget target;

    /// `fitClicked`: the front window and all the stack's windows.
    public StackBars(Action<nint> clicked, Action<nint, nint[]> fitClicked)
    {
        this.clicked = clicked;
        this.fitClicked = fitClicked;
        proc = WindowProc;
        instance = RegisterWindowClass(ClassName, proc, LoadCursor(0, IDC_HAND));
        target = factory.CreateDCRenderTarget(new RenderTargetProperties(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
        target.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale; // ClearType needs an opaque background
    }

    public bool Showing => byKey.Count > 0;

    public void Hide()
    {
        foreach (var bar in byKey.Values) DestroyWindow(bar.Hwnd);
        byKey.Clear();
    }

    /// The bars to show: `key` says which stack, and `above` is its front window.
    public void Show(IReadOnlyList<(nint key, nint above, List<BarTab> tabs)> wanted, double scale)
    {
        foreach (nint key in byKey.Keys.Where(k => wanted.All(w => w.key != k)).ToList())
        {
            byKey.Remove(key, out var gone);
            DestroyWindow(gone!.Hwnd);
        }
        foreach (var (key, above, tabs) in wanted)
        {
            if (!byKey.TryGetValue(key, out var bar))
            {
                nint hwnd = CreateWindowEx((uint)(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED), ClassName, "Expanse Stack Tabs",
                    unchecked((uint)WS_POPUP), 0, 0, 1, 1, 0, 0, instance, 0);
                if (hwnd == 0)
                {
                    Log.Info("stack tabs: couldn't create a window");
                    continue;
                }
                byKey[key] = bar = new Bar(hwnd);
            }
            bar.Tabs = tabs;
            bar.Box = tabs.Skip(1).Aggregate(tabs[0].Rect, (a, t) => a.Union(t.Rect));
            string look = $"{scale}|" + string.Join("|", tabs.Select(t =>
                $"{t.Fit}/{t.Text}/{t.Rect.X - bar.Box.X}/{t.Rect.W}/{t.Front}/{(t.Icon == null ? 0 : RuntimeHelpers(t.Icon))}"));
            if (look != bar.Drawn)
            {
                Render(bar, scale);
                bar.Drawn = look;
            }
            Place(bar, above);
        }
    }

    static int RuntimeHelpers(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);

    /// Where it goes, directly above `above`. Nothing ordinary is above that, then it goes on top of
    /// the ordinary windows, by way of always-on-top: the plain request can be ignored while another
    /// app is in front.
    void Place(Bar bar, nint above)
    {
        const uint flags = SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW;
        int x = (int)Math.Round(bar.Box.X), y = (int)Math.Round(bar.Box.Y);
        nint next = GetWindow(above, GW_HWNDPREV);
        if (next == bar.Hwnd)
        {
            SetWindowPos(bar.Hwnd, 0, x, y, 0, 0, flags | SWP_NOZORDER);
            return;
        }
        while (next != 0 && byKey.Values.Any(b => b.Hwnd == next)) next = GetWindow(next, GW_HWNDPREV);
        if (next == 0 || (ExStyle(next) & WS_EX_TOPMOST) != 0)
        {
            SetWindowPos(bar.Hwnd, HWND_TOPMOST, x, y, 0, 0, flags);
            SetWindowPos(bar.Hwnd, HWND_NOTOPMOST, x, y, 0, 0, flags);
        }
        else SetWindowPos(bar.Hwnd, next, x, y, 0, 0, flags);
    }

    /// Drawn like the canvas's tabs (SharedVisualPictures.Draw), with soft edges, so through a bitmap
    /// with an alpha channel. What's clear between the tabs lets clicks through.
    void Render(Bar bar, double scale)
    {
        int w = Math.Max(1, (int)Math.Ceiling(bar.Box.W)), h = Math.Max(1, (int)Math.Ceiling(bar.Box.H));
        nint screen = GetDC(0);
        nint mem = Gdi32.CreateCompatibleDC(screen);
        var header = new BitmapHeader { Size = 40, Width = w, Height = -h, Planes = 1, BitCount = 32 };
        nint bitmap = CreateDIBSection(mem, ref header, 0, out _, 0, 0);
        nint old = Gdi32.SelectObject(mem, bitmap);
        try
        {
            target.BindDC(mem, new Vortice.RawRect(0, 0, w, h));
            target.BeginDraw();
            target.Clear(new Color4(0, 0, 0, 0));
            foreach (var tab in bar.Tabs) DrawTab(tab, bar.Box, scale);
            target.EndDraw();
            var at = new POINT((int)Math.Round(bar.Box.X), (int)Math.Round(bar.Box.Y));
            var size = new SizePixels(w, h);
            var from = new POINT(0, 0);
            var blend = new Blend { Op = 0, Flags = 0, Alpha = 255, Format = 1 }; // AC_SRC_OVER, AC_SRC_ALPHA
            if (!UpdateLayeredWindow(bar.Hwnd, screen, ref at, ref size, mem, ref from, 0, ref blend, ULW_ALPHA))
                Log.Info($"stack tabs: couldn't update the window, error {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            Gdi32.SelectObject(mem, old);
            Gdi32.DeleteObject(bitmap);
            Gdi32.DeleteDC(mem);
            ReleaseDC(0, screen);
        }
    }

    void DrawTab(BarTab tab, RectD box, double scale)
    {
        float x = (float)(tab.Rect.X - box.X), y = (float)(tab.Rect.Y - box.Y), w = (float)tab.Rect.W, h = (float)tab.Rect.H;
        float pad = Math.Min((float)Math.Round(Groups.LabelPad * scale), w / 4);
        float iconRoom = tab.Icon != null ? tab.Icon.Size + (float)Math.Round(Stacks.TabIconGap * scale) : 0;
        float room = w - pad * 2 - iconRoom;
        bool showText = room >= 10 * scale;
        using var pill = target.CreateSolidColorBrush(tab.Front ? new Color4(46 / 255f, 110 / 255f, 220 / 255f, 1f) : new Color4(32 / 255f, 32 / 255f, 34 / 255f, 1f));
        target.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(x, y, w, h), h / 2, h / 2), pill);
        if (tab.Fit)
        {
            using var line = target.CreateSolidColorBrush(new Color4(240 / 255f, 240 / 255f, 240 / 255f, 1f));
            DrawFitGlyph(target, pill, line, x, y, h, scale);
            return;
        }
        if (tab.Icon is IconImage icon)
        {
            float ix = (float)Math.Round(showText ? x + pad : x + (w - icon.Size) / 2), iy = (float)Math.Round(y + (h - icon.Size) / 2);
            var pinned = GCHandle.Alloc(icon.Pixels, GCHandleType.Pinned);
            try
            {
                using var image = target.CreateBitmap(new SizeI(icon.Size, icon.Size), pinned.AddrOfPinnedObject(), (uint)(icon.Size * 4),
                    new BitmapProperties(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
                target.DrawBitmap(image, new Vortice.RawRectF(ix, iy, ix + icon.Size, iy + icon.Size), 1, Vortice.Direct2D1.BitmapInterpolationMode.Linear, null);
            }
            finally { pinned.Free(); }
        }
        if (!showText) return;
        using var font = dwrite.CreateTextFormat("Segoe UI", FontWeight.SemiBold, Vortice.DirectWrite.FontStyle.Normal, (float)(13 * scale));
        font.WordWrapping = WordWrapping.NoWrap;
        using var ellipsis = dwrite.CreateEllipsisTrimmingSign(font);
        font.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, ellipsis);
        using var layout = dwrite.CreateTextLayout(tab.Text, font, room, h);
        using var ink = target.CreateSolidColorBrush(new Color4(240 / 255f, 240 / 255f, 240 / 255f, 1f));
        target.DrawTextLayout(new Vector2(x + pad + iconRoom, y + (h - layout.Metrics.Height) / 2), layout, ink);
    }

    /// Two overlapping squares of one size, in the middle of a button `size` across at (x, y): the
    /// fit button's mark, on the canvas too. The one in front is filled with `back` to hide the other's corner.
    public static void DrawFitGlyph(ID2D1RenderTarget to, ID2D1Brush back, ID2D1Brush line, float x, float y, float size, double scale)
    {
        float whole = (float)Math.Round(11 * scale), shift = (float)Math.Round(3 * scale), side = whole - shift;
        float stroke = (float)Math.Max(1, Math.Round(scale));
        float left = (float)Math.Round(x + (size - whole) / 2) + stroke / 2, top = (float)Math.Round(y + (size - whole) / 2) + stroke / 2;
        var behind = new System.Drawing.RectangleF(left + shift, top, side, side);
        var ahead = new System.Drawing.RectangleF(left, top + shift, side, side);
        to.DrawRectangle(behind, line, stroke);
        to.FillRectangle(ahead, back);
        to.DrawRectangle(ahead, line, stroke);
    }

    nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_MOUSEACTIVATE:
                    return MA_NOACTIVATE;
                case WM_LBUTTONDOWN:
                    if (byKey.Values.FirstOrDefault(b => b.Hwnd == hwnd) is Bar bar)
                    {
                        var p = new Vec((short)(lParam & 0xFFFF) + bar.Box.X, (short)((lParam >> 16) & 0xFFFF) + bar.Box.Y);
                        if (bar.Tabs.FirstOrDefault(t => t.Rect.Contains(p)) is { Hwnd: not 0 } tab)
                        {
                            if (tab.Fit) fitClicked(tab.Hwnd, bar.Tabs.Where(t => !t.Fit).Select(t => t.Hwnd).ToArray());
                            else clicked(tab.Hwnd);
                        }
                    }
                    return 0;
            }
        }
        catch (Exception e)
        {
            Log.Info($"stack tabs: message 0x{msg:X} threw {e}");
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

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

    [StructLayout(LayoutKind.Sequential)]
    struct SizePixels(int w, int h)
    {
        public int W = w, H = h;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Blend
    {
        public byte Op, Flags, Alpha, Format;
    }

    [DllImport("gdi32.dll")] static extern nint CreateDIBSection(nint dc, ref BitmapHeader header, uint usage, out nint bits, nint section, uint offset);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UpdateLayeredWindow(nint hwnd, nint screen, ref POINT at, ref SizePixels size, nint source, ref POINT from, uint key, ref Blend blend, uint flags);
}
