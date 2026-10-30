using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MinecraftHost;

/// <summary>
/// The icon in the system tray (Shell_NotifyIcon, no WinForms). Left click shows the window again; right click asks
/// the window for its menu. Uses the exe's own icon (the blue creeper).
/// </summary>
sealed class Tray : IDisposable
{
    const int WM_APP_TRAY = 0x8000 + 41;
    const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_LBUTTONDBLCLK = 0x0203;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[] small, int count);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);

    readonly HwndSource _source;
    readonly IntPtr _icon;
    NotifyIconData _data;
    bool _shown;

    public event Action? Open;
    public event Action? Menu;

    public Tray(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(hwnd);
        _source.AddHook(Hook);
        var small = new IntPtr[1];
        ExtractIconEx(Environment.ProcessPath ?? "", 0, null, small, 1);
        _icon = small[0];
        _data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(), hWnd = hwnd, uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP, uCallbackMessage = WM_APP_TRAY, hIcon = _icon,
            szTip = "Minecraft Self-Hoster", szInfo = "", szInfoTitle = "",
        };
    }

    public void Show()
    {
        if (_shown) return;
        _shown = Shell_NotifyIcon(NIM_ADD, ref _data);
    }

    public void Hide()
    {
        if (!_shown) return;
        Shell_NotifyIcon(NIM_DELETE, ref _data);
        _shown = false;
    }

    public void SetTip(string tip)
    {
        _data.szTip = tip.Length > 127 ? tip[..127] : tip;
        _data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        if (_shown) Shell_NotifyIcon(NIM_MODIFY, ref _data);
    }

    /// <summary>A notification bubble from the tray icon.</summary>
    public void Balloon(string title, string text)
    {
        if (!_shown) return;
        _data.uFlags = NIF_INFO;
        _data.szInfoTitle = title;
        _data.szInfo = text;
        _data.dwInfoFlags = 0;
        Shell_NotifyIcon(NIM_MODIFY, ref _data);
        _data.szInfo = _data.szInfoTitle = "";
    }

    public void BringForward(Window window) => SetForegroundWindow(new WindowInteropHelper(window).Handle);

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_APP_TRAY) return IntPtr.Zero;
        var ev = (int)(lParam.ToInt64() & 0xFFFF);
        if (ev is WM_LBUTTONUP or WM_LBUTTONDBLCLK) { Open?.Invoke(); handled = true; }
        else if (ev == WM_RBUTTONUP) { Menu?.Invoke(); handled = true; }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Hide();
        _source.RemoveHook(Hook);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
    }
}
