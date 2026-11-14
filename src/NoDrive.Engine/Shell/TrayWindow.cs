using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NoDrive.Engine.Shell;

/// <summary>
/// Hidden top-level window + tray icon with a small menu, driven by a classic GetMessage loop. Win32 through CsWin32 with marshaling
/// disabled (Native AOT safe). The idle cost of the whole engine is this loop: it sleeps in GetMessage.
/// </summary>
internal static unsafe class TrayWindow
{
    private const uint WmTray = PInvoke.WM_APP + 1;
    private const nuint IdQuit = 1;
    private const nuint IdOpenFolder = 2;
    private const nuint IdStatus = 3;
    private const nuint IdSignIn = 4;

    private static HWND s_hwnd;
    private static bool s_hasIcon;
    private static string s_tooltip = "NoDrive";
    private static Action? s_onQuit;
    private static Action? s_onOpenFolder;
    private static Func<string>? s_statusText;
    private static Action? s_onSignIn;
    private static Func<bool>? s_needsSignIn;

    public static HWND Create(string tooltip, Action onQuit, Action onOpenFolder, Func<string> statusText, Action onSignIn, Func<bool> needsSignIn)
    {
        s_onQuit = onQuit; s_onOpenFolder = onOpenFolder; s_statusText = statusText; s_tooltip = tooltip; s_onSignIn = onSignIn; s_needsSignIn = needsSignIn;
        HINSTANCE hInstance = new(PInvoke.GetModuleHandle((PCWSTR)null).Value);
        fixed (char* pClass = "NoDriveEngineWindow")
        fixed (char* pTitle = "NoDrive")
        {
            var wc = new WNDCLASSEXW { cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &WndProc, hInstance = hInstance, lpszClassName = new PCWSTR(pClass) };
            if (PInvoke.RegisterClassEx(in wc) == 0) { throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastPInvokeError()}"); }
            s_hwnd = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, new PCWSTR(pClass), new PCWSTR(pTitle), WINDOW_STYLE.WS_OVERLAPPED, 0, 0, 0, 0, HWND.Null, HMENU.Null, hInstance, null);
        }
        if (s_hwnd.IsNull) { throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}"); }
        AddIcon();
        return s_hwnd;
    }

    public static void SetTooltip(string text)
    {
        s_tooltip = text;
        if (!s_hasIcon) { return; }
        var nid = BaseData();
        nid.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_TIP;
        CopyTip(ref nid);
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, &nid);
    }

    private static NOTIFYICONDATAW BaseData() => new() { cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = s_hwnd, uID = 1 };

    private static void CopyTip(ref NOTIFYICONDATAW nid)
    {
        var tip = nid.szTip.AsSpan();
        tip.Clear();
        s_tooltip.AsSpan(0, Math.Min(s_tooltip.Length, tip.Length - 1)).CopyTo(tip);
    }

    private static void AddIcon()
    {
        var nid = BaseData();
        nid.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP;
        nid.uCallbackMessage = WmTray;
        nid.hIcon = PInvoke.LoadIcon(HINSTANCE.Null, PInvoke.IDI_APPLICATION);
        CopyTip(ref nid);
        s_hasIcon = PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, &nid);
    }

    private static void RemoveIcon()
    {
        if (!s_hasIcon) { return; }
        var nid = BaseData();
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, &nid);
        s_hasIcon = false;
    }

    /// <summary>Blocks until the window is destroyed.</summary>
    public static void RunMessageLoop()
    {
        MSG msg;
        while (PInvoke.GetMessage(&msg, HWND.Null, 0, 0).Value > 0)
        {
            PInvoke.TranslateMessage(&msg);
            PInvoke.DispatchMessage(&msg);
        }
    }

    /// <summary>Thread-safe: asks the window to close, which ends the loop.</summary>
    public static void RequestQuit()
    {
        if (!s_hwnd.IsNull) { PInvoke.PostMessage(s_hwnd, PInvoke.WM_CLOSE, default, default); }
    }

    private static void ShowMenu()
    {
        PInvoke.GetCursorPos(out System.Drawing.Point pt);
        PInvoke.SetForegroundWindow(s_hwnd);
        HMENU menu = PInvoke.CreatePopupMenu();
        try
        {
            string status = s_statusText?.Invoke() ?? "";
            fixed (char* pStatus = status)
            fixed (char* pSign = "Sign in...")
            fixed (char* pOpen = "Open NoDrive folder")
            fixed (char* pQuit = "Quit NoDrive")
            {
                PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING | MENU_ITEM_FLAGS.MF_GRAYED, IdStatus, new PCWSTR(pStatus));
                if (s_needsSignIn?.Invoke() == true) { PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, IdSignIn, new PCWSTR(pSign)); }
                PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, IdOpenFolder, new PCWSTR(pOpen));
                PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, IdQuit, new PCWSTR(pQuit));
            }
            uint flags = (uint)(TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON | TRACK_POPUP_MENU_FLAGS.TPM_NONOTIFY);
            nuint cmd = (nuint)PInvoke.TrackPopupMenuEx(menu, flags, pt.X, pt.Y, s_hwnd, null).Value;
            if (cmd == IdOpenFolder) { s_onOpenFolder?.Invoke(); }
            else if (cmd == IdSignIn) { s_onSignIn?.Invoke(); }
            else if (cmd == IdQuit) { PInvoke.DestroyWindow(s_hwnd); }
        }
        finally { PInvoke.DestroyMenu(menu); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case WmTray:
                uint mouse = (uint)(lParam.Value & 0xFFFF);
                if (mouse is PInvoke.WM_RBUTTONUP or PInvoke.WM_CONTEXTMENU or PInvoke.WM_LBUTTONUP) { ShowMenu(); }
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
                s_onQuit?.Invoke();
                s_hwnd = HWND.Null;
                PInvoke.PostQuitMessage(0);
                return new LRESULT(0);
            default:
                return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }
}
