using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Expanse.Native;
using static Expanse.Native.User32;

namespace Expanse;

/// With Detailed Logging: what scrolls and touchpad gestures arrive as, for telling a touchpad from
/// a wheel. One line for each run of turns of a kind. Nothing acts on any of it.
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

    [StructLayout(LayoutKind.Sequential)] struct INPUT_MESSAGE_SOURCE { public int device, origin; }
    [DllImport("user32.dll")] static extern bool GetCurrentInputMessageSource(out INPUT_MESSAGE_SOURCE source);
    [DllImport("user32.dll")] static extern nint GetMessageExtraInfo();
    [DllImport("user32.dll")] static extern int GetMessageTime();
    [DllImport("user32.dll")] static extern bool GetPointerType(uint id, out int type);

    const int DM_POINTERHITTEST = 0x250, WM_GESTURE = 0x119, WM_GESTURENOTIFY = 0x11A, WM_POINTERWHEEL = 0x24E, WM_POINTERHWHEEL = 0x24F;

    static string Down(short state) => state < 0 ? "down" : "up";

    /// On the hook's thread, which mustn't wait: logged later (Flush).
    public static void Seen(bool horizontal, int delta, uint flags, nint extra, bool touchpad, string over) =>
        turns.Enqueue(new($"hook {(touchpad ? "touchpad" : "wheel")} {(horizontal ? "sideways" : "up/down")} over {over}, Ctrl now {Down(GetAsyncKeyState(VK_CONTROL))}, flags {flags}, extra 0x{extra:X}",
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

    /// On the overlay's thread, a few times a second: logs the runs that have ended.
    public static void Flush()
    {
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
    public static void Message(uint msg, nint wParam)
    {
        if (!Log.ToFile) return;
        switch (msg)
        {
            case DM_POINTERHITTEST or WM_POINTERWHEEL or WM_POINTERHWHEEL:
                string type = GetPointerType((uint)(wParam & 0xFFFF), out int t) ? $"{t}" : "unknown";
                Log.Info($"scroll: message 0x{msg:X}, pointer type {type}");
                break;
            case WM_GESTURE or WM_GESTURENOTIFY:
                Log.Info($"scroll: message 0x{msg:X}, {wParam}");
                break;
        }
    }
}
