#if WINDOWS
using System.Runtime.InteropServices;

namespace Kashishu;

/// <summary>
/// Shell_NotifyIcon wrapper with a standard Windows context menu.
/// </summary>
internal sealed class WindowsTrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 21;
    private const int NIM_ADD = 0;
    private const int NIM_DELETE = 2;
    private const int NIF_MESSAGE = 0x1;
    private const int NIF_ICON = 0x2;
    private const int NIF_TIP = 0x4;
    private const int WM_LBUTTONUP = 0x202;
    private const int WM_LBUTTONDBLCLK = 0x203;
    private const int WM_RBUTTONUP = 0x205;
    private const int WM_CONTEXTMENU = 0x7B;
    private const int IMAGE_ICON = 1;
    private const int LR_SHARED = 0x8000;
    private const int SM_CXSMICON = 49;
    private const int SM_CYSMICON = 50;
    private static readonly IntPtr AppIconId = new(32512);

    private readonly Action _leftClick;
    private readonly Action _rightClick;
    private readonly string _tooltip;
    private readonly WndProcDelegate _wndProc;
    private readonly IntPtr _instance;
    private readonly ushort _classAtom;
    private readonly string _className;
    private readonly uint _taskbarCreated;
    private IntPtr _window;
    private IntPtr _icon;
    private bool _added;
    private bool _disposed;

    public WindowsTrayIcon(string tooltip, Action leftClick, Action rightClick)
    {
        _tooltip = tooltip;
        _leftClick = leftClick;
        _rightClick = rightClick;
        _instance = GetModuleHandleW(null);
        _wndProc = WindowProc;
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");

        var className = _className = $"KashishuTrayWindow.{Environment.ProcessId}";
        var wc = new WndClassEx
        {
            cbSize = Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _instance,
            lpszClassName = className
        };
        _classAtom = RegisterClassExW(ref wc);
        if (_classAtom == 0)
            throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");

        _window = CreateWindowExW(0, className, string.Empty, 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
        // A hidden top-level window receives Explorer's TaskbarCreated broadcast.
        // Message-only windows do not, so their tray icon never recovers after a restart.
        if (_window == IntPtr.Zero)
        {
            UnregisterClassW(className, _instance);
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        }

        _icon = LoadImageW(_instance, AppIconId, IMAGE_ICON,
            GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON), LR_SHARED);
        if (_icon == IntPtr.Zero)
            _icon = LoadImageW(IntPtr.Zero, AppIconId, IMAGE_ICON, 0, 0, LR_SHARED);
        AddIcon();
    }

    private void AddIcon()
    {
        if (_disposed)
            return;
        var data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _window,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = _tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
            guidItem = Guid.Empty
        };
        _added = Shell_NotifyIconW(NIM_ADD, ref data);
    }

    public void ShowMenu(IReadOnlyList<TrayMenuEntry> entries, nint owner = 0) =>
        WindowsTrayMenu.Show(owner == 0 ? _window : owner, entries);

    private IntPtr WindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == CallbackMessage)
        {
            switch ((uint)(lParam.ToInt64() & 0xFFFF))
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    _leftClick();
                    return IntPtr.Zero;
                case WM_RBUTTONUP:
                case WM_CONTEXTMENU:
                    _rightClick();
                    return IntPtr.Zero;
            }
            return IntPtr.Zero;
        }

        if (message == _taskbarCreated && _taskbarCreated != 0)
        {
            // Explorer restarted and dropped the icon.
            _added = false;
            AddIcon();
            return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_added)
        {
            var data = new NotifyIconData
            {
                cbSize = Marshal.SizeOf<NotifyIconData>(),
                hWnd = _window,
                uID = 1,
                szTip = string.Empty,
                szInfo = string.Empty,
                szInfoTitle = string.Empty,
                guidItem = Guid.Empty
            };
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _added = false;
        }

        if (_window != IntPtr.Zero)
        {
            DestroyWindow(_window);
            _window = IntPtr.Zero;
        }
        UnregisterClassW(_className, _instance);
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClassW(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(IntPtr instance, IntPtr name, int type, int cx, int cy, int load);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(int message, ref NotifyIconData data);
}
#endif
