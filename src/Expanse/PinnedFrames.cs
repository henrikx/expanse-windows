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

/// What a pinned group shows at 100%, in screen coordinates: its box, and the row above it.
/// `Handle`: the strip along its screen-side edge that slides it out, `Edge` (-1 left, 1 right) the side; empty when it isn't
/// snapped to one, or is slid out.
readonly record struct PinnedFrameLook(long Id, RectD Box, string Name, RectD Swatch, RectD Label, RectD Pin, RectD Trash, GroupColour Colour,
    RectD Handle = default, int Edge = 0, RectD Dock = default, bool KeepClear = false, bool DockRight = false)
{
    public RectD Bounds => Box.Union(Swatch).Union(Label).Union(Pin).Union(Trash).Union(Dock.W > 0 ? Dock : Pin);
}

enum PinnedFramePart { Colour, Label, Pin, Dock, Trash, Edge, Body }

/// How opaque the windows drawn at 100% (the stack tabs and the pinned groups' frames) are: they fade
/// out as a glide starts and in as it ends (Canvas.SetDecorationAlpha), not appear and disappear.
static class DecorationAlpha
{
    public static byte Value = 255;
}

/// Pinned groups at 100%, where there's no overlay to draw them on: a window for each, fainter than
/// zoomed out, just behind the group's apps (which are always-on-top), so its box, name and buttons
/// show round them. It never takes focus and isn't in the taskbar or Alt-Tab. Like the stack tabs'
/// (StackBars), a frame no longer wanted is destroyed, not hidden: a window belongs to the virtual
/// desktop it was made on.
sealed class PinnedFrames
{
    sealed class Frame(nint hwnd)
    {
        public readonly nint Hwnd = hwnd;
        public PinnedFrameLook Look;
        public string Drawn = "";
    }

    const string ClassName = "ExpansePinnedFrame";
    const uint ULW_ALPHA = 2, WM_MOUSEMOVE = 0x200, WM_LBUTTONUP = 0x202, WM_CAPTURECHANGED = 0x215;
    /// How much fainter than zoomed out.
    const float Faint = 0.6f;
    public const double FaintLook = Faint;
    /// The dock button's picture: a window with a panel docked at that side (E90C / E90D).
    public const string DockLeftGlyph = "\uE90C", DockRightGlyph = "\uE90D";

    readonly Dictionary<long, Frame> byId = new();
    readonly WndProc proc; // kept alive: Windows holds a pointer to it
    readonly nint instance;
    readonly ID2D1Factory factory = D2D1.D2D1CreateFactory<ID2D1Factory>();
    readonly IDWriteFactory dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
    readonly ID2D1DCRenderTarget target;
    /// A press on a frame (the group, which part, the pointer on the screen), the pointer moving
    /// while it's held, and its release.
    public Action<long, PinnedFramePart, Vec>? Pressed;
    public Action<Vec>? Dragged;
    public Action? Released;
    nint held;

    public PinnedFrames()
    {
        proc = WindowProc;
        instance = RegisterWindowClass(ClassName, proc, LoadCursor(0, IDC_ARROW));
        target = factory.CreateDCRenderTarget(new RenderTargetProperties(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
        target.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale; // ClearType needs an opaque background
    }

    /// Their opacity, as it is now (DecorationAlpha), without drawing them again.
    public void ApplyAlpha()
    {
        var blend = new Blend { Op = 0, Flags = 0, Alpha = DecorationAlpha.Value, Format = 1 };
        foreach (var f in byId.Values) UpdateLayeredWindowBlend(f.Hwnd, 0, 0, 0, 0, 0, 0, ref blend, ULW_ALPHA);
    }

    public void Hide()
    {
        foreach (var f in byId.Values) DestroyWindow(f.Hwnd);
        byId.Clear();
    }

    /// The frames to show, each just behind `below` (its group's lowest app; 0: none showing).
    public void Show(IReadOnlyList<(PinnedFrameLook look, nint below)> wanted, double scale)
    {
        foreach (long id in byId.Keys.Where(id => wanted.All(w => w.look.Id != id)).ToList())
        {
            byId.Remove(id, out var gone);
            DestroyWindow(gone!.Hwnd);
        }
        foreach (var (look, below) in wanted)
        {
            if (!byId.TryGetValue(look.Id, out var frame))
            {
                nint hwnd = CreateWindowEx((uint)(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED), ClassName, "Expanse Pinned Group",
                    unchecked((uint)WS_POPUP), 0, 0, 1, 1, 0, 0, instance, 0);
                if (hwnd == 0)
                {
                    Log.Info("pinned group: couldn't create a frame window");
                    continue;
                }
                byId[look.Id] = frame = new Frame(hwnd);
            }
            frame.Look = look;
            var b = look.Bounds;
            string drawn = $"{scale}|{look.Name}|{look.Colour.Packed}|{look.Box.X - b.X},{look.Box.Y - b.Y},{look.Box.W},{look.Box.H}|{look.Label.W}|{look.Edge}|{look.Handle.W}|{look.KeepClear}|{look.DockRight}";
            if (drawn != frame.Drawn)
            {
                Render(frame, scale);
                frame.Drawn = drawn;
            }
            Place(frame, below);
        }
    }

    /// Moved as a whole (dragged at 100%), without drawing it again. With a `batch` (DeferWindowPos),
    /// it moves with the other windows in it, at once; the batch to go on with.
    public nint MoveBy(long id, Vec by, nint batch = 0)
    {
        if (!byId.TryGetValue(id, out var f)) return batch;
        var l = f.Look;
        f.Look = l with { Box = l.Box.Offset(by), Swatch = l.Swatch.Offset(by), Label = l.Label.Offset(by), Pin = l.Pin.Offset(by), Trash = l.Trash.Offset(by), Handle = l.Handle.Offset(by), Dock = l.Dock.Offset(by) };
        var b = f.Look.Bounds;
        const uint flags = SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE;
        int x = (int)Math.Round(b.X), y = (int)Math.Round(b.Y);
        if (batch != 0) return DeferWindowPos(batch, f.Hwnd, 0, x, y, 0, 0, flags);
        SetWindowPos(f.Hwnd, 0, x, y, 0, 0, flags);
        return 0;
    }

    void Place(Frame f, nint below)
    {
        var b = f.Look.Bounds;
        int x = (int)Math.Round(b.X), y = (int)Math.Round(b.Y);
        const uint flags = SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW;
        // Behind its lowest app, which is always-on-top: so is the frame then, over everything else.
        SetWindowPos(f.Hwnd, below != 0 ? below : HWND_TOPMOST, x, y, 0, 0, flags);
    }

    void Render(Frame f, double scale)
    {
        var look = f.Look;
        var b = look.Bounds;
        int w = Math.Max(1, (int)Math.Ceiling(b.W)), h = Math.Max(1, (int)Math.Ceiling(b.H));
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
            Draw(look, b, scale);
            target.EndDraw();
            var at = new POINT((int)Math.Round(b.X), (int)Math.Round(b.Y));
            var size = new SizePixels(w, h);
            var from = new POINT(0, 0);
            var blend = new Blend { Op = 0, Flags = 0, Alpha = DecorationAlpha.Value, Format = 1 }; // AC_SRC_OVER, AC_SRC_ALPHA
            if (!UpdateLayeredWindow(f.Hwnd, screen, ref at, ref size, mem, ref from, 0, ref blend, ULW_ALPHA))
                Log.Info($"pinned group: couldn't update its frame, error {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            Gdi32.SelectObject(mem, old);
            Gdi32.DeleteObject(bitmap);
            Gdi32.DeleteDC(mem);
            ReleaseDC(0, screen);
        }
    }

    /// As zoomed out (SharedVisualPictures.SetGroups, solid), but fainter.
    void Draw(PinnedFrameLook look, RectD bounds, double scale)
    {
        System.Drawing.RectangleF Local(RectD r) => new((float)(r.X - bounds.X), (float)(r.Y - bounds.Y), (float)r.W, (float)r.H);
        var fillTint = GroupColours.Fill(look.Colour, 1, false);
        var edgeTint = GroupColours.Edge(look.Colour, 1, false);
        using var fill = target.CreateSolidColorBrush(new Color4(fillTint.R, fillTint.G, fillTint.B, fillTint.A * Faint));
        using var edge = target.CreateSolidColorBrush(new Color4(edgeTint.R, edgeTint.G, edgeTint.B, edgeTint.A * Faint));
        var box = Local(look.Box);
        target.FillRectangle(box, fill);
        float t = (float)Math.Max(1, Math.Round(scale * 2));
        target.DrawRectangle(new System.Drawing.RectangleF(box.X + t / 2, box.Y + t / 2, box.Width - t, box.Height - t), edge, t);

        using var dark = target.CreateSolidColorBrush(new Color4(32 / 255f, 32 / 255f, 34 / 255f, Faint));
        using var ink = target.CreateSolidColorBrush(new Color4(240 / 255f, 240 / 255f, 240 / 255f, Faint));
        using var colour = target.CreateSolidColorBrush(new Color4(look.Colour.R / 255f, look.Colour.G / 255f, look.Colour.B / 255f, Faint));
        if (look.Handle.W > 0)
        {
            var h = Local(look.Handle);
            using var strip = target.CreateSolidColorBrush(new Color4(edgeTint.R, edgeTint.G, edgeTint.B, Math.Min(1, edgeTint.A * Faint * 1.6f)));
            target.FillRectangle(h, strip);
            using var chevron = dwrite.CreateTextFormat("Segoe MDL2 Assets", FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, (float)(10 * scale));
            chevron.TextAlignment = TextAlignment.Center;
            chevron.ParagraphAlignment = ParagraphAlignment.Center;
            using var chevronInk = target.CreateSolidColorBrush(new Color4(240 / 255f, 240 / 255f, 240 / 255f, Faint));
            target.DrawText(look.Edge < 0 ? "\uE76B" : "\uE76C", chevron, new Vortice.RawRectF(h.X, h.Y, h.X + h.Width, h.Y + h.Height), chevronInk);
        }
        var swatch = Local(look.Swatch);
        target.FillEllipse(new Ellipse(new Vector2(swatch.X + swatch.Width / 2, swatch.Y + swatch.Height / 2), swatch.Width / 2, swatch.Height / 2), colour);

        var label = Local(look.Label);
        target.FillRoundedRectangle(new RoundedRectangle(label, label.Height / 2, label.Height / 2), dark);
        float pad = (float)Math.Round(Groups.LabelPad * scale);
        using (var font = dwrite.CreateTextFormat("Segoe UI", FontWeight.SemiBold, Vortice.DirectWrite.FontStyle.Normal, (float)(13 * scale)))
        {
            font.WordWrapping = WordWrapping.NoWrap;
            using var ellipsis = dwrite.CreateEllipsisTrimmingSign(font);
            font.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, ellipsis);
            using var layout = dwrite.CreateTextLayout(look.Name, font, Math.Max(1, label.Width - pad * 2), label.Height);
            target.DrawTextLayout(new Vector2(label.X + pad, label.Y + (label.Height - layout.Metrics.Height) / 2), layout, ink);
        }
        using var glyph = dwrite.CreateTextFormat("Segoe MDL2 Assets", FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, (float)(12 * scale));
        glyph.TextAlignment = TextAlignment.Center;
        glyph.ParagraphAlignment = ParagraphAlignment.Center;
        foreach (var (at, text) in new[] { (look.Pin, ""), (look.Trash, "") })
        {
            var r = Local(at);
            target.FillEllipse(new Ellipse(new Vector2(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2), dark);
            target.DrawText(text, glyph, new Vortice.RawRectF(r.X, r.Y, r.X + r.Width, r.Y + r.Height), ink);
        }
        if (look.Dock.W > 0)
        {
            // On: in the blue the front tab is in.
            using var on = target.CreateSolidColorBrush(new Color4(46 / 255f, 110 / 255f, 220 / 255f, Faint + 0.2f));
            var r = Local(look.Dock);
            target.FillEllipse(new Ellipse(new Vector2(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2), look.KeepClear ? on : dark);
            target.DrawText(look.DockRight ? DockRightGlyph : DockLeftGlyph, glyph, new Vortice.RawRectF(r.X, r.Y, r.X + r.Width, r.Y + r.Height), ink);
        }
    }

    static Vec Cursor()
    {
        GetCursorPos(out var p);
        return new Vec(p.X, p.Y);
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
                    if (byId.Values.FirstOrDefault(f => f.Hwnd == hwnd) is Frame frame)
                    {
                        var p = Cursor();
                        var l = frame.Look;
                        var part = l.Swatch.Contains(p) ? PinnedFramePart.Colour : l.Pin.Contains(p) ? PinnedFramePart.Pin
                            : l.Dock.W > 0 && l.Dock.Contains(p) ? PinnedFramePart.Dock
                            : l.Trash.Contains(p) ? PinnedFramePart.Trash : l.Label.Contains(p) ? PinnedFramePart.Label
                            : l.Handle.W > 0 && l.Handle.Contains(p) ? PinnedFramePart.Edge
                            : l.Box.Contains(p) ? PinnedFramePart.Body : (PinnedFramePart?)null;
                        if (part is PinnedFramePart.Label or PinnedFramePart.Body)
                        {
                            held = hwnd;
                            SetCapture(hwnd);
                        }
                        if (part is PinnedFramePart pressed) Pressed?.Invoke(l.Id, pressed, p);
                    }
                    return 0;
                case WM_MOUSEMOVE when held == hwnd:
                    Dragged?.Invoke(Cursor());
                    return 0;
                case WM_LBUTTONUP when held == hwnd:
                    held = 0;
                    ReleaseCapture();
                    Released?.Invoke();
                    return 0;
                case WM_CAPTURECHANGED when held == hwnd:
                    held = 0;
                    Released?.Invoke();
                    return 0;
            }
        }
        catch (Exception e)
        {
            Log.Info($"pinned group frame: message 0x{msg:X} threw {e}");
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
    /// Only the blend: the window keeps its picture.
    [DllImport("user32.dll", EntryPoint = "UpdateLayeredWindow", SetLastError = true)]
    static extern bool UpdateLayeredWindowBlend(nint hwnd, nint screen, nint at, nint size, nint source, nint from, uint key, ref Blend blend, uint flags);
}
