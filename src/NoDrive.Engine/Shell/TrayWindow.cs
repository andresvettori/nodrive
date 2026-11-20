using NoDrive.Engine.Infrastructure;
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
    private const nuint IdAllowDeletes = 5;
    private const nuint IdPause = 6;
    private const nuint IdAutostart = 7;
    private const nuint IdOpenUi = 8;
    private const nuint IdReport = 9;
    private static Action? s_onReport;

    /// <summary>Opens the page on its "report a problem" section.</summary>
    public static void SetReportProblem(Action onReport) => s_onReport = onReport;
    private static Action? s_onOpenUi;
    private static Action? s_onResume;

    /// <summary>Called when the PC wakes from sleep or hibernation.</summary>
    public static void SetOnResume(Action onResume) => s_onResume = onResume;

    /// <summary>Opens the local settings and activity page (also the left-click and double-click action).</summary>
    public static void SetOpenUi(Action onOpenUi) => s_onOpenUi = onOpenUi;
    private static Func<bool>? s_isPaused;
    private static Action? s_onTogglePause;
    private static Func<bool>? s_autostartOn;
    private static Action? s_onToggleAutostart;

    /// <summary>Wires the pause and autostart menu entries.</summary>
    public static void Configure(Func<bool> isPaused, Action togglePause, Func<bool> autostartOn, Action toggleAutostart)
    {
        s_isPaused = isPaused; s_onTogglePause = togglePause; s_autostartOn = autostartOn; s_onToggleAutostart = toggleAutostart;
    }
    private static Func<int>? s_heldDeletes;
    private static Action? s_onAllowDeletes;

    private static HWND s_hwnd;
    private static bool s_hasIcon;
    private static string s_tooltip = "NoDrive";
    private static Action? s_onQuit;
    private static Action? s_onOpenFolder;
    private static Func<string>? s_statusText;
    private static Action? s_onSignIn;
    private static Func<bool>? s_needsSignIn;

    public static HWND Create(string tooltip, Action onQuit, Action onOpenFolder, Func<string> statusText, Action onSignIn, Func<bool> needsSignIn, Func<int>? heldDeletes = null, Action? onAllowDeletes = null)
    {
        s_heldDeletes = heldDeletes; s_onAllowDeletes = onAllowDeletes;
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
        nid.hIcon = LoadAppIcon();
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

    /// <summary>The NoDrive icon embedded in the exe (ApplicationIcon); falls back to the generic application icon.</summary>
    private static HICON LoadAppIcon()
    {
        fixed (char* p = Environment.ProcessPath ?? "")
        {
            HICON icon = PInvoke.ExtractIcon(HINSTANCE.Null, new PCWSTR(p), 0);
            return icon.IsNull ? PInvoke.LoadIcon(HINSTANCE.Null, PInvoke.IDI_APPLICATION) : icon;
        }
    }

    private static void ShowMenu()
    {
        PInvoke.GetCursorPos(out System.Drawing.Point pt);
        PInvoke.SetForegroundWindow(s_hwnd);
        HMENU menu = PInvoke.CreatePopupMenu();
        try
        {
            string status = s_statusText?.Invoke() ?? "";
            string allowText = $"Allow {s_heldDeletes?.Invoke() ?? 0} held deletions...";
            fixed (char* pStatus = status)
            fixed (char* pSign = "Sign in...")
            fixed (char* pAllow = allowText)
            fixed (char* pPause = (s_isPaused?.Invoke() == true ? "Resume syncing" : "Pause syncing"))
            fixed (char* pAuto = "Start NoDrive with Windows")
            fixed (char* pUi = "Open NoDrive...")
            fixed (char* pReport = "Report a problem...")
            fixed (char* pOpen = "Open NoDrive folder")
            fixed (char* pQuit = "Quit NoDrive")
            {
                PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING | MENU_ITEM_FLAGS.MF_GRAYED, IdStatus, new PCWSTR(pStatus));
                if (s_needsSignIn?.Invoke() == true) { PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, IdSignIn, new PCWSTR(pSign)); }
                if ((s_heldDeletes?.Invoke() ?? 0) > 0) { PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, IdAllowDeletes, new PCWSTR(pAllow)); }
                PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, IdOpenUi, new PCWSTR(pUi));
                PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, IdReport, new PCWSTR(pReport));
                PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, IdPause, new PCWSTR(pPause));
                PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING | (s_autostartOn?.Invoke() == true ? MENU_ITEM_FLAGS.MF_CHECKED : 0), IdAutostart, new PCWSTR(pAuto));
                PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, IdOpenFolder, new PCWSTR(pOpen));
                PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, IdQuit, new PCWSTR(pQuit));
            }
            uint flags = (uint)(TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON | TRACK_POPUP_MENU_FLAGS.TPM_NONOTIFY);
            nuint cmd = (nuint)PInvoke.TrackPopupMenuEx(menu, flags, pt.X, pt.Y, s_hwnd, null).Value;
            if (cmd == IdOpenFolder) { s_onOpenFolder?.Invoke(); }
            else if (cmd == IdOpenUi) { s_onOpenUi?.Invoke(); }
            else if (cmd == IdReport) { s_onReport?.Invoke(); }
            else if (cmd == IdPause) { s_onTogglePause?.Invoke(); }
            else if (cmd == IdAutostart) { s_onToggleAutostart?.Invoke(); }
            else if (cmd == IdAllowDeletes) { s_onAllowDeletes?.Invoke(); }
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
                if (mouse == 0x0203) { s_onOpenUi?.Invoke(); }
                else if (mouse is PInvoke.WM_RBUTTONUP or PInvoke.WM_CONTEXTMENU or PInvoke.WM_LBUTTONUP) { ShowMenu(); }
                return new LRESULT(0);
            case PInvoke.WM_CLOSE:
                Log.Info("shutdown message: WM_CLOSE");
                PInvoke.DestroyWindow(hwnd);
                return new LRESULT(0);
            case 0x0218:                                                   // WM_POWERBROADCAST
                if ((nuint)wParam is 0x12 or 0x7) { s_onResume?.Invoke(); }   // PBT_APMRESUMEAUTOMATIC, PBT_APMRESUMESUSPEND
                return new LRESULT(1);
            case PInvoke.WM_QUERYENDSESSION:
                Log.Info($"shutdown message: WM_QUERYENDSESSION (flags=0x{(ulong)lParam.Value:X})");
                PackageRemoval.OnCloseRequested((nuint)lParam.Value);
                return new LRESULT(1);
            case PInvoke.WM_ENDSESSION:
                Log.Info($"shutdown message: WM_ENDSESSION (ending={wParam.Value != 0}, flags=0x{(ulong)lParam.Value:X})");
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
