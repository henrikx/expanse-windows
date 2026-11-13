using System.Numerics;
using System.Runtime.InteropServices;
using Expanse.Native;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;
using AlphaMode = Vortice.DXGI.AlphaMode;
using BitmapInterpolationMode = Vortice.DirectComposition.BitmapInterpolationMode;
using BorderMode = Vortice.DirectComposition.BorderMode;
using Color4 = Vortice.Mathematics.Color4;

namespace Expanse;

/// Live pictures as DWM shared thumbnail visuals: DirectComposition visuals that DWM fills with
/// a window's content, placed in our own visual tree over the overlay. Every picture sits at its
/// canvas spot inside one `canvas` visual, so the camera is a single transform, applied by DWM
/// to all of them in the same frame. Our UI lives in the same tree, over the pictures: the hover
/// ring, titles (drawn with Direct2D/DirectWrite) and snapping guides.
///
/// Undocumented: dwmapi.dll ordinal 147 (see ADeltaX, "DWM Thumbnails. But with
/// IDCompositionVisual"), checked on Windows 11 build 26300. If it's missing or fails for every
/// window, the canvas falls back to DWM thumbnails.
sealed class SharedVisualPictures : IPictures
{
    [DllImport("dwmapi.dll", EntryPoint = "#147")]
    static extern int DwmpCreateSharedThumbnailVisual(nint dest, nint source, uint flags,
        ref DWM_THUMBNAIL_PROPERTIES props, nint dcompDevice, out nint visual, out nint thumb);

    [DllImport("dcomp.dll")]
    static extern int DCompositionCreateDevice3(nint renderingDevice, in Guid iid, out nint device);

    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, int[]? levels,
        uint levelCount, uint sdkVersion, out nint device, out int level, out nint context);

    sealed class Picture(IDCompositionVisual2 visual, nint thumb, (int w, int h) size = default, RECT source = default)
    {
        public readonly IDCompositionVisual2 Visual = visual;
        public readonly nint Thumb = thumb;
        /// What it was made for: a window at another size, or with other borders, needs a new one.
        public readonly (int w, int h) Size = size;
        public readonly RECT Source = source;

        public bool Fits(CanvasWindow w) =>
            Size == PictureSize(w) && (Source.Left, Source.Top, Source.Right, Source.Bottom) ==
            (w.SourceRect.Left, w.SourceRect.Top, w.SourceRect.Right, w.SourceRect.Bottom);
    }

    static (int w, int h) PictureSize(CanvasWindow w) => ((int)Math.Round(w.Frame.W), (int)Math.Round(w.Frame.H));

    /// A picture's own ring and close button, in the canvas right above it: a window in front
    /// covers them as it covers the picture. Canvas coordinates (see SetMarks).
    sealed class MarkLayer(IDCompositionVisual2 container, IDCompositionVisual2[] ring, IDCompositionVisual2 close)
    {
        public readonly IDCompositionVisual2 Container = container;
        public readonly IDCompositionVisual2[] Ring = ring;
        public readonly IDCompositionVisual2 Close = close;
        /// The tabs of its stack, while this window is the one in front, by the window of each.
        public readonly Dictionary<nint, Title> Tabs = new();
        public bool Showing;
    }

    /// A window's title: a visual with a surface drawn once per text and DPI scale.
    sealed class Title(IDCompositionVisual2 visual)
    {
        public readonly IDCompositionVisual2 Visual = visual;
        public IDCompositionSurface? Surface;
        public string Text = "";
        public double Scale;
        public float W, H;
        public bool Front;
        /// The width it was made to fit (0: as wide as its text).
        public float Fit;
        public IconImage? Icon;
    }

    readonly nint dest;
    readonly ID3D11Device d3d;
    readonly IDCompositionDesktopDevice device;
    readonly IDCompositionTarget target;
    readonly IDCompositionVisual2 root, desktop, canvas, titles;
    Picture? desktopPicture;
    readonly Strips accent, marqueeFill, guides;
    /// The stack tabs' opacity as they fade out, shared by all of them.
    readonly IDCompositionEffectGroup tabFade, decorationsHidden;
    readonly Dictionary<nint, MarkLayer> markLayers = new();
    IDCompositionSurface? closeSurface;
    double closeScale;
    readonly IDCompositionVisual2 minimapVisual;
    IDCompositionSurface? minimapSurface;
    (int w, int h) minimapSize;
    MinimapDrawing? minimapDrawn;
    readonly IDCompositionVisual2 desktopsVisual;
    IDCompositionSurface? desktopsSurface;
    (int w, int h) desktopsSize;
    DesktopsDrawing? desktopsDrawn;
    byte[]? tileThumb;
    (int w, int h) tileThumbSize;
    readonly ID2D1Factory1 d2dFactory;
    readonly ID2D1Device d2dDevice;
    readonly ID2D1DeviceContext d2d;
    readonly IDWriteFactory dwrite;
    readonly Dictionary<double, IDWriteTextFormat> fonts = new();
    readonly Dictionary<nint, Picture> pictures = new();
    readonly Dictionary<nint, Title> titleVisuals = new();
    readonly Dictionary<long, Title> groupTitles = new();
    readonly Dictionary<long, IDCompositionVisual2> groupTrash = new();
    /// Each group's tint and border, in its own colour.
    readonly Dictionary<long, (Strips fill, Strips edge)> groupBoxes = new();
    readonly Dictionary<long, Swatch> groupSwatches = new();
    readonly IDCompositionVisual2 groupBoxLayer, groupLabels, buttonLayer;
    readonly IDCompositionVisual2 wheelLayer, wheelVisual, wheelMarker;
    IDCompositionSurface? wheelSurface, markerSurface;
    int wheelSize, markerSize;
    Title? newGroupTitle;
    readonly IDCompositionVisual2 treeVisual;
    IDCompositionSurface? treeSurface;
    (int w, int h) treeSize;
    TreeDrawing? treeDrawn;
    IDCompositionSurface? trashSurface;
    double trashScale;
    readonly bool failForTesting;
    (double x, double y, double s)? lastCamera;
    bool commitFailed;

    public string Name => "shared-visuals";
    public int Count => pictures.Count;
    public bool DrawsDecorations => true;

    /// When DWM restarts (it does after a graphics driver reset or update) every shared
    /// thumbnail and the DirectComposition device go with it; nothing draws until they're made
    /// again. The Mac's equivalent is its capture service restarting.
    public bool Lost
    {
        get
        {
            if (commitFailed) return true;
            try { return d3d.DeviceRemovedReason.Failure; }
            catch { return true; }
        }
    }

    /// `failForTesting`: every picture fails, to exercise the fallback (`pictures shared-fail`).
    public SharedVisualPictures(nint destination, bool failForTesting = false)
    {
        dest = destination;
        this.failForTesting = failForTesting;
        d3d = CreateD3D(out string driver);
        using (var dxgi = d3d.QueryInterface<IDXGIDevice>())
        {
            Marshal.ThrowExceptionForHR(DCompositionCreateDevice3(dxgi.NativePointer, typeof(IDCompositionDesktopDevice).GUID, out nint p));
            device = new IDCompositionDesktopDevice(p);
            d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded, DebugLevel.None);
            d2dDevice = d2dFactory.CreateDevice(dxgi);
        }
        d2d = d2dDevice.CreateDeviceContext();
        d2d.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale; // ClearType needs an opaque background
        dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);

        // Topmost: the tree draws over the overlay's own (GDI) backdrop. Bottom to top: the
        // pictures (each with its ring and close button), the selection box and marquee, titles,
        // snapping guides.
        device.CreateTargetForHwnd(dest, true, out target);
        device.CreateVisual(out root);
        target.SetRoot(root);
        device.CreateVisual(out desktop);
        root.AddVisual(desktop, false, null);
        // Groups' boxes: over the desktop, under the pictures, so a preview is never tinted.
        device.CreateVisual(out groupBoxLayer);
        root.AddVisual(groupBoxLayer, false, null);
        device.CreateVisual(out canvas);
        canvas.SetBitmapInterpolationMode(BitmapInterpolationMode.Linear);
        root.AddVisual(canvas, false, null);
        // Their names, colour and delete buttons: over the pictures, so a window can't hide them.
        device.CreateVisual(out groupLabels);
        root.AddVisual(groupLabels, false, null);
        device.CreateEffectGroup(out tabFade);
        device.CreateEffectGroup(out decorationsHidden);
        decorationsHidden.SetOpacity(0f);
        accent = new Strips(this, 76 / 255f, 154 / 255f, 1f);
        marqueeFill = new Strips(this, 76 / 255f, 154 / 255f, 1f, 0.12f);
        device.CreateVisual(out titles);
        root.AddVisual(titles, false, null);
        guides = new Strips(this, 1f, 0.22f, 0.37f);
        device.CreateVisual(out minimapVisual);
        root.AddVisual(minimapVisual, false, null);
        device.CreateVisual(out desktopsVisual);
        root.AddVisual(desktopsVisual, false, null);
        device.CreateVisual(out treeVisual);
        root.AddVisual(treeVisual, false, null);
        device.CreateVisual(out buttonLayer);
        root.AddVisual(buttonLayer, false, null);
        device.CreateVisual(out wheelLayer);
        wheelLayer.SetBitmapInterpolationMode(BitmapInterpolationMode.Linear);
        root.AddVisual(wheelLayer, false, null);
        device.CreateVisual(out wheelVisual);
        device.CreateVisual(out wheelMarker);
        device.Commit();
        Log.Info($"pictures: shared visuals (D3D11 {driver})");
    }

    static ID3D11Device CreateD3D(out string driver)
    {
        const uint bgraSupport = 0x20;
        foreach (var (type, name) in new[] { (1, "hardware"), (5, "WARP") })
        {
            if (D3D11CreateDevice(0, type, 0, bgraSupport, null, 0, 7, out nint d, out int level, out nint context) != 0) continue;
            Marshal.Release(context);
            driver = $"{name}, feature level 0x{level:X}";
            return new ID3D11Device(d);
        }
        throw new InvalidOperationException("no D3D11 device");
    }

    // MARK: Pictures

    /// The pictures from the last time the canvas was up are still here (Detach keeps them): DWM
    /// takes a while over a new one the first time it shows it, and zooming out shows them all at
    /// once, which stuttered. Those of windows that went are dropped, those of windows that changed
    /// size are made again, the rest go back into the stack in its order, at their windows' spots.
    public bool Attach(IEnumerable<CanvasWindow> bottomToTop)
    {
        var stack = bottomToTop.ToList();
        var wanted = stack.Select(w => w.Hwnd).ToHashSet();
        foreach (nint gone in pictures.Keys.Where(h => !wanted.Contains(h)).ToList()) Drop(gone);
        foreach (var w in stack)
            if (pictures.TryGetValue(w.Hwnd, out var p) && !p.Fits(w)) Drop(w.Hwnd);
        int kept = pictures.Count;
        canvas.RemoveAllVisuals();
        int tried = 0;
        foreach (var w in stack)
        {
            if (pictures.TryGetValue(w.Hwnd, out var p))
            {
                p.Visual.SetOffsetX((float)w.Frame.X);
                p.Visual.SetOffsetY((float)w.Frame.Y);
                canvas.AddVisual(p.Visual, false, null);
                canvas.AddVisual(markLayers[w.Hwnd].Container, true, p.Visual);
                continue;
            }
            tried++;
            if (!Add(w) && pictures.Count == 0 && tried >= 3) break; // not working here: stop early
        }
        lastCamera = null;
        if (kept > 0) Log.Info($"pictures: {kept} kept from last time, {pictures.Count - kept} new");
        return tried == 0 || pictures.Count > 0;
    }

    /// Drops a picture and its marks, by window.
    void Drop(nint hwnd)
    {
        if (markLayers.Remove(hwnd, out var layer))
        {
            canvas.RemoveVisual(layer.Container);
            Dispose(layer);
        }
        if (!pictures.Remove(hwnd, out var p)) return;
        canvas.RemoveVisual(p.Visual);
        device.Commit(); // out of the tree before its thumbnail goes, as in Remove
        Dwm.DwmUnregisterThumbnail(p.Thumb);
        p.Visual.Dispose();
    }

    public bool Add(CanvasWindow w)
    {
        if (pictures.ContainsKey(w.Hwnd)) return true;
        if (Create(w) is not Picture p) return false;
        canvas.AddVisual(p.Visual, false, null); // no reference visual, not "above": on top of its siblings
        pictures[w.Hwnd] = p;
        var layer = NewMarkLayer();
        canvas.AddVisual(layer.Container, true, p.Visual);
        markLayers[w.Hwnd] = layer;
        return true;
    }

    MarkLayer NewMarkLayer()
    {
        device.CreateVisual(out var container);
        var ring = new IDCompositionVisual2[4];
        for (int i = 0; i < 4; i++)
        {
            device.CreateVisual(out ring[i]);
            ring[i].SetContent(accent.Colour);
            ring[i].SetBorderMode(BorderMode.Hard);
        }
        device.CreateVisual(out var close);
        return new MarkLayer(container, ring, close);
    }

    static void Dispose(MarkLayer layer)
    {
        layer.Container.RemoveAllVisuals();
        foreach (var t in layer.Tabs.Values) Dispose(t);
        foreach (var v in layer.Ring) v.Dispose();
        layer.Close.Dispose();
        layer.Container.Dispose();
    }

    /// A new visual at the new size, put where the old one was in the stack (under its marks).
    public void Resized(CanvasWindow w)
    {
        if (!pictures.TryGetValue(w.Hwnd, out var old) || Create(w) is not Picture p) return;
        canvas.AddVisual(p.Visual, true, old.Visual);
        canvas.RemoveVisual(old.Visual);
        Dwm.DwmUnregisterThumbnail(old.Thumb);
        old.Visual.Dispose();
        pictures[w.Hwnd] = p;
    }

    /// The window's picture: its visible frame, at its canvas spot.
    Picture? Create(CanvasWindow w)
    {
        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = Dwm.DWM_TNP_RECTDESTINATION | Dwm.DWM_TNP_RECTSOURCE | Dwm.DWM_TNP_VISIBLE |
                      Dwm.DWM_TNP_OPACITY | Dwm.DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new RECT(0, 0, (int)Math.Round(w.Frame.W), (int)Math.Round(w.Frame.H)),
            rcSource = w.SourceRect,
            opacity = 255,
            fVisible = 1,
        };
        int hr;
        nint visual = 0, thumb = 0;
        if (failForTesting) hr = unchecked((int)0x80004005);
        else try { hr = DwmpCreateSharedThumbnailVisual(dest, w.Hwnd, 2, ref props, device.NativePointer, out visual, out thumb); }
        catch (EntryPointNotFoundException) { Log.Info("pictures: this Windows has no DwmpCreateSharedThumbnailVisual"); return null; }
        if (hr != 0 || visual == 0) { Log.Info($"pictures: shared visual for {w} failed 0x{hr:X8}"); return null; }
        var v = new IDCompositionVisual2(visual);
        v.SetOffsetX((float)w.Frame.X);
        v.SetOffsetY((float)w.Frame.Y);
        return new Picture(v, thumb, PictureSize(w), w.SourceRect);
    }

    public void Remove(CanvasWindow w)
    {
        if (titleVisuals.Remove(w.Hwnd, out var t)) Dispose(t);
        if (markLayers.Remove(w.Hwnd, out var layer))
        {
            canvas.RemoveVisual(layer.Container);
            Dispose(layer);
        }
        if (!pictures.Remove(w.Hwnd, out var p)) return;
        canvas.RemoveVisual(p.Visual);
        device.Commit();
        Dwm.DwmUnregisterThumbnail(p.Thumb);
        p.Visual.Dispose();
    }

    public void Raise(CanvasWindow w)
    {
        if (!pictures.TryGetValue(w.Hwnd, out var p)) return;
        canvas.RemoveVisual(p.Visual);
        canvas.AddVisual(p.Visual, false, null);
        if (markLayers.TryGetValue(w.Hwnd, out var layer))
        {
            canvas.RemoveVisual(layer.Container);
            canvas.AddVisual(layer.Container, true, p.Visual);
        }
        device.Commit();
    }

    /// As the Mac's canvas opens onto a fresh picture of the desktop: what's under the windows
    /// stays as it was at 100% (icons, and the desktop picture however Windows fits it), not the
    /// backdrop's copy of the picture.
    public void SetDesktop(RECT? screen)
    {
        if (desktopPicture is Picture old)
        {
            desktop.RemoveAllVisuals();
            Dwm.DwmUnregisterThumbnail(old.Thumb);
            old.Visual.Dispose();
            desktopPicture = null;
        }
        nint shell = User32.FindWindow("Progman", null);
        if (screen is not RECT s || shell == 0 || !User32.GetWindowRect(shell, out var r)) return;
        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = Dwm.DWM_TNP_RECTDESTINATION | Dwm.DWM_TNP_RECTSOURCE | Dwm.DWM_TNP_VISIBLE | Dwm.DWM_TNP_OPACITY,
            rcDestination = new RECT(0, 0, s.Width, s.Height),
            rcSource = new RECT(s.Left - r.Left, s.Top - r.Top, s.Right - r.Left, s.Bottom - r.Top),
            opacity = 255,
            fVisible = 1,
        };
        int hr = DwmpCreateSharedThumbnailVisual(dest, shell, 2, ref props, device.NativePointer, out nint visual, out nint thumb);
        if (hr != 0 || visual == 0) { Log.Info($"pictures: no picture of the desktop (0x{hr:X8})"); return; }
        var v = new IDCompositionVisual2(visual);
        desktop.AddVisual(v, false, null);
        desktopPicture = new Picture(v, thumb);
    }

    /// The overlay is down. The pictures stay, with nothing to show them, for next time (see Attach);
    /// so do the titles, whose surfaces are drawn again only when the text changes.
    public void Detach()
    {
        SetDesktop(null);
        titles.RemoveAllVisuals();
        accent.Set([]);
        marqueeFill.Set([]);
        guides.Set([]);
        device.Commit();
    }

    /// Every picture gone (Dispose).
    void DropAll()
    {
        canvas.RemoveAllVisuals();
        device.Commit();
        foreach (var p in pictures.Values)
        {
            Dwm.DwmUnregisterThumbnail(p.Thumb);
            p.Visual.Dispose();
        }
        pictures.Clear();
        foreach (var layer in markLayers.Values) Dispose(layer);
        markLayers.Clear();
    }

    /// One transform for the whole canvas; nothing else changes as the camera moves.
    public void Layout(IReadOnlyList<CanvasWindow> windows, Camera camera, RectD view)
    {
        var key = (camera.Origin.X, camera.Origin.Y, camera.Scale);
        if (lastCamera == key) return;
        float s = (float)camera.Scale;
        canvas.SetTransform(new Matrix3x2(s, 0, 0, s, (float)(-camera.Origin.X * camera.Scale), (float)(-camera.Origin.Y * camera.Scale)));
        lastCamera = key;
    }

    public void Moved(CanvasWindow w, Camera camera, RectD view)
    {
        if (!pictures.TryGetValue(w.Hwnd, out var p)) return;
        p.Visual.SetOffsetX((float)w.Frame.X);
        p.Visual.SetOffsetY((float)w.Frame.Y);
    }

    /// One effect, opacity 0, on each of their layers: their surfaces stay, so they're composed (and
    /// paid for) while hidden.
    public void HideDecorations(bool hidden)
    {
        foreach (var layer in new[] { groupBoxLayer, groupLabels, titles, minimapVisual, desktopsVisual, treeVisual, buttonLayer, wheelLayer })
            layer.SetEffect(hidden ? decorationsHidden : null);
    }

    public void Present()
    {
        var commit = device.Commit();
        if (commit.Failure)
        {
            if (!commitFailed) Log.Info($"pictures: commit failed {commit}");
            commitFailed = true;
        }
    }

    public void Flush() => device.WaitForCommitCompletion();

    // MARK: Our UI over the pictures

    public bool SetGuides(IReadOnlyList<(Vec a, Vec b)> lines, double thickness)
    {
        guides.Set(lines.Select(l =>
        {
            // Vertical or horizontal: a thin rect along it, centred on the line.
            double x = Math.Min(l.a.X, l.b.X), y = Math.Min(l.a.Y, l.b.Y);
            double w = Math.Abs(l.b.X - l.a.X), h = Math.Abs(l.b.Y - l.a.Y);
            return w < h ? new RectD(x - thickness / 2, y, thickness, h) : new RectD(x, y - thickness / 2, w, thickness);
        }).ToList());
        return true;
    }

    /// Rings 2 px outside their pictures (3 px at 100% hovered, 4 selected), a 1 px box 8 px
    /// around a selection, the marquee (filled at 12%, outlined), close buttons. All in the accent
    /// blue, as strips.
    public void SetMarks(Marks marks, double scale)
    {
        var rects = new List<RectD>();
        tabFade.SetOpacity((float)marks.TabOpacity);
        double gap = Math.Max(1, Math.Round(2 * scale)), thin = Math.Max(1, Math.Round(scale));
        if (marks.SelectionBox is RectD box) Outline(rects, box.Inset(-8 * scale, -8 * scale), thin);
        if (marks.Marquee is RectD m) Outline(rects, m, thin);
        accent.Set(rects);
        marqueeFill.Set(marks.Marquee is RectD fill ? [fill] : []);

        // Each window's ring and close button go in its mark layer, in canvas coordinates: worked
        // out in view pixels (so thicknesses stay whole pixels), then taken back through the camera.
        if (marks.Closes.Count > 0 && (closeSurface == null || closeScale != scale)) DrawClose(scale);
        var (ox, oy, s) = lastCamera ?? (0, 0, 1);
        RectD ToCanvas(RectD v) => new(v.X / s + ox, v.Y / s + oy, v.W / s, v.H / s);
        var rings = marks.Rings.ToDictionary(r => r.hwnd);
        var closes = marks.Closes.ToDictionary(c => c.hwnd);
        foreach (var (hwnd, layer) in markLayers)
        {
            bool ring = rings.TryGetValue(hwnd, out var r), close = closes.TryGetValue(hwnd, out var c);
            var ownTabs = marks.Tabs.Where(t => t.Layer == hwnd).ToList();
            if (!ring && !close && ownTabs.Count == 0 && !layer.Showing) continue;
            layer.Container.RemoveAllVisuals();
            layer.Showing = ring || close || ownTabs.Count > 0;
            if (ring)
            {
                var strips = new List<RectD>(4);
                double t = Math.Max(2, Math.Round((r.selected ? 4 : 3) * scale));
                Outline(strips, r.picture.Inset(-(gap + t), -(gap + t)), t);
                for (int i = 0; i < 4; i++)
                {
                    var cr = ToCanvas(strips[i]);
                    layer.Ring[i].SetTransform(new Matrix3x2((float)cr.W, 0, 0, (float)cr.H, (float)cr.X, (float)cr.Y));
                    layer.Container.AddVisual(layer.Ring[i], false, null);
                }
            }
            if (close)
            {
                // Drawn at the screen's pixel size: scaled by the inverse of the camera's zoom.
                var cv = Marks.CloseRect(c.picture, scale);
                layer.Close.SetContent(closeSurface);
                layer.Close.SetTransform(new Matrix3x2((float)(1 / s), 0, 0, (float)(1 / s),
                    (float)(Math.Round(cv.X) / s + ox), (float)(Math.Round(cv.Y) / s + oy)));
                layer.Container.AddVisual(layer.Close, false, null);
            }
            foreach (var tab in ownTabs)
            {
                if (!layer.Tabs.TryGetValue(tab.Hwnd, out var t))
                {
                    device.CreateVisual(out var v);
                    layer.Tabs[tab.Hwnd] = t = new Title(v);
                }
                float fit = (float)tab.Rect.W;
                if (t.Text != tab.Text || t.Scale != scale || t.Front != tab.Front || t.Fit != fit || t.Icon != tab.Icon)
                    Draw(t, tab.Text, scale, tab.Front, fit, tab.Icon, tab.FitButton);
                // Like the close button: at the screen's pixel size, whatever the camera's zoom.
                t.Visual.SetTransform(new Matrix3x2((float)(1 / s), 0, 0, (float)(1 / s),
                    (float)(tab.Rect.X / s + ox), (float)(tab.Rect.Y / s + oy)));
                // Only during a fade, so a tab that isn't fading is drawn just as before.
                t.Visual.SetEffect(marks.TabOpacity < 1 ? tabFade : null);
                layer.Container.AddVisual(t.Visual, false, null);
            }
            foreach (nint gone in layer.Tabs.Keys.Where(h => ownTabs.All(t => t.Hwnd != h)).ToList())
            {
                layer.Tabs.Remove(gone, out var old);
                Dispose(old!);
            }
        }
    }

    /// The fade from light to solid in this many steps (see SetGroups).
    const int SoliditySteps = 32;

    /// Each group's tinted box and border, its name and delete button; the New Group button.
    /// `solidity`, 0 to 1: how strong the tint and border are (see Canvas.GroupsTakeClicks).
    public void SetGroups(IReadOnlyList<GroupDrawing> groups, RectD? newGroupButton, string buttonText, double scale, double solidity)
    {
        // In steps, so a fade goes through the same few colours each time, which Strips keeps.
        solidity = Math.Round(solidity * SoliditySteps) / SoliditySteps;
        double thin = Math.Max(1, Math.Round(scale * (1 + solidity)));
        groupLabels.RemoveAllVisuals();
        if (groups.Count > 0 && (trashSurface == null || trashScale != scale)) DrawTrash(groups[0].Trash.H, scale);
        foreach (var g in groups)
        {
            if (!groupTitles.TryGetValue(g.Id, out var t))
            {
                device.CreateVisual(out var v);
                groupTitles[g.Id] = t = new Title(v);
                device.CreateVisual(out var can);
                groupTrash[g.Id] = can;
                groupBoxes[g.Id] = (new Strips(this, 0, 0, 0, 0, groupBoxLayer), new Strips(this, 0, 0, 0, 0, groupBoxLayer));
                device.CreateVisual(out var circle);
                groupSwatches[g.Id] = new Swatch(circle);
            }
            // From a light tint to a dark shade, which shows on any wallpaper.
            var (fill, edge) = groupBoxes[g.Id];
            var fillTint = GroupColours.Fill(g.Colour, solidity, g.Hovered);
            fill.SetColour(fillTint.R, fillTint.G, fillTint.B, fillTint.A);
            fill.Set([g.Box]);
            var edgeTint = GroupColours.Edge(g.Colour, solidity, g.Hovered);
            edge.SetColour(edgeTint.R, edgeTint.G, edgeTint.B, edgeTint.A);
            var edges = new List<RectD>(4);
            Outline(edges, g.Box, thin);
            edge.Set(edges);
            var swatch = groupSwatches[g.Id];
            if (swatch.Colour != g.Colour || swatch.Scale != scale || swatch.Size != (int)Math.Round(g.Swatch.H)) DrawSwatch(swatch, g.Colour, g.Swatch.H, scale);
            swatch.Visual.SetOffsetX((float)Math.Round(g.Swatch.X));
            swatch.Visual.SetOffsetY((float)Math.Round(g.Swatch.Y));
            groupLabels.AddVisual(swatch.Visual, false, null);
            if (t.Text != g.Name || t.Scale != scale) Draw(t, g.Name, scale);
            t.Visual.SetOffsetX((float)Math.Round(g.Label.X));
            t.Visual.SetOffsetY((float)Math.Round(g.Label.Y));
            t.Visual.SetClip(new Vortice.RawRectF(0, 0, Math.Min(t.W, (float)g.Label.W), t.H));
            groupLabels.AddVisual(t.Visual, false, null);
            var trash = groupTrash[g.Id];
            trash.SetContent(trashSurface);
            trash.SetOffsetX((float)Math.Round(g.Trash.X));
            trash.SetOffsetY((float)Math.Round(g.Trash.Y));
            groupLabels.AddVisual(trash, false, null);
        }
        // Groups out of view or deleted: their labels are made again when wanted.
        foreach (long id in groupTitles.Keys.Where(id => groups.All(g => g.Id != id)).ToList())
        {
            groupTitles.Remove(id, out var gone);
            Dispose(gone!);
            groupTrash.Remove(id, out var can);
            can!.Dispose();
            groupBoxes.Remove(id, out var box);
            box.fill.Dispose();
            box.edge.Dispose();
            groupSwatches.Remove(id, out var circle);
            circle!.Dispose();
        }
        buttonLayer.RemoveAllVisuals();
        if (newGroupButton is RectD b)
        {
            if (newGroupTitle == null)
            {
                device.CreateVisual(out var v);
                newGroupTitle = new Title(v);
            }
            if (newGroupTitle.Text != buttonText || newGroupTitle.Scale != scale) Draw(newGroupTitle, buttonText, scale);
            newGroupTitle.Visual.SetOffsetX((float)Math.Round(b.X));
            newGroupTitle.Visual.SetOffsetY((float)Math.Round(b.Y));
            buttonLayer.AddVisual(newGroupTitle.Visual, false, null);
        }
    }

    /// The groups panel, drawn like the minimap: a surface of its own, redrawn only when it changes.
    public bool SetTreePanel(TreeDrawing? tree, double scale)
    {
        if (tree is not TreeDrawing d)
        {
            if (treeDrawn != null) { treeVisual.SetContent(null); treeDrawn = null; }
            return true;
        }
        if (treeDrawn is TreeDrawing last && last.Same(d)) return true;
        int w = (int)Math.Ceiling(d.Panel.W), h = Math.Max(1, (int)Math.Ceiling(d.Panel.H));
        if (treeSurface == null || treeSize != (w, h))
        {
            treeSurface?.Dispose();
            device.CreateSurface((uint)w, (uint)h, Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied, out var created);
            treeSurface = created;
            treeSize = (w, h);
        }
        float k = (float)scale;
        System.Drawing.RectangleF Local(RectD r) => new((float)(r.X - d.Panel.X), (float)(r.Y - d.Panel.Y), (float)r.W, (float)r.H);
        Vortice.RawRectF Raw(double x, double y, double right, double bottom) => new((float)(x - d.Panel.X), (float)(y - d.Panel.Y), (float)(right - d.Panel.X), (float)(bottom - d.Panel.Y));
        using (var dxgi = treeSurface.BeginDraw<IDXGISurface>(null, out Vortice.Mathematics.Int2 offset))
        using (var bitmap = d2d.CreateBitmapFromDxgiSurface(dxgi, new BitmapProperties1(
                   new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96,
                   BitmapOptions.Target | BitmapOptions.CannotDraw)))
        using (var panel = d2d.CreateSolidColorBrush(d.Dark ? new Color4(0.12f, 0.12f, 0.13f, 0.94f) : new Color4(0.97f, 0.97f, 0.98f, 0.94f)))
        using (var edge = d2d.CreateSolidColorBrush(d.Dark ? new Color4(1f, 1f, 1f, 0.12f) : new Color4(0f, 0f, 0f, 0.1f)))
        using (var field = d2d.CreateSolidColorBrush(d.Dark ? new Color4(1f, 1f, 1f, 0.09f) : new Color4(0f, 0f, 0f, 0.06f)))
        using (var mark = d2d.CreateSolidColorBrush(new Color4(76 / 255f, 154 / 255f, 1f, d.Dark ? 0.3f : 0.2f)))
        using (var ink = d2d.CreateSolidColorBrush(d.Dark ? new Color4(1f, 1f, 1f, 0.95f) : new Color4(0f, 0f, 0f, 0.85f)))
        using (var muted = d2d.CreateSolidColorBrush(d.Dark ? new Color4(1f, 1f, 1f, 0.55f) : new Color4(0f, 0f, 0f, 0.5f)))
        using (var heading = dwrite.CreateTextFormat("Segoe UI", FontWeight.SemiBold, Vortice.DirectWrite.FontStyle.Normal, 13 * k))
        using (var plain = dwrite.CreateTextFormat("Segoe UI", FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, 13 * k))
        using (var count = dwrite.CreateTextFormat("Segoe UI", FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, 12 * k))
        using (var glyph = dwrite.CreateTextFormat("Segoe MDL2 Assets", FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, 10 * k))
        using (var headingCut = dwrite.CreateEllipsisTrimmingSign(heading))
        using (var plainCut = dwrite.CreateEllipsisTrimmingSign(plain))
        {
            foreach (var (format, cut) in new[] { (heading, headingCut), (plain, plainCut) })
            {
                format.WordWrapping = WordWrapping.NoWrap;
                format.ParagraphAlignment = ParagraphAlignment.Center;
                format.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, cut);
            }
            count.TextAlignment = TextAlignment.Trailing;
            count.ParagraphAlignment = ParagraphAlignment.Center;
            glyph.TextAlignment = TextAlignment.Center;
            glyph.ParagraphAlignment = ParagraphAlignment.Center;
            d2d.Target = bitmap;
            d2d.BeginDraw();
            d2d.Transform = Matrix3x2.CreateTranslation(offset.X, offset.Y);
            d2d.PushAxisAlignedClip(new Vortice.RawRectF(0, 0, w, h), AntialiasMode.Aliased);
            d2d.Clear(new Color4(0, 0, 0, 0));
            float corner = 12 * k;
            var outline = new System.Drawing.RectangleF(0.5f, 0.5f, (float)d.Panel.W - 1, (float)d.Panel.H - 1);
            d2d.FillRoundedRectangle(new RoundedRectangle(outline, corner, corner), panel);
            d2d.DrawRoundedRectangle(new RoundedRectangle(outline, corner, corner), edge, 1);
            float fieldCorner = 6 * k;
            d2d.FillRoundedRectangle(new RoundedRectangle(Local(d.Search), fieldCorner, fieldCorner), field);
            d2d.DrawText("\uE721", glyph, Raw(d.Search.X + 2 * k, d.Search.Y, d.Search.X + 26 * k, d.Search.Bottom), muted);
            var typed = GroupTree.SearchTextRect(d.Search, k);
            if (!d.Typing) d2d.DrawText(d.Query.Length > 0 ? d.Query : TreeDrawing.Placeholder, plain, Raw(typed.X, typed.Y, typed.Right, typed.Bottom), d.Query.Length > 0 ? ink : muted);
            d2d.PushAxisAlignedClip(Raw(d.List.X, d.List.Y, d.List.Right, d.List.Bottom), AntialiasMode.Aliased);
            // Each group's rows, its header and apps, as one block of its colour, a little apart from the next.
            float apart = (float)Math.Round(k);
            System.Drawing.RectangleF Inset(System.Drawing.RectangleF r) => new(r.X, r.Y + apart, r.Width, r.Height - 2 * apart);
            ID2D1SolidColorBrush Brush(Tint t) => d2d.CreateSolidColorBrush(new Color4(t.R, t.G, t.B, t.A));
            for (int i = 0; i < d.Rows.Count;)
            {
                int end = i + 1;
                while (end < d.Rows.Count && d.Rows[end].Group == d.Rows[i].Group) end++;
                if (d.Rows[i].Colour is GroupColour colour)
                {
                    var block = Local(d.Rows[i].Rect with { H = d.Rows[end - 1].Rect.Bottom - d.Rows[i].Rect.Y });
                    using var tint = Brush(GroupColours.TreeBlock(colour, d.Dark));
                    d2d.FillRoundedRectangle(new RoundedRectangle(Inset(block), fieldCorner, fieldCorner), tint);
                }
                i = end;
            }
            foreach (var row in d.Rows)
            {
                var r = row.Rect;
                if (row.Highlighted && row.Colour is GroupColour own)
                {
                    using var stronger = Brush(GroupColours.TreeHighlight(own, d.Dark));
                    d2d.FillRoundedRectangle(new RoundedRectangle(Inset(Local(r)), fieldCorner, fieldCorner), stronger);
                }
                else if (row.Highlighted) d2d.FillRoundedRectangle(new RoundedRectangle(Local(r), fieldCorner, fieldCorner), mark);
                double x = r.X + row.Depth * GroupTree.Indent * k;
                if (row.Expandable)
                    d2d.DrawText(row.Expanded ? "\uE70D" : "\uE76C", glyph, Raw(x, r.Y, x + 24 * k, r.Bottom), muted);
                if (row.Icon is IconImage icon)
                {
                    // In the space a group's arrow has, so names line up.
                    DrawIcon(icon, (float)Math.Round(x + 4 * k - d.Panel.X), (float)Math.Round(r.Y + (r.H - icon.Size) / 2 - d.Panel.Y));
                }
                double right = r.Right - (row.Count.Length > 0 ? 34 : 8) * k;
                d2d.DrawText(row.Text, row.Heading ? heading : plain, Raw(x + 24 * k, r.Y, right, r.Bottom), ink);
                if (row.Count.Length > 0) d2d.DrawText(row.Count, count, Raw(right, r.Y, r.Right - 8 * k, r.Bottom), muted);
            }
            if (d.Empty.Length > 0) d2d.DrawText(d.Empty, plain, Raw(d.List.X + 10 * k, d.List.Y, d.List.Right - 10 * k, d.List.Y + GroupTree.RowHeight * k), muted);
            d2d.PopAxisAlignedClip();
            d2d.PopAxisAlignedClip();
            d2d.EndDraw();
            d2d.Target = null;
        }
        treeSurface.EndDraw();
        treeVisual.SetContent(treeSurface);
        treeVisual.SetOffsetX((float)Math.Round(d.Panel.X));
        treeVisual.SetOffsetY((float)Math.Round(d.Panel.Y));
        treeDrawn = d;
        return true;
    }

    IDWriteTextFormat TitleFont(double scale)
    {
        if (fonts.TryGetValue(scale, out var font)) return font;
        font = dwrite.CreateTextFormat("Segoe UI", FontWeight.SemiBold, Vortice.DirectWrite.FontStyle.Normal, (float)(13 * scale));
        font.WordWrapping = WordWrapping.NoWrap;
        using var ellipsis = dwrite.CreateEllipsisTrimmingSign(font);
        font.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, ellipsis);
        return fonts[scale] = font;
    }

    /// With the font and rounding Draw uses, so that a label's rectangle (the text and Groups.LabelPad
    /// on each side) comes out as wide as its pill and what sits beside it stays close.
    public double? MeasureText(string text, double scale)
    {
        using var layout = dwrite.CreateTextLayout(text, TitleFont(scale), (float)(4000 * scale), (float)Math.Round(22 * scale));
        return Math.Ceiling(layout.Metrics.Width + 2 * Math.Round(10 * scale)) - 2 * Groups.LabelPad * scale;
    }

    /// The circle by a group's label, filled with its colour, with the delete button's dark for a
    /// rim so that it shows on any wallpaper.
    void DrawSwatch(Swatch swatch, GroupColour colour, double height, double scale)
    {
        int size = (int)Math.Round(height);
        if (swatch.Surface == null || swatch.Size != size)
        {
            swatch.Surface?.Dispose();
            device.CreateSurface((uint)size, (uint)size, Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied, out var made);
            swatch.Surface = made;
            swatch.Size = size;
            swatch.Visual.SetContent(made);
        }
        float middle = size / 2f, rim = (float)(1.5 * scale);
        using (var dxgi = swatch.Surface.BeginDraw<IDXGISurface>(null, out Vortice.Mathematics.Int2 offset))
        using (var bitmap = d2d.CreateBitmapFromDxgiSurface(dxgi, new BitmapProperties1(
                   new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96,
                   BitmapOptions.Target | BitmapOptions.CannotDraw)))
        using (var dark = d2d.CreateSolidColorBrush(new Color4(32 / 255f, 32 / 255f, 34 / 255f, 1f)))
        using (var paint = d2d.CreateSolidColorBrush(new Color4(colour.R / 255f, colour.G / 255f, colour.B / 255f, 1f)))
        {
            d2d.Target = bitmap;
            d2d.BeginDraw();
            d2d.Transform = Matrix3x2.CreateTranslation(offset.X, offset.Y);
            d2d.PushAxisAlignedClip(new Vortice.RawRectF(0, 0, size, size), AntialiasMode.Aliased);
            d2d.Clear(new Color4(0, 0, 0, 0));
            d2d.FillEllipse(new Ellipse(new Vector2(middle, middle), middle, middle), dark);
            d2d.FillEllipse(new Ellipse(new Vector2(middle, middle), middle - rim, middle - rim), paint);
            d2d.PopAxisAlignedClip();
            d2d.EndDraw();
            d2d.Target = null;
        }
        swatch.Surface.EndDraw();
        swatch.Colour = colour;
        swatch.Scale = scale;
    }

    /// The colour wheel, which is scaled from its full size as it grows, and once it's grown the
    /// ring that marks the group's colour on it.
    public void SetColourWheel(ColourWheelDrawing? wheel, double scale)
    {
        wheelLayer.RemoveAllVisuals();
        if (wheel is not ColourWheelDrawing d) return;
        int size = (int)Math.Round(d.Rect.W);
        if (wheelSurface == null || wheelSize != size) DrawWheel(size, scale);
        double grown = d.Shown.W / d.Rect.W;
        wheelVisual.SetTransform(new Matrix3x2((float)grown, 0, 0, (float)grown, (float)d.Shown.X, (float)d.Shown.Y));
        wheelLayer.AddVisual(wheelVisual, false, null);
        if (d.Shown != d.Rect) return;
        int ring = (int)Math.Round(14 * scale);
        if (markerSurface == null || markerSize != ring) DrawWheelMarker(ring, scale);
        wheelMarker.SetOffsetX((float)Math.Round(d.Marker.X - ring / 2.0));
        wheelMarker.SetOffsetY((float)Math.Round(d.Marker.Y - ring / 2.0));
        wheelLayer.AddVisual(wheelMarker, false, null);
    }

    void DrawWheel(int size, double scale)
    {
        wheelSurface?.Dispose();
        device.CreateSurface((uint)size, (uint)size, Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied, out var surface);
        wheelSurface = surface;
        wheelSize = size;
        wheelVisual.SetContent(surface);
        var pixels = GroupColours.WheelPixels(size);
        float middle = size / 2f, rim = (float)(1.5 * scale);
        var pinned = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            using var dxgi = surface.BeginDraw<IDXGISurface>(null, out Vortice.Mathematics.Int2 offset);
            using var bitmap = d2d.CreateBitmapFromDxgiSurface(dxgi, new BitmapProperties1(
                new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96,
                BitmapOptions.Target | BitmapOptions.CannotDraw));
            using var image = d2d.CreateBitmap(new Vortice.Mathematics.SizeI(size, size), pinned.AddrOfPinnedObject(), (uint)(size * 4),
                new BitmapProperties(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
            using var dark = d2d.CreateSolidColorBrush(new Color4(32 / 255f, 32 / 255f, 34 / 255f, 1f));
            d2d.Target = bitmap;
            d2d.BeginDraw();
            d2d.Transform = Matrix3x2.CreateTranslation(offset.X, offset.Y);
            d2d.PushAxisAlignedClip(new Vortice.RawRectF(0, 0, size, size), AntialiasMode.Aliased);
            d2d.Clear(new Color4(0, 0, 0, 0));
            d2d.DrawBitmap(image, new Vortice.RawRectF(0, 0, size, size), 1, Vortice.Direct2D1.BitmapInterpolationMode.Linear, null);
            d2d.DrawEllipse(new Ellipse(new Vector2(middle, middle), middle - rim / 2, middle - rim / 2), dark, rim);
            d2d.PopAxisAlignedClip();
            d2d.EndDraw();
            d2d.Target = null;
            surface.EndDraw();
        }
        finally { pinned.Free(); }
    }

    /// A white ring with a dark edge, which shows on every colour of the wheel.
    void DrawWheelMarker(int size, double scale)
    {
        markerSurface?.Dispose();
        device.CreateSurface((uint)size, (uint)size, Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied, out var surface);
        markerSurface = surface;
        markerSize = size;
        wheelMarker.SetContent(surface);
        float middle = size / 2f, line = (float)(1.5 * scale);
        using (var dxgi = surface.BeginDraw<IDXGISurface>(null, out Vortice.Mathematics.Int2 offset))
        using (var bitmap = d2d.CreateBitmapFromDxgiSurface(dxgi, new BitmapProperties1(
                   new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96,
                   BitmapOptions.Target | BitmapOptions.CannotDraw)))
        using (var dark = d2d.CreateSolidColorBrush(new Color4(32 / 255f, 32 / 255f, 34 / 255f, 1f)))
        using (var white = d2d.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f)))
        {
            d2d.Target = bitmap;
            d2d.BeginDraw();
            d2d.Transform = Matrix3x2.CreateTranslation(offset.X, offset.Y);
            d2d.PushAxisAlignedClip(new Vortice.RawRectF(0, 0, size, size), AntialiasMode.Aliased);
            d2d.Clear(new Color4(0, 0, 0, 0));
            d2d.DrawEllipse(new Ellipse(new Vector2(middle, middle), middle - line, middle - line), dark, line * 2);
            d2d.DrawEllipse(new Ellipse(new Vector2(middle, middle), middle - line, middle - line), white, line);
            d2d.PopAxisAlignedClip();
            d2d.EndDraw();
            d2d.Target = null;
        }
        surface.EndDraw();
    }

    /// The delete button: a dark circle with a white garbage can (a glyph every Windows has).
    void DrawTrash(double height, double scale)
    {
        float size = (float)Math.Round(height);
        device.CreateSurface((uint)size, (uint)size, Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied, out var surface);
        using (var dxgi = surface.BeginDraw<IDXGISurface>(null, out Vortice.Mathematics.Int2 offset))
        using (var bitmap = d2d.CreateBitmapFromDxgiSurface(dxgi, new BitmapProperties1(
                   new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96,
                   BitmapOptions.Target | BitmapOptions.CannotDraw)))
        using (var dark = d2d.CreateSolidColorBrush(new Color4(32 / 255f, 32 / 255f, 34 / 255f, 1f)))
        using (var white = d2d.CreateSolidColorBrush(new Color4(240 / 255f, 240 / 255f, 240 / 255f, 1f)))
        using (var glyph = dwrite.CreateTextFormat("Segoe MDL2 Assets", FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, (float)(12 * scale)))
        {
            glyph.TextAlignment = TextAlignment.Center;
            glyph.ParagraphAlignment = ParagraphAlignment.Center;
            d2d.Target = bitmap;
            d2d.BeginDraw();
            d2d.Transform = Matrix3x2.CreateTranslation(offset.X, offset.Y);
            d2d.PushAxisAlignedClip(new Vortice.RawRectF(0, 0, size, size), AntialiasMode.Aliased);
            d2d.Clear(new Color4(0, 0, 0, 0));
            d2d.FillEllipse(new Ellipse(new Vector2(size / 2, size / 2), size / 2, size / 2), dark);
            d2d.DrawText("\uE74D", glyph, new Vortice.RawRectF(0, 0, size, size), white);
            d2d.PopAxisAlignedClip();
            d2d.EndDraw();
            d2d.Target = null;
        }
        surface.EndDraw();
        trashSurface?.Dispose();
        trashSurface = surface;
        trashScale = scale;
    }

    /// Four strips `t` thick along the inside of `o`.
    static void Outline(List<RectD> rects, RectD o, double t)
    {
        rects.Add(new RectD(o.X, o.Y, o.W, t));
        rects.Add(new RectD(o.X, o.Bottom - t, o.W, t));
        rects.Add(new RectD(o.X, o.Y + t, t, o.H - 2 * t));
        rects.Add(new RectD(o.Right - t, o.Y + t, t, o.H - 2 * t));
    }

    /// The minimap, drawn with Direct2D into a surface of its own and redrawn only when what it
    /// shows changes. Colours after the Mac's: marks dark on a light panel, light on a dark one;
    /// the view in the accent colour.
    public bool SetMinimap(MinimapDrawing? minimap, double scale)
    {
        if (minimap is not MinimapDrawing m)
        {
            if (minimapDrawn != null) { minimapVisual.SetContent(null); minimapDrawn = null; }
            return true;
        }
        if (minimapDrawn is MinimapDrawing last && last.Same(m)) return true;
        int w = (int)Math.Ceiling(m.Panel.W), h = (int)Math.Ceiling(m.Panel.H);
        if (minimapSurface == null || minimapSize != (w, h))
        {
            minimapSurface?.Dispose();
            device.CreateSurface((uint)w, (uint)h, Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied, out var created);
            minimapSurface = created;
            minimapSize = (w, h);
        }
        float k = (float)scale;
        System.Drawing.RectangleF Local(RectD r) => new((float)(r.X - m.Panel.X), (float)(r.Y - m.Panel.Y), (float)r.W, (float)r.H);
        static float Radius(RectD r, float lo, float hi, double share) =>
            (float)Math.Min(Math.Min(hi, Math.Max(lo, Math.Min(r.W, r.H) * share)), Math.Min(r.W, r.H) / 2);
        var accent = new Color4(76 / 255f, 154 / 255f, 1f, 1f);
        using (var dxgi = minimapSurface.BeginDraw<IDXGISurface>(null, out Vortice.Mathematics.Int2 offset))
        using (var bitmap = d2d.CreateBitmapFromDxgiSurface(dxgi, new BitmapProperties1(
                   new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96,
                   BitmapOptions.Target | BitmapOptions.CannotDraw)))
        using (var panel = d2d.CreateSolidColorBrush(m.Dark ? new Color4(0.12f, 0.12f, 0.13f, 0.9f) : new Color4(0.97f, 0.97f, 0.98f, 0.9f)))
        using (var edge = d2d.CreateSolidColorBrush(m.Dark ? new Color4(1f, 1f, 1f, 0.12f) : new Color4(0f, 0f, 0f, 0.1f)))
        using (var block = d2d.CreateSolidColorBrush(m.Dark ? new Color4(1f, 1f, 1f, 0.38f) : new Color4(0f, 0f, 0f, 0.2f)))
        using (var seam = d2d.CreateSolidColorBrush(m.Dark ? new Color4(0.12f, 0.12f, 0.13f, 0.5f) : new Color4(0f, 0f, 0f, 0.24f)))
        using (var tint = d2d.CreateSolidColorBrush(new Color4(accent.R, accent.G, accent.B, m.Dark ? 0.14f : 0.1f)))
        using (var ring = d2d.CreateSolidColorBrush(m.FramesAll ? (m.Dark ? new Color4(1f, 1f, 1f, 0.35f) : new Color4(0f, 0f, 0f, 0.28f)) : accent))
        using (var ink = d2d.CreateSolidColorBrush(m.Dark ? new Color4(1f, 1f, 1f, 1f) : new Color4(0f, 0f, 0f, 0.75f)))
        using (var titleFont = dwrite.CreateTextFormat("Segoe UI", FontWeight.SemiBold, Vortice.DirectWrite.FontStyle.Normal, 11 * k))
        using (var zoomFont = dwrite.CreateTextFormat("Segoe UI", FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, 11 * k))
        {
            zoomFont.TextAlignment = TextAlignment.Trailing;
            d2d.Target = bitmap;
            d2d.BeginDraw();
            d2d.Transform = Matrix3x2.CreateTranslation(offset.X, offset.Y);
            d2d.PushAxisAlignedClip(new Vortice.RawRectF(0, 0, w, h), AntialiasMode.Aliased);
            d2d.Clear(new Color4(0, 0, 0, 0));
            float corner = 12 * k;
            var panelRect = new System.Drawing.RectangleF(0.5f, 0.5f, (float)m.Panel.W - 1, (float)m.Panel.H - 1);
            d2d.FillRoundedRectangle(new RoundedRectangle(panelRect, corner, corner), panel);
            d2d.DrawRoundedRectangle(new RoundedRectangle(panelRect, corner, corner), edge, 1);
            if (!m.FramesAll)
            {
                float vr = Radius(m.Viewport, 3 * k, 12 * k, 0.14);
                d2d.FillRoundedRectangle(new RoundedRectangle(Local(m.Viewport), vr, vr), tint);
            }
            foreach (var r in m.Windows)
            {
                float rr = Radius(r, 2 * k, 3 * k, 0.18);
                var rect = new RoundedRectangle(Local(r), rr, rr);
                d2d.FillRoundedRectangle(rect, block);
                d2d.DrawRoundedRectangle(rect, seam, m.Dark ? 0.75f * k : 0.5f * k);
            }
            // The ring stands off the windows inside it: a frame round what you see, not one more
            // (selected) window.
            var outer = m.Viewport.Inset(-Minimap.RingOutset * k, -Minimap.RingOutset * k);
            float outerRadius = Radius(m.Viewport, 3 * k, 12 * k, 0.14) + (float)(Minimap.RingOutset * k);
            d2d.DrawRoundedRectangle(new RoundedRectangle(Local(outer), outerRadius, outerRadius), ring, (m.FramesAll ? 1 : 2) * k);
            d2d.DrawText("Expanse", titleFont, new Vortice.RawRectF(14 * k, 9 * k, 134 * k, 25 * k), ink);
            d2d.DrawText(m.Zoom, zoomFont, new Vortice.RawRectF(w - 74 * k, 9 * k, w - 14 * k, 25 * k), ink);
            d2d.PopAxisAlignedClip();
            d2d.EndDraw();
            d2d.Target = null;
        }
        minimapSurface.EndDraw();
        minimapVisual.SetContent(minimapSurface);
        minimapVisual.SetOffsetX((float)Math.Round(m.Panel.X));
        minimapVisual.SetOffsetY((float)Math.Round(m.Panel.Y));
        minimapDrawn = m;
        return true;
    }

    /// The Desktops panel (see DesktopsPanel), drawn like the minimap: a surface of its own,
    /// redrawn only when it changes. Each tile shows the desktop picture.
    public bool SetDesktops(DesktopsDrawing? desktops, double scale)
    {
        if (desktops is not DesktopsDrawing d)
        {
            if (desktopsDrawn != null) { desktopsVisual.SetContent(null); desktopsDrawn = null; }
            return true;
        }
        if (desktopsDrawn is DesktopsDrawing last && last.Same(d)) return true;
        int w = (int)Math.Ceiling(d.Panel.W), h = (int)Math.Ceiling(d.Panel.H);
        if (desktopsSurface == null || desktopsSize != (w, h))
        {
            desktopsSurface?.Dispose();
            device.CreateSurface((uint)w, (uint)h, Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied, out var created);
            desktopsSurface = created;
            desktopsSize = (w, h);
        }
        float k = (float)scale;
        int tw = d.Tiles.Count > 0 ? (int)Math.Round(d.Tiles[0].W) : 1, th = d.Tiles.Count > 0 ? (int)Math.Round(d.Tiles[0].H) : 1;
        if (tileThumb == null || tileThumbSize != (tw, th))
        {
            tileThumb = Wallpaper.Thumbnail(tw, th);
            tileThumbSize = (tw, th);
        }
        System.Drawing.RectangleF Local(RectD r) => new((float)(r.X - d.Panel.X), (float)(r.Y - d.Panel.Y), (float)r.W, (float)r.H);
        var accent = new Color4(76 / 255f, 154 / 255f, 1f, 1f);
        using (var dxgi = desktopsSurface.BeginDraw<IDXGISurface>(null, out Vortice.Mathematics.Int2 offset))
        using (var bitmap = d2d.CreateBitmapFromDxgiSurface(dxgi, new BitmapProperties1(
                   new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96,
                   BitmapOptions.Target | BitmapOptions.CannotDraw)))
        using (var panel = d2d.CreateSolidColorBrush(d.Dark ? new Color4(0.12f, 0.12f, 0.13f, 0.9f) : new Color4(0.97f, 0.97f, 0.98f, 0.9f)))
        using (var edge = d2d.CreateSolidColorBrush(d.Dark ? new Color4(1f, 1f, 1f, 0.12f) : new Color4(0f, 0f, 0f, 0.1f)))
        using (var well = d2d.CreateSolidColorBrush(d.Dark ? new Color4(1f, 1f, 1f, 0.1f) : new Color4(0f, 0f, 0f, 0.1f)))
        using (var quiet = d2d.CreateSolidColorBrush(d.Dark ? new Color4(1f, 1f, 1f, 0.3f) : new Color4(0f, 0f, 0f, 0.3f)))
        using (var ring = d2d.CreateSolidColorBrush(accent))
        using (var ink = d2d.CreateSolidColorBrush(d.Dark ? new Color4(1f, 1f, 1f, 1f) : new Color4(0f, 0f, 0f, 0.85f)))
        using (var headFont = dwrite.CreateTextFormat("Segoe UI", FontWeight.SemiBold, Vortice.DirectWrite.FontStyle.Normal, 11 * k))
        using (var nameFont = dwrite.CreateTextFormat("Segoe UI", FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, 11 * k))
        using (var boldFont = dwrite.CreateTextFormat("Segoe UI", FontWeight.SemiBold, Vortice.DirectWrite.FontStyle.Normal, 11 * k))
        {
            nameFont.TextAlignment = TextAlignment.Center;
            boldFont.TextAlignment = TextAlignment.Center;
            d2d.Target = bitmap;
            d2d.BeginDraw();
            d2d.Transform = Matrix3x2.CreateTranslation(offset.X, offset.Y);
            d2d.PushAxisAlignedClip(new Vortice.RawRectF(0, 0, w, h), AntialiasMode.Aliased);
            d2d.Clear(new Color4(0, 0, 0, 0));
            float corner = 12 * k;
            var panelRect = new System.Drawing.RectangleF(0.5f, 0.5f, (float)d.Panel.W - 1, (float)d.Panel.H - 1);
            d2d.FillRoundedRectangle(new RoundedRectangle(panelRect, corner, corner), panel);
            d2d.DrawRoundedRectangle(new RoundedRectangle(panelRect, corner, corner), edge, 1);
            d2d.DrawText("Desktops", headFont, new Vortice.RawRectF(14 * k, 9 * k, 200 * k, 25 * k), ink);
            ID2D1Bitmap? picture = null;
            if (tileThumb != null)
            {
                var handle = System.Runtime.InteropServices.GCHandle.Alloc(tileThumb, System.Runtime.InteropServices.GCHandleType.Pinned);
                try
                {
                    picture = d2d.CreateBitmap(new Vortice.Mathematics.SizeI(tw, th), handle.AddrOfPinnedObject(), (uint)(tw * 4),
                        new BitmapProperties(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
                }
                finally { handle.Free(); }
            }
            float tileCorner = Math.Max(2, corner - 14 * k + 6 * k);
            for (int i = 0; i < d.Tiles.Count; i++)
            {
                var t = Local(d.Tiles[i]);
                var rounded = new RoundedRectangle(t, tileCorner, tileCorner);
                if (picture != null)
                {
                    using var geometry = d2dFactory.CreateRoundedRectangleGeometry(rounded);
                    d2d.PushLayer(new LayerParameters { ContentBounds = new Vortice.RawRectF(t.Left, t.Top, t.Right, t.Bottom),
                        GeometricMask = geometry, Opacity = 1, MaskTransform = Matrix3x2.Identity }, null);
                    d2d.DrawBitmap(picture, new Vortice.RawRectF(t.Left, t.Top, t.Right, t.Bottom), 1, Vortice.Direct2D1.BitmapInterpolationMode.Linear, null);
                    d2d.PopLayer();
                }
                else d2d.FillRoundedRectangle(rounded, well);
                bool current = i == d.Current;
                if (current || i == d.Hovered)
                {
                    float o = (float)(DesktopsPanel.Outset * k);
                    var r = new RoundedRectangle(new System.Drawing.RectangleF(t.X - o, t.Y - o, t.Width + 2 * o, t.Height + 2 * o), tileCorner + o, tileCorner + o);
                    d2d.DrawRoundedRectangle(r, current ? ring : quiet, (current ? 2.5f : 1.5f) * k);
                }
                d2d.DrawText(d.Names[i], current ? boldFont : nameFont,
                    new Vortice.RawRectF(t.Left - 2 * k, t.Bottom + 7 * k, t.Right + 2 * k, t.Bottom + 23 * k), ink);
            }
            picture?.Dispose();
            d2d.PopAxisAlignedClip();
            d2d.EndDraw();
            d2d.Target = null;
        }
        desktopsSurface.EndDraw();
        desktopsVisual.SetContent(desktopsSurface);
        desktopsVisual.SetOffsetX((float)Math.Round(d.Panel.X));
        desktopsVisual.SetOffsetY((float)Math.Round(d.Panel.Y));
        desktopsDrawn = d;
        return true;
    }

    /// The close button: a dark, three-quarters opaque circle with a white ×, as on the Mac.
    void DrawClose(double scale)
    {
        float size = (float)Math.Round(26 * scale), a = (float)(9 * scale), b = size - a;
        device.CreateSurface((uint)size, (uint)size, Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied, out var surface);
        using (var dxgi = surface.BeginDraw<IDXGISurface>(null, out Vortice.Mathematics.Int2 offset))
        using (var bitmap = d2d.CreateBitmapFromDxgiSurface(dxgi, new BitmapProperties1(
                   new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96,
                   BitmapOptions.Target | BitmapOptions.CannotDraw)))
        using (var dark = d2d.CreateSolidColorBrush(new Color4(0.1f, 0.1f, 0.1f, 0.75f)))
        using (var white = d2d.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f)))
        using (var round = d2dFactory.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round }))
        {
            d2d.Target = bitmap;
            d2d.BeginDraw();
            d2d.Transform = Matrix3x2.CreateTranslation(offset.X, offset.Y);
            d2d.PushAxisAlignedClip(new Vortice.RawRectF(0, 0, size, size), AntialiasMode.Aliased);
            d2d.Clear(new Color4(0, 0, 0, 0));
            d2d.FillEllipse(new Ellipse(new Vector2(size / 2, size / 2), size / 2, size / 2), dark);
            float w = (float)(2 * scale);
            d2d.DrawLine(new Vector2(a, a), new Vector2(b, b), white, w, round);
            d2d.DrawLine(new Vector2(a, b), new Vector2(b, a), white, w, round);
            d2d.PopAxisAlignedClip();
            d2d.EndDraw();
            d2d.Target = null;
        }
        surface.EndDraw();
        closeSurface?.Dispose();
        closeSurface = surface;
        closeScale = scale;
    }

    /// Each title a dark pill, centred above its picture and no wider than it (cut off at the
    /// picture's width; a title only has to name the app).
    public void SetLabels(IReadOnlyList<Label> labels, double scale)
    {
        titles.RemoveAllVisuals();
        foreach (var label in labels)
        {
            if (!titleVisuals.TryGetValue(label.Hwnd, out var t))
            {
                device.CreateVisual(out var v);
                titleVisuals[label.Hwnd] = t = new Title(v);
            }
            if (t.Text != label.Text || t.Scale != scale) Draw(t, label.Text, scale);
            var r = label.Picture;
            float w = Math.Min(t.W, (float)r.W);
            float x = (float)Math.Round(r.X + (r.W - w) / 2), y = (float)Math.Round(r.Y - t.H - 6 * scale);
            t.Visual.SetOffsetX(x);
            t.Visual.SetOffsetY(y);
            t.Visual.SetClip(new Vortice.RawRectF(0, 0, w, t.H));
            titles.AddVisual(t.Visual, false, null);
        }
    }

    /// An app's icon, into the drawing under way.
    void DrawIcon(IconImage icon, float x, float y)
    {
        var pinned = System.Runtime.InteropServices.GCHandle.Alloc(icon.Pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            using var image = d2d.CreateBitmap(new Vortice.Mathematics.SizeI(icon.Size, icon.Size), pinned.AddrOfPinnedObject(), (uint)(icon.Size * 4),
                new BitmapProperties(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
            d2d.DrawBitmap(image, new Vortice.RawRectF(x, y, x + icon.Size, y + icon.Size), 1, Vortice.Direct2D1.BitmapInterpolationMode.Linear, null);
        }
        finally { pinned.Free(); }
    }

    /// `fit`: the pill is this wide, its text ending in an ellipsis if it doesn't fit.
    /// `icon`: at the left of the text, or alone in the middle if the text has no room.
    /// `fitGlyph`: the stack's fit button: its mark instead of any text.
    void Draw(Title t, string text, double scale, bool front = false, float fit = 0, IconImage? icon = null, bool fitGlyph = false)
    {
        var font = TitleFont(scale);
        float pad = (float)Math.Round(10 * scale), h = (float)Math.Round(22 * scale);
        if (fit > 0) pad = Math.Min(pad, fit / 4); // a narrow one still has room for the ellipsis
        float iconRoom = icon != null ? icon.Size + (float)Math.Round(Stacks.TabIconGap * scale) : 0;
        float room = fit > 0 ? fit - pad * 2 - iconRoom : (float)(480 * scale);
        using var layout = dwrite.CreateTextLayout(text, font, Math.Max(room, 1), h);
        float w = fit > 0 ? fit : (float)Math.Ceiling(layout.Metrics.Width + pad * 2 + iconRoom);
        bool showText = room >= 10 * scale;

        device.CreateSurface((uint)w, (uint)h, Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied, out var surface);
        using (var dxgi = surface.BeginDraw<IDXGISurface>(null, out Vortice.Mathematics.Int2 offset))
        using (var bitmap = d2d.CreateBitmapFromDxgiSurface(dxgi, new BitmapProperties1(
                   new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96,
                   BitmapOptions.Target | BitmapOptions.CannotDraw)))
        using (var pill = d2d.CreateSolidColorBrush(front ? new Color4(46 / 255f, 110 / 255f, 220 / 255f, 1f) : new Color4(32 / 255f, 32 / 255f, 34 / 255f, 1f)))
        using (var ink = d2d.CreateSolidColorBrush(new Color4(240 / 255f, 240 / 255f, 240 / 255f, 1f)))
        {
            d2d.Target = bitmap;
            d2d.BeginDraw();
            // The surface may be part of a bigger texture: draw only in our rectangle of it.
            d2d.Transform = Matrix3x2.CreateTranslation(offset.X, offset.Y);
            d2d.PushAxisAlignedClip(new Vortice.RawRectF(0, 0, w, h), AntialiasMode.Aliased);
            d2d.Clear(new Color4(0, 0, 0, 0));
            d2d.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(0, 0, w, h), h / 2, h / 2), pill);
            if (icon != null) DrawIcon(icon, (float)Math.Round(showText ? pad : (w - icon.Size) / 2), (float)Math.Round((h - icon.Size) / 2));
            if (fitGlyph) StackBars.DrawFitGlyph(d2d, pill, ink, 0, 0, h, scale);
            else if (showText) d2d.DrawTextLayout(new Vector2(pad + iconRoom, (h - layout.Metrics.Height) / 2), layout, ink);
            d2d.PopAxisAlignedClip();
            d2d.EndDraw();
            d2d.Target = null;
        }
        surface.EndDraw();
        t.Visual.SetContent(surface);
        t.Surface?.Dispose();
        t.Surface = surface;
        t.Text = text;
        t.Scale = scale;
        t.W = w;
        t.H = h;
        t.Front = front;
        t.Fit = fit;
        t.Icon = icon;
    }

    static void Dispose(Title t)
    {
        t.Visual.Dispose();
        t.Surface?.Dispose();
    }

    /// Solid-colour rectangles over the pictures (the ring, the guides): visuals sharing one 1x1
    /// surface, each stretched to its rectangle by its transform.
    /// The circle with a group's colour.
    sealed class Swatch(IDCompositionVisual2 visual)
    {
        public readonly IDCompositionVisual2 Visual = visual;
        public IDCompositionSurface? Surface;
        public GroupColour Colour;
        public double Scale;
        public int Size;

        public void Dispose()
        {
            Visual.Dispose();
            Surface?.Dispose();
        }
    }

    sealed class Strips
    {
        readonly SharedVisualPictures owner;
        readonly IDCompositionVisual2 parent;
        readonly IDCompositionVisual2 container;
        IDCompositionSurface colour;
        public IDCompositionSurface Colour => colour;
        readonly List<IDCompositionVisual2> pool = new();

        (float r, float g, float b, float a) painted;
        /// A surface for each colour these have been: going back to one is a change of content, not a
        /// drawing. A group's box takes a few dozen on its way from light to solid (see SetGroups), and
        /// drawing one every frame was GPU work the compositor waited on.
        readonly Dictionary<(float r, float g, float b, float a), IDCompositionSurface> surfaces = new();
        /// More than this and all but the current go (dragging on the colour wheel makes a new colour
        /// at every move).
        const int MostSurfaces = 96;

        /// `parent`: where they go in the visual tree. Otherwise on top of what's there so far.
        public Strips(SharedVisualPictures owner, float r, float g, float b, float a = 1f, IDCompositionVisual2? parent = null)
        {
            this.owner = owner;
            this.parent = parent ?? owner.root;
            owner.device.CreateVisual(out container);
            this.parent.AddVisual(container, false, null);
            painted = (r, g, b, a);
            colour = Paint(r, g, b, a);
            surfaces[painted] = colour;
        }

        /// The rectangles in this colour, if that's a change.
        public void SetColour(float r, float g, float b, float a)
        {
            var key = (r, g, b, a);
            if (key == painted) return;
            if (!surfaces.TryGetValue(key, out var surface))
            {
                if (surfaces.Count >= MostSurfaces)
                {
                    foreach (var (k, old) in surfaces.Where(e => e.Key != painted).ToList())
                    {
                        old.Dispose();
                        surfaces.Remove(k);
                    }
                }
                surfaces[key] = surface = Paint(r, g, b, a);
            }
            painted = key;
            colour = surface;
            foreach (var v in pool) v.SetContent(colour);
        }

        IDCompositionSurface Paint(float r, float g, float b, float a)
        {
            owner.device.CreateSurface(1, 1, Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied, out var surface);
            // ImmediateContext is cached by Vortice: not ours to dispose.
            using (var tex = surface.BeginDraw<ID3D11Texture2D>(null, out Vortice.Mathematics.Int2 offset))
            using (var rtv = owner.d3d.CreateRenderTargetView(tex, null))
            using (var context1 = owner.d3d.ImmediateContext.QueryInterface<ID3D11DeviceContext1>())
                context1.ClearView(rtv, new Color4(r * a, g * a, b * a, a), new[] { new Vortice.RawRect(offset.X, offset.Y, offset.X + 1, offset.Y + 1) });
            surface.EndDraw();
            return surface;
        }

        public void Set(IReadOnlyList<RectD> rects)
        {
            while (pool.Count < rects.Count)
            {
                owner.device.CreateVisual(out var v);
                v.SetContent(colour);
                v.SetBorderMode(BorderMode.Hard); // soft edges fade a stretched 1 px bitmap to nothing
                pool.Add(v);
            }
            container.RemoveAllVisuals();
            for (int i = 0; i < rects.Count; i++)
            {
                var r = rects[i];
                pool[i].SetTransform(new Matrix3x2((float)r.W, 0, 0, (float)r.H, (float)r.X, (float)r.Y));
                container.AddVisual(pool[i], false, null);
            }
        }

        public void Dispose()
        {
            if (parent != owner.root) parent.RemoveVisual(container);
            container.RemoveAllVisuals();
            foreach (var v in pool) v.Dispose();
            container.Dispose();
            foreach (var surface in surfaces.Values) surface.Dispose();
        }
    }

    public void Dispose()
    {
        Detach();
        DropAll();
        device.Commit();
        foreach (var t in titleVisuals.Values) Dispose(t);
        titleVisuals.Clear();
        foreach (var t in groupTitles.Values) Dispose(t);
        foreach (var v in groupTrash.Values) v.Dispose();
        if (newGroupTitle != null) Dispose(newGroupTitle);
        trashSurface?.Dispose();
        treeSurface?.Dispose();
        treeVisual.Dispose();
        foreach (var (fill, edge) in groupBoxes.Values)
        {
            fill.Dispose();
            edge.Dispose();
        }
        foreach (var swatch in groupSwatches.Values) swatch.Dispose();
        wheelLayer.RemoveAllVisuals();
        wheelMarker.Dispose();
        wheelVisual.Dispose();
        markerSurface?.Dispose();
        wheelSurface?.Dispose();
        wheelLayer.Dispose();
        groupBoxLayer.Dispose();
        groupLabels.Dispose();
        buttonLayer.Dispose();
        accent.Dispose();
        marqueeFill.Dispose();
        tabFade.Dispose();
        decorationsHidden.Dispose();
        guides.Dispose();
        closeSurface?.Dispose();
        minimapSurface?.Dispose();
        minimapVisual.Dispose();
        desktopsSurface?.Dispose();
        desktopsVisual.Dispose();
        foreach (var f in fonts.Values) f.Dispose();
        titles.Dispose();
        canvas.Dispose();
        root.Dispose();
        target.Dispose();
        device.Dispose();
        d2d.Dispose();
        d2dDevice.Dispose();
        d2dFactory.Dispose();
        dwrite.Dispose();
        d3d.Dispose();
    }
}
