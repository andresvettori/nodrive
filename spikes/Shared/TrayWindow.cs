// Shared by the P0 spikes (linked via <Compile Include>). Hidden top-level window + optional tray icon
// with a Quit menu, driven by a classic GetMessage loop. All Win32 via CsWin32 with marshaling disabled,
// so this file is Native AOT clean.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NoDrive.Spikes.Shared;

internal static unsafe class TrayWindow
{
    private const uint WM_TRAY = PInvoke.WM_APP + 1;
    private const nuint ID_QUIT = 1;

    private static HWND s_hwnd;
    private static bool s_hasIcon;
    private static Action? s_onQuit;
    private static string s_tooltip = "NoDrive";

    /// <summary>Creates the hidden window (and the tray icon when <paramref name="withIcon"/>). Call from the thread that will run the loop.</summary>
    public static HWND Create(string className, string tooltip, bool withIcon, Action? onQuit)
    {
        s_onQuit = onQuit;
        s_tooltip = tooltip;
        HINSTANCE hInstance = new(PInvoke.GetModuleHandle((PCWSTR)null).Value);

        fixed (char* pClass = className)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = hInstance,
                lpszClassName = new PCWSTR(pClass),
            };
            if (PInvoke.RegisterClassEx(in wc) == 0)
            {
                throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastPInvokeError()}");
            }

            fixed (char* pTitle = tooltip)
            {
                s_hwnd = PInvoke.CreateWindowEx(
                    WINDOW_EX_STYLE.WS_EX_TOOLWINDOW,
                    new PCWSTR(pClass),
                    new PCWSTR(pTitle),
                    WINDOW_STYLE.WS_OVERLAPPED,
                    0, 0, 0, 0,
                    HWND.Null,
                    HMENU.Null,
                    hInstance,
                    null);
            }
        }

        if (s_hwnd.IsNull)
        {
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}");
        }

        if (withIcon)
        {
            AddIcon();
        }

        return s_hwnd;
    }

    private static void AddIcon()
    {
        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = s_hwnd,
            uID = 1,
            uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP,
            uCallbackMessage = WM_TRAY,
            hIcon = PInvoke.LoadIcon(HINSTANCE.Null, PInvoke.IDI_APPLICATION),
        };
        var tip = nid.szTip.AsSpan();
        s_tooltip.AsSpan(0, Math.Min(s_tooltip.Length, tip.Length - 1)).CopyTo(tip);

        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, &nid))
        {
            throw new InvalidOperationException("Shell_NotifyIcon(NIM_ADD) failed");
        }
        s_hasIcon = true;
    }

    private static void RemoveIcon()
    {
        if (!s_hasIcon) { return; }
        var nid = new NOTIFYICONDATAW { cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = s_hwnd, uID = 1 };
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, &nid);
        s_hasIcon = false;
    }

    /// <summary>Blocks until WM_QUIT.</summary>
    public static void RunMessageLoop()
    {
        MSG msg;
        while (PInvoke.GetMessage(&msg, HWND.Null, 0, 0).Value > 0)
        {
            PInvoke.TranslateMessage(&msg);
            PInvoke.DispatchMessage(&msg);
        }
    }

    /// <summary>Thread-safe request to end the loop.</summary>
    public static void RequestQuit()
    {
        if (!s_hwnd.IsNull) { PInvoke.PostMessage(s_hwnd, PInvoke.WM_CLOSE, default, default); }
    }

    public static void Destroy()
    {
        RemoveIcon();
        if (!s_hwnd.IsNull) { PInvoke.DestroyWindow(s_hwnd); s_hwnd = HWND.Null; }
    }

    private static void ShowMenu()
    {
        PInvoke.GetCursorPos(out System.Drawing.Point pt);
        PInvoke.SetForegroundWindow(s_hwnd);
        HMENU menu = PInvoke.CreatePopupMenu();
        try
        {
            fixed (char* pQuit = "Quit")
            {
                PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, ID_QUIT, new PCWSTR(pQuit));
            }
            uint flags = (uint)(TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON | TRACK_POPUP_MENU_FLAGS.TPM_NONOTIFY);
            BOOL cmd = PInvoke.TrackPopupMenuEx(menu, flags, pt.X, pt.Y, s_hwnd, null);
            if ((nuint)cmd.Value == ID_QUIT)
            {
                s_onQuit?.Invoke();
                PInvoke.DestroyWindow(s_hwnd);
            }
        }
        finally
        {
            PInvoke.DestroyMenu(menu);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case WM_TRAY:
                uint mouse = (uint)(lParam.Value & 0xFFFF);
                if (mouse == PInvoke.WM_RBUTTONUP || mouse == PInvoke.WM_CONTEXTMENU || mouse == PInvoke.WM_LBUTTONUP)
                {
                    ShowMenu();
                }
                return new LRESULT(0);
            case PInvoke.WM_CLOSE:
                PInvoke.DestroyWindow(hwnd);
                return new LRESULT(0);
            case PInvoke.WM_QUERYENDSESSION:
                return new LRESULT(1);
            case PInvoke.WM_ENDSESSION:
                s_onQuit?.Invoke();
                return new LRESULT(0);
            case PInvoke.WM_DESTROY:
                RemoveIcon();
                s_hwnd = HWND.Null;
                PInvoke.PostQuitMessage(0);
                return new LRESULT(0);
            default:
                return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }
}
