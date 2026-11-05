using System.Text;
using Expanse.Native;
using static Expanse.Native.User32;

namespace Expanse;

/// Asks for a percentage on the tray menu's behalf (Group Selection Zoom, Auto-Centre When Hidden).
/// Drawn as Welcome is, with a plain text box for the number. Modal, as a message box is: it runs
/// messages itself until it's answered.
static class PercentDialog
{
    const uint WS_BORDER = 0x800000, WS_CLIPCHILDREN = 0x2000000, ES_CENTER = 0x1, ES_NUMBER = 0x2000,
        EM_LIMITTEXT = 0xC5, EM_SETSEL = 0xB1, WM_SETFONT = 0x30;

    static nint hwnd, edit;
    static WndProc? proc; // kept alive: Windows holds a pointer to it
    static RECT ok, cancel;
    static int? answer;
    static string explanation = "", prompt = "";
    static int lowest, highest;

    /// The number chosen, from `min` to `max`, or null if it was cancelled (or the dialog is
    /// already up).
    public static int? Ask(string title, string about, string question, int current, int min, int max)
    {
        if (hwnd != 0 && IsWindow(hwnd))
        {
            SetForegroundWindow(hwnd);
            return null;
        }
        (explanation, prompt, lowest, highest) = (about, question, min, max);
        proc ??= WindowProc;
        var instance = RegisterWindowClass("ExpansePercent", proc);
        var (_, work) = PrimaryMonitor();
        double k = PrimaryDpi() / 96.0;
        int S(double v) => (int)Math.Round(v * k);
        int w = S(420), h = S(230);
        hwnd = CreateWindowEx((uint)WS_EX_TOPMOST, "ExpansePercent", title, 0x00C00000 | 0x00080000 | WS_CLIPCHILDREN, // caption, system menu
            work.Left + (work.Width - w) / 2, work.Top + (work.Height - h) / 2, w, h, 0, 0, instance, 0);
        if (hwnd == 0) { Log.Info($"{title}: couldn't open"); return null; }
        GetClientRect(hwnd, out var rc);
        edit = CreateWindowEx(0, "EDIT", current.ToString(), unchecked((uint)WS_CHILD | (uint)WS_VISIBLE) | WS_BORDER | ES_CENTER | ES_NUMBER,
            rc.Width - S(24) - S(64), S(86), S(64), S(28), hwnd, 0, instance, 0);
        nint font = Gdi32.CreateFont(-S(14), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        SendMessage(edit, WM_SETFONT, font, 1);
        SendMessage(edit, EM_LIMITTEXT, max.ToString().Length, 0);
        ShowWindow(hwnd, SW_SHOWNORMAL);
        SetForegroundWindow(hwnd);
        SetFocus(edit);
        SendMessage(edit, EM_SETSEL, 0, -1);

        answer = null;
        while (hwnd != 0)
        {
            int got = GetMessage(out var m, 0, 0, 0);
            if (got <= 0)
            {
                if (got == 0) PostQuitMessage((int)m.wParam); // for the main loop to see
                break;
            }
            if (m.message == WM_KEYDOWN && (int)m.wParam == VK_RETURN) { Accept(); continue; }
            if (m.message == WM_KEYDOWN && (int)m.wParam == VK_ESCAPE) { DestroyWindow(hwnd); continue; }
            // The characters those keys make would only beep.
            if (m.message == 0x102 /* WM_CHAR */ && (int)m.wParam is 13 or 27) continue;
            TranslateMessage(ref m);
            DispatchMessage(ref m);
        }
        Gdi32.DeleteObject(font);
        return answer;
    }

    /// OK: closes with the number typed; an empty box, or a number out of range, stays open for
    /// another.
    static void Accept()
    {
        var text = new StringBuilder(8);
        GetWindowText(edit, text, text.Capacity);
        if (!int.TryParse(text.ToString(), out int number) || number < lowest || number > highest)
        {
            SetFocus(edit);
            SendMessage(edit, EM_SETSEL, 0, -1);
            return;
        }
        answer = number;
        DestroyWindow(hwnd);
    }

    static nint WindowProc(nint h, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_PAINT:
                Paint(h);
                return 0;
            case WM_ERASEBKGND:
                return 1;
            case WM_LBUTTONUP:
            {
                int x = (short)(lParam & 0xFFFF), y = (short)((lParam >> 16) & 0xFFFF);
                static bool In(RECT r, int x, int y) => x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
                if (In(ok, x, y)) Accept();
                else if (In(cancel, x, y)) DestroyWindow(h);
                return 0;
            }
            case WM_DESTROY:
                hwnd = 0;
                return 0;
        }
        return DefWindowProc(h, msg, wParam, lParam);
    }

    static void Paint(nint h)
    {
        nint hdc = BeginPaint(h, out var ps);
        GetClientRect(h, out var rc);
        double k = GetDpiForWindow(h) / 96.0;
        int S(double v) => (int)Math.Round(v * k);
        nint white = Gdi32.CreateSolidBrush(Gdi32.RGB(255, 255, 255));
        nint grey = Gdi32.CreateSolidBrush(Gdi32.RGB(240, 241, 244));
        nint blue = Gdi32.CreateSolidBrush(Gdi32.RGB(44, 120, 220));
        nint body = Gdi32.CreateFont(-S(14), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        nint strong = Gdi32.CreateFont(-S(14), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        var all = new RECT(0, 0, rc.Width, rc.Height);
        FillRect(hdc, ref all, white);
        Gdi32.SetBkMode(hdc, Gdi32.TRANSPARENT);
        nint oldFont = Gdi32.SelectObject(hdc, body);
        nint oldPen = Gdi32.SelectObject(hdc, Gdi32.GetStockObject(Gdi32.NULL_PEN));
        nint oldBrush = Gdi32.SelectObject(hdc, grey);
        int x = S(24), right = rc.Width - S(24);

        Gdi32.SetTextColor(hdc, Gdi32.RGB(100, 100, 108));
        var about = new RECT(x, S(20), right, S(78));
        DrawText(hdc, explanation, -1, ref about, DT_WORDBREAK | DT_NOPREFIX);
        Gdi32.SetTextColor(hdc, Gdi32.RGB(28, 28, 30));
        var question = new RECT(x, S(86), right - S(64) - S(8), S(114));
        DrawText(hdc, prompt, -1, ref question, DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);

        int bw = S(84), bh = S(32), top = rc.Height - S(20) - bh;
        ok = new RECT(right - bw, top, right, top + bh);
        cancel = new RECT(ok.Left - S(8) - bw, top, ok.Left - S(8), top + bh);
        Gdi32.RoundRect(hdc, cancel.Left, cancel.Top, cancel.Right, cancel.Bottom, bh, bh);
        var cancelText = cancel;
        DrawText(hdc, "Cancel", -1, ref cancelText, DT_SINGLELINE | DT_VCENTER | DT_CENTER | DT_NOPREFIX);
        Gdi32.SelectObject(hdc, blue);
        Gdi32.SelectObject(hdc, strong);
        Gdi32.RoundRect(hdc, ok.Left, ok.Top, ok.Right, ok.Bottom, bh, bh);
        Gdi32.SetTextColor(hdc, Gdi32.RGB(255, 255, 255));
        var okText = ok;
        DrawText(hdc, "OK", -1, ref okText, DT_SINGLELINE | DT_VCENTER | DT_CENTER | DT_NOPREFIX);

        Gdi32.SelectObject(hdc, oldFont);
        Gdi32.SelectObject(hdc, oldPen);
        Gdi32.SelectObject(hdc, oldBrush);
        Gdi32.DeleteObject(body);
        Gdi32.DeleteObject(strong);
        Gdi32.DeleteObject(white);
        Gdi32.DeleteObject(grey);
        Gdi32.DeleteObject(blue);
        EndPaint(h, ref ps);
    }
}
