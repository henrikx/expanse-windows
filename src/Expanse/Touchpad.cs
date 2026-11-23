using System.Runtime.InteropServices;
using Expanse.Native;

namespace Expanse;

/// How many fingers are on a Windows Precision Touchpad, from its raw reports: for telling its
/// scrolls from a wheel's (TouchpadTurns), which Windows sends alike. They come also while another
/// app has focus, but only while it's touched. Other touchpads look like a mouse, and stay one.
/// All on one thread, the one that registered.
static class Touchpad
{
    public const uint WM_INPUT = 0xFF;
    const int HidSuccess = 0x00110000;
    static readonly Dictionary<nint, nint?> touchpads = [];

    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort page, usage; public uint flags; public nint hwnd; }
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(in RAWINPUTDEVICE devices, uint count, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(nint input, uint command, nint data, ref uint size, uint headerSize);
    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW")] static extern uint GetRawInputDeviceInfo(nint device, uint command, nint data, ref uint size);
    [DllImport("hid.dll")] static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value, nint preparsed, nint report, uint length);

    /// Its reports to this thread, as WM_INPUT. The window they're for, to destroy when done (0: none).
    public static nint Follow()
    {
        nint window = User32.CreateWindowEx(0, "STATIC", null, 0, 0, 0, 0, 0, -3 /* HWND_MESSAGE */, 0, 0, 0);
        // Digitizers (0x0D), touch pad (0x05).
        var device = new RAWINPUTDEVICE { page = 0x0D, usage = 0x05, flags = 0x100 /* RIDEV_INPUTSINK */, hwnd = window };
        if (window == 0 || !RegisterRawInputDevices(device, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            Log.Info($"touchpad: can't follow its fingers ({Marshal.GetLastPInvokeError()})");
        return window;
    }

    /// A WM_INPUT: the touchpad's reports.
    public static unsafe void Read(nint input, TouchpadTurns turns)
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
            for (uint i = 0; i < count; i++) Report(pad, (nint)(p + headerSize + 8 + i * reportSize), reportSize, turns);
        }
    }

    /// The touchpad's description of its reports (kept for good); null if they can't be read.
    static nint? Known(nint device)
    {
        if (touchpads.TryGetValue(device, out var known)) return known;
        uint size = 0;
        GetRawInputDeviceInfo(device, 0x20000005 /* RIDI_PREPARSEDDATA */, 0, ref size);
        nint? pad = null;
        if (size > 0)
        {
            nint data = Marshal.AllocHGlobal((int)size);
            uint copied = GetRawInputDeviceInfo(device, 0x20000005, data, ref size);
            if (copied is not (0 or uint.MaxValue)) pad = data;
            else Marshal.FreeHGlobal(data);
        }
        Log.Info(pad != null ? "touchpad: found" : "touchpad: its reports can't be read");
        touchpads[device] = pad;
        return pad;
    }

    static void Report(nint data, nint report, uint length, TouchpadTurns turns)
    {
        if (HidP_GetUsageValue(0, 0x0D, 0, 0x54 /* contact count */, out uint contacts, data, report, length) != HidSuccess) return;
        // Zero: the rest of the report before, on a touchpad that sends fingers one at a time. (A finger
        // that lifts is counted in a last report.)
        if (contacts > 0) turns.Fingers((int)contacts, Environment.TickCount64);
    }
}
