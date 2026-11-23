using System.Runtime.InteropServices;
using System.Text;
using Expanse.Native;
using static Expanse.Native.User32;

namespace Expanse;

/// The command drawer's two text boxes, a command's name and its line, over the fields it draws.
/// Popups owned by the overlay, for the reason in RenameBox. Enter, Esc and Tab are theirs; clicks
/// on the drawer's buttons are the canvas's, so losing focus ends nothing.
sealed class CommandBox
{
    const int GWLP_WNDPROC = -4, VK_ESC = 0x1B, VK_TAB = 0x09, VK_SELECT_ALL = 0x41, NameLength = 80, LineLength = 2000;
    const uint ES_AUTOHSCROLL = 0x80, WS_BORDER = 0x800000, EM_SETSEL = 0xB1, EM_LIMITTEXT = 0xC5, WM_SETFOCUS = 0x7, WM_SETFONT = 0x30, WM_CHAR = 0x102;

    readonly WndProc proc; // kept alive: Windows holds a pointer to it
    nint name, line, last, baseProc, font;
    double fontScale;
    RECT nameAt, lineAt;

    /// Enter (true) or Esc (false).
    public Action<bool>? Ended;
    public bool IsOpen { get; private set; }

    public CommandBox() => proc = WindowProc;

    /// Rectangles in screen coordinates.
    public void Open(nint owner, RECT nameRect, RECT lineRect, string nameText, string lineText, double scale)
    {
        if (name == 0) name = Make(owner, NameLength);
        if (line == 0) line = Make(owner, LineLength);
        if (name == 0 || line == 0) return;
        if (font == 0 || fontScale != scale)
        {
            if (font != 0) Gdi32.DeleteObject(font);
            font = Gdi32.CreateFont(-(int)(13 * scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            fontScale = scale;
            SendMessage(name, WM_SETFONT, font, 1);
            SendMessage(line, WM_SETFONT, font, 1);
        }
        SetWindowText(name, nameText);
        SetWindowText(line, lineText);
        IsOpen = true;
        nameAt = lineAt = default;
        MoveTo(nameRect, lineRect);
        // A new command starts with what it runs; its name can follow from that.
        Focus(lineText.Length == 0 ? line : name);
    }

    nint Make(nint owner, int length)
    {
        nint edit = CreateWindowEx((uint)(WS_EX_TOPMOST | WS_EX_TOOLWINDOW), "EDIT", "", unchecked((uint)WS_POPUP) | WS_BORDER | ES_AUTOHSCROLL,
            0, 0, 10, 10, owner, 0, 0, 0);
        if (edit == 0)
        {
            Log.Info($"commands: couldn't make a text box: {Marshal.GetLastWin32Error()}");
            return 0;
        }
        baseProc = SetWindowLongPtr(edit, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(proc));
        SendMessage(edit, EM_LIMITTEXT, length, 0);
        return edit;
    }

    public void MoveTo(RECT nameRect, RECT lineRect)
    {
        if (!IsOpen) return;
        Put(name, nameRect, ref nameAt);
        Put(line, lineRect, ref lineAt);
    }

    static void Put(nint edit, RECT r, ref RECT at)
    {
        if (r.Left == at.Left && r.Top == at.Top && r.Right == at.Right && r.Bottom == at.Bottom) return;
        at = r;
        SetWindowPos(edit, HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height, SWP_SHOWWINDOW | SWP_NOACTIVATE);
    }

    /// The keyboard, back to the field that had it.
    public void Refocus()
    {
        if (!IsOpen) return;
        SetForegroundWindow(last);
        SetFocus(last);
    }

    void Focus(nint edit)
    {
        last = edit;
        SetForegroundWindow(edit);
        SetFocus(edit);
        SendMessage(edit, EM_SETSEL, 0, -1);
    }

    public (string name, string line) Texts => (Text(name, NameLength), Text(line, LineLength));

    static string Text(nint edit, int length)
    {
        var sb = new StringBuilder(length + 1);
        GetWindowText(edit, sb, sb.Capacity);
        return sb.ToString().Trim();
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        ShowWindow(name, SW_HIDE);
        ShowWindow(line, SW_HIDE);
    }

    nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (msg == WM_SETFOCUS) last = hwnd; // clicked into
            switch (msg)
            {
                case WM_KEYDOWN when (int)wParam == VK_RETURN:
                    Ended?.Invoke(true);
                    return 0;
                case WM_KEYDOWN when (int)wParam == VK_ESC:
                    Ended?.Invoke(false);
                    return 0;
                case WM_KEYDOWN when (int)wParam == VK_TAB:
                    Focus(hwnd == name ? line : name);
                    return 0;
                case WM_KEYDOWN when (int)wParam == VK_SELECT_ALL && GetKeyState(VK_CONTROL) < 0:
                    SendMessage(hwnd, EM_SETSEL, 0, -1);
                    return 0;
                case WM_CHAR when (int)wParam is 1 or 9 or 13 or 27: // or it beeps
                    return 0;
            }
        }
        catch (Exception e)
        {
            Log.Info($"commands: message 0x{msg:X} threw {e}");
        }
        return CallWindowProc(baseProc, hwnd, msg, wParam, lParam);
    }
}
