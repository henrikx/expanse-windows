using System.Runtime.InteropServices;
using System.Text;
using Expanse.Native;
using static Expanse.Native.User32;

namespace Expanse;

/// The search bar's text box, up only while you're typing in it; the rest of the time the panel
/// draws what it holds. A popup owned by the overlay, for the reason in RenameBox.
sealed class SearchBox
{
    const int GWLP_WNDPROC = -4, VK_ESC = 0x1B, VK_TAB = 0x09, MaxLength = 120;
    const uint ES_AUTOHSCROLL = 0x80, EM_SETSEL = 0xB1, EM_LIMITTEXT = 0xC5, WM_SETFONT = 0x30, WM_KILLFOCUS = 0x8, WM_CHAR = 0x102,
        WM_CUT = 0x300, WM_PASTE = 0x302, WM_CLEAR = 0x303, WM_UNDO = 0x304;

    readonly WndProc proc; // kept alive: Windows holds a pointer to it
    nint edit, baseProc, font;
    double fontScale;
    string text = "";

    /// The text changed; a key the box doesn't handle itself (Enter, Esc, Tab, up, down); focus left.
    public Action<string>? Changed;
    public Action<int>? Key;
    public Action? Left;
    public bool IsOpen { get; private set; }

    public SearchBox() => proc = WindowProc;

    public void Open(nint owner, int x, int y, int width, int height, string current, double scale)
    {
        if (edit == 0)
        {
            edit = CreateWindowEx((uint)(WS_EX_TOPMOST | WS_EX_TOOLWINDOW), "EDIT", current, unchecked((uint)WS_POPUP) | ES_AUTOHSCROLL,
                x, y, width, height, owner, 0, 0, 0);
            if (edit == 0) { Log.Info($"search: couldn't make the text box: {Marshal.GetLastWin32Error()}"); return; }
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
        text = current;
        SetWindowText(edit, current);
        IsOpen = true;
        SetWindowPos(edit, HWND_TOPMOST, x, y, width, height, SWP_SHOWWINDOW);
        SetForegroundWindow(edit);
        SetFocus(edit);
        SendMessage(edit, EM_SETSEL, current.Length, current.Length);
    }

    /// Takes the box down, keeping nothing; the caller holds the text.
    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false; // hiding takes the focus away, which comes back here
        ShowWindow(edit, SW_HIDE);
    }

    public void SetText(string value)
    {
        if (edit != 0) SetWindowText(edit, value);
        NoteChange();
    }

    void NoteChange()
    {
        var sb = new StringBuilder(MaxLength + 1);
        if (edit != 0) GetWindowText(edit, sb, sb.Capacity);
        if (sb.ToString() == text) return;
        text = sb.ToString();
        Changed?.Invoke(text);
    }

    nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_KEYDOWN when (int)wParam is VK_RETURN or VK_ESC or VK_TAB or VK_UP or VK_DOWN:
                    Key?.Invoke((int)wParam);
                    return 0;
                case WM_CHAR when (int)wParam is 13 or 27 or 9 or 127: // they'd beep, or type a box (Ctrl+Backspace)
                    return 0;
                case WM_KILLFOCUS when IsOpen:
                    Close();
                    Left?.Invoke();
                    break;
            }
            nint result = CallWindowProc(baseProc, hwnd, msg, wParam, lParam);
            if (IsOpen && msg is WM_CHAR or WM_KEYDOWN or WM_CUT or WM_PASTE or WM_CLEAR or WM_UNDO) NoteChange();
            return result;
        }
        catch (Exception e)
        {
            Log.Info($"search: message 0x{msg:X} threw {e}");
            return 0;
        }
    }
}
