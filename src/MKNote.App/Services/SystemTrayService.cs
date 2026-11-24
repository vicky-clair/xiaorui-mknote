using System.Runtime.InteropServices;
using MKNote.Core.Localization;

namespace MKNote.App.Services;

/// <summary>
/// 原生 Win32 系统托盘服务（基于 Shell_NotifyIconW 与 SetWindowSubclass 实现）。
/// 支持左键单击显示/隐藏主窗口，以及右键弹出本地化快捷操作菜单（新建、截图、锁定、退出）。
/// </summary>
public sealed class SystemTrayService : IDisposable
{
    private const uint WM_TRAYICON = 0x8000 + 101;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_RIGHTALIGN = 0x0008;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_RETURNCMD = 0x0100;

    private const uint CMD_SHOW = 1001;
    private const uint CMD_NEW_MD = 1002;
    private const uint CMD_NEW_RICH = 1003;
    private const uint CMD_CAPTURE = 1004;
    private const uint CMD_LOCK = 1005;
    private const uint CMD_EXIT = 1006;

    private const nuint SubclassId = 4201;

    private delegate nint SubclassProcDelegate(nint hWnd, uint uMsg, nuint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint ExtractAssociatedIconW(nint hInst, string lpIconPath, ref ushort lpiIcon);

    [DllImport("user32.dll")]
    private static extern nint LoadIconW(nint hInstance, nint lpIconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(nint hMenu, uint uFlags, nuint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProcDelegate pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(nint hWnd, SubclassProcDelegate pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nuint wParam, nint lParam);

    private readonly nint _hwnd;
    private readonly SubclassProcDelegate _subclassProc; // Keep delegate alive to prevent GC
    private nint _hIcon;
    private bool _iconOwned;
    private bool _disposed;

    public event Action? ToggleShowHideRequested;
    public event Action? NewMarkdownRequested;
    public event Action? NewRichRequested;
    public event Action? CaptureScreenRequested;
    public event Action? LockRequested;
    public event Action? ExitRequested;

    public SystemTrayService(nint hwnd, string tooltip = "MKNote Next — 本地加密知识库")
    {
        _hwnd = hwnd;
        _subclassProc = SubclassProc;

        ushort iconIdx = 0;
        var exePath = Environment.ProcessPath ?? string.Empty;
        if (!string.IsNullOrEmpty(exePath))
        {
            _hIcon = ExtractAssociatedIconW(0, exePath, ref iconIdx);
            _iconOwned = _hIcon != 0;
        }
        if (_hIcon == 0)
        {
            _hIcon = LoadIconW(0, 32512); // IDI_APPLICATION
            _iconOwned = false;
        }

        SetWindowSubclass(_hwnd, _subclassProc, SubclassId, 0);

        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _hIcon,
            szTip = tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };

        Shell_NotifyIconW(NIM_ADD, ref nid);
    }

    private nint SubclassProc(nint hWnd, uint uMsg, nuint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (uMsg == WM_TRAYICON)
        {
            var mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);
            if (mouseMsg is WM_LBUTTONUP or WM_LBUTTONDBLCLK)
            {
                ToggleShowHideRequested?.Invoke();
                return 0;
            }
            if (mouseMsg == WM_RBUTTONUP)
            {
                ShowContextMenu();
                return 0;
            }
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0) return;

        try
        {
            AppendMenuW(menu, MF_STRING, CMD_SHOW, Strings.T("tray_open"));
            AppendMenuW(menu, MF_SEPARATOR, 0, string.Empty);
            AppendMenuW(menu, MF_STRING, CMD_NEW_MD, "📝 " + Strings.T("tray_new_md"));
            AppendMenuW(menu, MF_STRING, CMD_NEW_RICH, "📄 " + Strings.T("tray_new_rich"));
            AppendMenuW(menu, MF_STRING, CMD_CAPTURE, "📷 " + Strings.T("tray_capture"));
            AppendMenuW(menu, MF_STRING, CMD_LOCK, "🛡️ " + Strings.T("menu_lock"));
            AppendMenuW(menu, MF_SEPARATOR, 0, string.Empty);
            AppendMenuW(menu, MF_STRING, CMD_EXIT, Strings.T("tray_exit"));

            GetCursorPos(out var pt);
            SetForegroundWindow(_hwnd);
            var cmd = TrackPopupMenu(menu, TPM_RIGHTALIGN | TPM_BOTTOMALIGN | TPM_RETURNCMD, pt.X, pt.Y, 0, _hwnd, 0);

            switch (cmd)
            {
                case CMD_SHOW: ToggleShowHideRequested?.Invoke(); break;
                case CMD_NEW_MD: NewMarkdownRequested?.Invoke(); break;
                case CMD_NEW_RICH: NewRichRequested?.Invoke(); break;
                case CMD_CAPTURE: CaptureScreenRequested?.Invoke(); break;
                case CMD_LOCK: LockRequested?.Invoke(); break;
                case CMD_EXIT: ExitRequested?.Invoke(); break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            szTip = string.Empty,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
        Shell_NotifyIconW(NIM_DELETE, ref nid);
        RemoveWindowSubclass(_hwnd, _subclassProc, SubclassId);

        if (_iconOwned && _hIcon != 0)
        {
            DestroyIcon(_hIcon);
            _hIcon = 0;
        }
    }
}
