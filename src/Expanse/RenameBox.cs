using System.Runtime.InteropServices;
using System.Text;
using Expanse.Native;
using static Expanse.Native.User32;

namespace Expanse;

/// A text box for typing a name in place. A popup of its own, owned by the overlay: the pictures'
/// visual tree draws over everything inside the overlay, child windows included, so a child would
/// be hidden. Enter or clicking away saves, Esc cancels.
sealed class RenameBox
{
    const int GWLP_WNDPROC = -4;
    const uint ES_AUTOHSCROLL = 0x80, WS_BORDER = 0x800000, EM_SETSEL = 0xB1, EM_LIMITTEXT = 0xC5, WM_SETFONT = 0x30,
        WM_KILLFOCUS = 0x8, WM_CHAR = 0x102;
    const int VK_ESC = 0x1B, MaxLength = 80;

    readonly WndProc proc; // kept alive: Windows holds a pointer to it
    nint edit, baseProc, font;
    double fontScale;
    Action<string?, bool>? finished;

    public RenameBox() => proc = WindowProc;

    /// `done` gets the text (null: cancelled) and whether a key ended it, rather than clicking away.
    public void Show(nint owner, int x, int y, int width, int height, string text, double scale, Action<string?, bool> done)
    {
        Finish(save: true, byKey: false);
        if (edit == 0)
        {
            edit = CreateWindowEx((uint)(WS_EX_TOPMOST | WS_EX_TOOLWINDOW), "EDIT", text, unchecked((uint)WS_POPUP) | WS_BORDER | ES_AUTOHSCROLL,
                x, y, width, height, owner, 0, 0, 0);
            if (edit == 0) { Log.Info($"rename: couldn't make the text box: {Marshal.GetLastWin32Error()}"); return; }
            baseProc = SetWindowLongPtr(edit, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(proc));
            SendMessage(edit, EM_LIMITTEXT, MaxLength, 0);
        }
        if (font == 0 || fontScale != scale)
        {
            if (font != 0) Gdi32.DeleteObject(font);
            font = Gdi32.CreateFont(-(int)(13 * scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            fontScale = scale;
            SendMessage(edit, WM_SETFONT, font, 1);
        }
        SetWindowText(edit, text);
        finished = done;
        SetWindowPos(edit, HWND_TOPMOST, x, y, width, height, SWP_SHOWWINDOW);
        SetForegroundWindow(edit);
        SetFocus(edit);
        SendMessage(edit, EM_SETSEL, 0, -1);
    }

    /// Ends the rename, if there is one.
    public void Finish(bool save, bool byKey)
    {
        if (finished is not Action<string?, bool> done) return;
        finished = null; // hiding takes the focus away, which comes back here
        string? text = null;
        if (save)
        {
            var sb = new StringBuilder(MaxLength + 1);
            GetWindowText(edit, sb, sb.Capacity);
            text = sb.ToString();
        }
        ShowWindow(edit, SW_HIDE);
        done(text, byKey);
    }

    nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_KEYDOWN when (int)wParam == VK_RETURN:
                    Finish(save: true, byKey: true);
                    return 0;
                case WM_KEYDOWN when (int)wParam == VK_ESC:
                    Finish(save: false, byKey: true);
                    return 0;
                case WM_CHAR when (int)wParam is 13 or 27: // or it beeps
                    return 0;
                case WM_KILLFOCUS:
                    Finish(save: true, byKey: false);
                    break;
            }
        }
        catch (Exception e)
        {
            Log.Info($"rename: message 0x{msg:X} threw {e}");
        }
        return CallWindowProc(baseProc, hwnd, msg, wParam, lParam);
    }
}
