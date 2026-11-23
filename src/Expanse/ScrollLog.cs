using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Expanse.Native;
using static Expanse.Native.User32;

namespace Expanse;

/// With Detailed Logging: what scrolls and touchpad gestures arrive as, for telling a touchpad from
/// a wheel. One line for each run of turns of a kind, and one whenever the number of fingers on a
/// precision touchpad changes. Nothing acts on any of it.
static class ScrollLog
{
    /// A turn, as the mouse hook (`Seen`) or the overlay (`Got`) has it.
    readonly record struct Turn(string Kind, int Delta, long At, double HandledMs, long LagMs);

    sealed class Run
    {
        public long First, Last;
        public int Count, Sum;
        public double SlowestMs;
        public long LongestLag;
        public readonly List<int> Deltas = [];
    }

    const long RunPauseMs = 300;
    const int DeltasShown = 40;
    static readonly ConcurrentQueue<Turn> turns = new();
    static readonly Dictionary<string, Run> runs = [];
    static nint registeredFor;
    static int fingers;
    static int reports;
    static readonly Dictionary<nint, (nint data, int links)?> touchpads = [];

    [StructLayout(LayoutKind.Sequential)] struct INPUT_MESSAGE_SOURCE { public int device, origin; }
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort page, usage; public uint flags; public nint hwnd; }
    [DllImport("user32.dll")] static extern bool GetCurrentInputMessageSource(out INPUT_MESSAGE_SOURCE source);
    [DllImport("user32.dll")] static extern nint GetMessageExtraInfo();
    [DllImport("user32.dll")] static extern int GetMessageTime();
    [DllImport("user32.dll")] static extern bool GetPointerType(uint id, out int type);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(in RAWINPUTDEVICE devices, uint count, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(nint input, uint command, nint data, ref uint size, uint headerSize);
    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW")] static extern uint GetRawInputDeviceInfo(nint device, uint command, nint data, ref uint size);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(nint preparsed, [Out] ushort[] caps);
    [DllImport("hid.dll")] static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value, nint preparsed, nint report, uint length);
    [DllImport("hid.dll")] static extern int HidP_GetUsages(int type, ushort page, ushort link, [Out] ushort[] usages, ref uint count, nint preparsed, nint report, uint length);

    const int WM_INPUT = 0xFF, DM_POINTERHITTEST = 0x250, WM_GESTURE = 0x119, WM_GESTURENOTIFY = 0x11A, WM_POINTERWHEEL = 0x24E, WM_POINTERHWHEEL = 0x24F;
    const int HidSuccess = 0x00110000;

    static string Down(short state) => state < 0 ? "down" : "up";

    /// On the hook's thread, which mustn't wait: logged later (Flush).
    public static void Seen(bool horizontal, int delta, uint flags, nint extra, string over) =>
        turns.Enqueue(new($"hook {(horizontal ? "sideways" : "up/down")} over {over}, Ctrl now {Down(GetAsyncKeyState(VK_CONTROL))}, flags {flags}, extra 0x{extra:X}",
            delta, Environment.TickCount64, 0, 0));

    /// A turn the overlay is handling: to Got once it has.
    public static string Describe(bool horizontal, int keys)
    {
        string source = GetCurrentInputMessageSource(out var s) ? $"device {s.device} origin {s.origin}" : "device unknown";
        return $"canvas {(horizontal ? "sideways" : "up/down")}, keys {keys}, Ctrl {Down(GetKeyState(VK_CONTROL))} (now {Down(GetAsyncKeyState(VK_CONTROL))}), {source}, extra 0x{GetMessageExtraInfo():X}";
    }

    public static long Lag => unchecked((uint)(Environment.TickCount - GetMessageTime()));

    public static void Got(string kind, int delta, double handledMs, long lagMs) =>
        turns.Enqueue(new(kind, delta, Environment.TickCount64, handledMs, lagMs));

    /// On the overlay's thread, a few times a second: logs the runs that have ended, and starts and
    /// stops following the touchpad with Detailed Logging.
    public static void Flush(nint overlay)
    {
        if (Log.ToFile != (registeredFor != 0)) Follow(overlay);
        while (turns.TryDequeue(out var t))
        {
            if (runs.TryGetValue(t.Kind, out var r) && t.At - r.Last > RunPauseMs) { Write(t.Kind, r); r = null; }
            if (r == null) runs[t.Kind] = r = new Run { First = t.At };
            r.Last = t.At;
            r.Count++;
            r.Sum += t.Delta;
            r.SlowestMs = Math.Max(r.SlowestMs, t.HandledMs);
            r.LongestLag = Math.Max(r.LongestLag, t.LagMs);
            if (r.Deltas.Count < DeltasShown) r.Deltas.Add(t.Delta);
        }
        long now = Environment.TickCount64;
        foreach (var (kind, r) in runs.Where(p => now - p.Value.Last > RunPauseMs).ToList())
        {
            Write(kind, r);
            runs.Remove(kind);
        }
    }

    static void Write(string kind, Run r) =>
        Log.Info($"scroll: {kind}: {r.Count} turns in {r.Last - r.First}ms, {r.Sum} in all [{string.Join(" ", r.Deltas)}{(r.Count > r.Deltas.Count ? " …" : "")}]" +
                 (r.SlowestMs > 0 ? $", slowest {r.SlowestMs:0.0}ms, latest {r.LongestLag}ms" : ""));

    /// Any of the overlay's messages: those a touchpad might send.
    public static void Message(uint msg, nint wParam, nint lParam)
    {
        if (!Log.ToFile) return;
        switch (msg)
        {
            case WM_INPUT:
                Touchpad(lParam);
                break;
            case DM_POINTERHITTEST or WM_POINTERWHEEL or WM_POINTERHWHEEL:
                string type = GetPointerType((uint)(wParam & 0xFFFF), out int t) ? $"{t}" : "unknown";
                Log.Info($"scroll: message 0x{msg:X}, pointer type {type}");
                break;
            case WM_GESTURE or WM_GESTURENOTIFY:
                Log.Info($"scroll: message 0x{msg:X}, {wParam}");
                break;
        }
    }

    static void Follow(nint overlay)
    {
        bool on = Log.ToFile;
        // Windows Precision Touchpads: digitizers (0x0D), touch pad (0x05), also while another app has focus.
        var device = new RAWINPUTDEVICE { page = 0x0D, usage = 0x05, flags = on ? 0x100u /* RIDEV_INPUTSINK */ : 1u /* RIDEV_REMOVE */, hwnd = on ? overlay : 0 };
        bool ok = RegisterRawInputDevices(device, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        if (on) Log.Info(ok ? "scroll: following the touchpad's fingers" : $"scroll: can't follow the touchpad's fingers ({Marshal.GetLastPInvokeError()})");
        registeredFor = on ? overlay : 0;
        fingers = reports = 0;
    }

    static unsafe void Touchpad(nint input)
    {
        const uint headerSize = 24; // RAWINPUTHEADER, 64-bit: type, size, device, wParam
        uint size = 0;
        if (GetRawInputData(input, 0x10000003 /* RID_INPUT */, 0, ref size, headerSize) != 0 || size < headerSize + 8) return;
        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            if (GetRawInputData(input, 0x10000003, (nint)p, ref size, headerSize) != size) return;
            if (*(uint*)p != 2 /* RIM_TYPEHID */) return;
            nint device = *(nint*)(p + 8);
            uint reportSize = *(uint*)(p + headerSize), count = *(uint*)(p + headerSize + 4);
            if (reportSize == 0 || headerSize + 8 + (ulong)reportSize * count > size) return;
            if (Known(device) is not { } pad) return;
            for (uint i = 0; i < count; i++) Report(pad.data, pad.links, (nint)(p + headerSize + 8 + i * reportSize), reportSize);
        }
    }

    /// The touchpad's description of its reports (kept for good) and how many parts they have.
    static (nint data, int links)? Known(nint device)
    {
        if (touchpads.TryGetValue(device, out var known)) return known;
        uint size = 0;
        GetRawInputDeviceInfo(device, 0x20000005 /* RIDI_PREPARSEDDATA */, 0, ref size);
        (nint data, int links)? pad = null;
        if (size > 0)
        {
            nint data = Marshal.AllocHGlobal((int)size);
            var caps = new ushort[32]; // HIDP_CAPS
            uint copied = GetRawInputDeviceInfo(device, 0x20000005, data, ref size);
            if (copied is not (0 or uint.MaxValue) && HidP_GetCaps(data, caps) == HidSuccess) pad = (data, caps[22] /* NumberLinkCollectionNodes */);
            else Marshal.FreeHGlobal(data);
        }
        Log.Info(pad is { } found ? $"scroll: a touchpad, {found.links} parts to its reports" : "scroll: a touchpad whose reports can't be read");
        touchpads[device] = pad;
        return pad;
    }

    static void Report(nint data, int links, nint report, uint length)
    {
        reports++;
        if (HidP_GetUsageValue(0, 0x0D, 0, 0x54 /* contact count */, out uint contacts, data, report, length) != HidSuccess) return;
        int touching = 0;
        var usages = new ushort[32];
        for (ushort link = 1; link < links; link++)
        {
            uint n = (uint)usages.Length;
            if (HidP_GetUsages(0, 0x0D, link, usages, ref n, data, report, length) == HidSuccess && usages.AsSpan(0, (int)n).Contains((ushort)0x42 /* tip */)) touching++;
        }
        // Zero contacts: the rest of the report before, on a touchpad that sends fingers one at a time.
        if (contacts == 0 || touching == fingers) return;
        Log.Info($"scroll: {touching} fingers on the touchpad (were {fingers}; {contacts} reported; {reports} reports since)");
        fingers = touching;
        reports = 0;
    }
}
