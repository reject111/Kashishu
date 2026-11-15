#if WINDOWS
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace LyricifyIsland;

/// <summary>
/// Windows counterpart of <see cref="NativeOverlay"/>. Avalonia's transparent window is a
/// normal rectangle to the window manager, so the area around the island would otherwise
/// swallow clicks. A window region shaped like the capsule (plus its shadow halo) keeps the
/// empty sides out of hit testing for every process; WM_NCHITTEST reports HTTRANSPARENT in
/// the remaining halo, and the window stays out of the task switcher and activation chain.
/// </summary>
internal sealed class WindowsOverlay : IDisposable
{
    private const int WmNcHitTest = 0x0084;
    private const int WmMouseActivate = 0x0021;
    private const int WmWindowPosChanging = 0x0046;
    private const int WmExitSizeMove = 0x0232;
    private const uint SwpNoMove = 0x0002;
    private const nint HtClient = 1;
    private const nint HtTransparent = -1;
    private const nint MaNoActivate = 3;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x0000_0080L;
    private const long WsExNoActivate = 0x0800_0000L;
    // The capsule's drop shadow is blurred by sigma 7 and offset by 3 px, so the window
    // region keeps a small halo around the pill to avoid cutting the shadow off. The halo
    // above the pill is left out: Windows will not let the window region cross the top edge
    // of the screen while a window is dragged, so a top halo would keep the capsule that
    // far away from the top edge when the island is dragged there.
    private const float RegionHalo = 28f;
    private const float RegionTopHalo = 0f;

    // WDA_EXCLUDEFROMCAPTURE: the island's own screen grabs then see the desktop behind it,
    // which is what the acrylic material is sampled from. The cost is that the island is hidden
    // from other screen captures too, so this is only switched on while acrylic is enabled.
    private const uint WdaExcludeFromCapture = 0x0000_0011;

    // The island follows the user between virtual desktops; a second is often enough to keep
    // up with a switch and cheap enough to poll.
    private const int DesktopFollowIntervalMs = 1000;

    private readonly Window _window;
    private readonly Win32Properties.CustomWndProcHookCallback _hook;
    private IVirtualDesktopManager? _desktopManager;
    private Guid _currentDesktop;
    private DispatcherTimer? _desktopTimer;
    private bool _desktopFollow = true;
    private Rect _islandBounds;
    private bool _temporarilyHidden;
    private bool _captureHidden;
    private nint _hwnd;
    private Rect _appliedRegion;
    private double _appliedScaling;
    private bool _appliedHidden;
    private bool _hasRegion;
    private bool _attached;
    private bool _disposed;

    public WindowsOverlay(Window window)
    {
        _window = window;
        _hook = OnWndProc;
    }

    /// <summary>
    /// Called with the position the window manager is about to move the window to, so the
    /// capsule can be kept on screen while the OS move loop is running instead of snapping
    /// back only once the drag ends.
    /// </summary>
    public Func<PixelPoint, PixelPoint>? ConstrainPosition { get; set; }

    /// <summary>Raised after the OS move/size loop has finished.</summary>
    public Action? MoveCompleted { get; set; }

    public void Attach()
    {
        if (_attached || _disposed)
            return;
        var hwnd = _window.TryGetPlatformHandle()?.Handle ?? nint.Zero;
        if (hwnd == nint.Zero)
        {
            // The native handle is created on the UI thread; retry once the window is shown.
            Dispatcher.UIThread.Post(Attach, DispatcherPriority.Loaded);
            return;
        }

        try
        {
            Win32Properties.AddWndProcHookCallback(_window, _hook);
            _hwnd = hwnd;
            var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            SetWindowLongPtr(hwnd, GwlExStyle,
                new nint(style | WsExToolWindow | WsExNoActivate));
            _attached = true;
            ApplyCaptureHidden();
            if (_islandBounds.Width > 0d && _islandBounds.Height > 0d)
                ApplyRegion();
            StartDesktopFollow();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[windows] overlay hook unavailable: {exception.Message}");
        }
    }

    /// <summary>
    /// Keeps the island out of screen captures, so that the acrylic material can sample the
    /// desktop underneath it instead of the island itself.
    /// </summary>
    public void SetCaptureHidden(bool hidden)
    {
        if (_captureHidden == hidden)
            return;
        _captureHidden = hidden;
        if (_attached)
            ApplyCaptureHidden();
    }

    private void ApplyCaptureHidden()
    {
        if (_hwnd == nint.Zero)
            return;
        if (!SetWindowDisplayAffinity(_hwnd, _captureHidden ? WdaExcludeFromCapture : 0u))
        {
            Console.Error.WriteLine(
                $"[windows] capture exclusion unavailable: {Marshal.GetLastPInvokeError()}");
        }
    }

    private void StartDesktopFollow()
    {
        if (_desktopTimer is not null || !_desktopFollow)
            return;
        _desktopTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DesktopFollowIntervalMs) };
        _desktopTimer.Tick += (_, _) => FollowActiveDesktop();
        _desktopTimer.Start();
        FollowActiveDesktop();
    }

    /// <summary>
    /// Keeps the island on the desktop the user is looking at. A window is created on whichever
    /// virtual desktop happened to be active and then stays there while the user switches, which
    /// is the last thing an always-on-top overlay should do.
    /// </summary>
    private void FollowActiveDesktop()
    {
        if (!_attached || _hwnd == nint.Zero)
            return;
        try
        {
            var manager = _desktopManager ??= (IVirtualDesktopManager)new VirtualDesktopManager();
            if (!TryFindCurrentDesktop(manager, out var desktop))
                return;
            // The island's own window reports no desktop at all - Avalonia's no-redirection-bitmap
            // window is not tracked by the manager - so the desktop it should be on is taken from
            // other windows and compared with the one seen last time.
            if (desktop == _currentDesktop)
                return;
            var firstLook = _currentDesktop == Guid.Empty;
            _currentDesktop = desktop;
            if (firstLook)
                return;
            // The composition surface does not survive the move: the window lands on the new
            // desktop with nothing drawn in it, and invalidating alone leaves it blank. Hiding
            // and showing it again makes the renderer build a fresh surface.
            _window.Hide();
            var result = manager.MoveWindowToDesktop(_hwnd, ref desktop);
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed)
                    return;
                _window.Show();
                _window.InvalidateVisual();
            }, DispatcherPriority.Background);
            if (result != 0)
            {
                // The call is unavailable on some builds; the island then simply stays put.
                _desktopFollow = false;
                _desktopTimer?.Stop();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[windows] desktop follow unavailable: {exception.Message}");
            _desktopFollow = false;
            _desktopTimer?.Stop();
        }
    }

    /// <summary>
    /// Resolves the id of the desktop the user is on. Only the published desktop API is used, and
    /// it cannot answer the question directly: a window pinned to every desktop reports the
    /// desktop it was created on. So the windows visible right now vote, and the desktop most of
    /// them report wins; the foreground window counts twice because it is what the user is using.
    /// </summary>
    private static bool TryFindCurrentDesktop(IVirtualDesktopManager manager, out Guid desktop)
    {
        desktop = Guid.Empty;
        var votes = new Dictionary<Guid, int>();
        var foreground = GetForegroundWindow();
        var candidates = new List<nint>();
        if (foreground != nint.Zero)
            candidates.Add(foreground);
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && GetWindowTextLength(hwnd) > 0)
                candidates.Add(hwnd);
            return true;
        }, nint.Zero);

        foreach (var candidate in candidates)
        {
            if (manager.IsWindowOnCurrentVirtualDesktop(candidate, out var onCurrent) != 0
                || onCurrent == 0)
                continue;
            if (manager.GetWindowDesktopId(candidate, out var id) != 0 || id == Guid.Empty)
                continue;
            votes.TryGetValue(id, out var count);
            votes[id] = count + (candidate == foreground ? 2 : 1);
        }

        var best = 0;
        foreach (var (id, count) in votes)
        {
            if (count <= best)
                continue;
            best = count;
            desktop = id;
        }
        return best > 0;
    }

    /// <summary>Updates the native input shape. <paramref name="bounds"/> is in logical
    /// coordinates relative to the window's client area.</summary>
    public void SetIslandRegion(Rect bounds, bool temporarilyHidden)
    {
        _islandBounds = bounds;
        _temporarilyHidden = temporarilyHidden;
        if (_attached)
            ApplyRegion();
    }

    /// <summary>
    /// Shapes the native window to the capsule. Avalonia windows are plain rectangles to the
    /// window manager, and HTTRANSPARENT only routes input to windows of the same thread, so a
    /// window region is what lets clicks outside the capsule reach the windows underneath.
    /// </summary>
    private void ApplyRegion()
    {
        var hwnd = _hwnd;
        if (hwnd == nint.Zero)
            return;

        var scaling = _window.RenderScaling;
        if (!double.IsFinite(scaling) || scaling <= 0d)
            scaling = 1d;

        var bounds = _islandBounds;
        var hasShape = bounds.Width > 0d && bounds.Height > 0d;
        // A hidden island is already faded out; emptying the region removes its hit area as well.
        var hidden = _temporarilyHidden || !hasShape;

        if (hidden)
        {
            if (_appliedHidden)
                return;
            var empty = CreateRectRgn(0, 0, 0, 0);
            if (empty == nint.Zero)
                return;
            if (SetWindowRgn(hwnd, empty, true) == 0)
            {
                DeleteObject(empty);
                return;
            }
            _appliedHidden = true;
            _hasRegion = false;
            return;
        }
        _appliedHidden = false;

        if (_hasRegion && scaling == _appliedScaling
            && Math.Abs(bounds.X - _appliedRegion.X) < 0.5d
            && Math.Abs(bounds.Y - _appliedRegion.Y) < 0.5d
            && Math.Abs(bounds.Width - _appliedRegion.Width) < 0.5d
            && Math.Abs(bounds.Height - _appliedRegion.Height) < 0.5d)
            return;

        // The region is expressed in physical window coordinates, while Avalonia reports the
        // pill in logical client coordinates.
        var pad = (int)Math.Round(RegionHalo * scaling);
        var topPad = (int)Math.Round(RegionTopHalo * scaling);
        var offset = ClientOffset(hwnd);
        var left = (int)Math.Floor(bounds.Left * scaling) + offset.X - pad;
        var top = (int)Math.Floor(bounds.Top * scaling) + offset.Y - topPad;
        var right = (int)Math.Ceiling(bounds.Right * scaling) + offset.X + pad;
        var bottom = (int)Math.Ceiling(bounds.Bottom * scaling) + offset.Y + pad;
        var width = Math.Max(1, right - left);
        var height = Math.Max(1, bottom - top);

        var radius = height;
        var region = CreateRoundRectRgn(left, top, left + width, top + height, radius, radius);
        if (region == nint.Zero)
            return;
        if (SetWindowRgn(hwnd, region, true) == 0)
        {
            DeleteObject(region);
            return;
        }

        _hasRegion = true;
        _appliedRegion = bounds;
        _appliedScaling = scaling;
    }

    private static (int X, int Y) ClientOffset(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out var window) || !ClientOrigin(hwnd, out var client))
            return (0, 0);
        return (client.X - window.Left, client.Y - window.Top);
    }

    private nint OnWndProc(nint hwnd, uint message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmWindowPosChanging && ConstrainPosition is { } constrain)
        {
            var change = Marshal.PtrToStructure<WindowPos>(lParam);
            // Minimised and hidden windows report -32000, and SWP_NOMOVE means the position
            // was not touched at all; neither should be re-positioned.
            if ((change.flags & SwpNoMove) == 0 && change.x > -30000 && change.y > -30000)
            {
                var constrained = constrain(new PixelPoint(change.x, change.y));
                if (constrained.X != change.x || constrained.Y != change.y)
                {
                    change.x = constrained.X;
                    change.y = constrained.Y;
                    Marshal.StructureToPtr(change, lParam, false);
                }
            }
            return nint.Zero;
        }
        if (message == WmExitSizeMove && MoveCompleted is { } completed)
        {
            // Run after the loop has unwound so setting Position cannot feed back into it.
            Dispatcher.UIThread.Post(completed, DispatcherPriority.Background);
            return nint.Zero;
        }
        if (message == WmMouseActivate)
        {
            handled = true;
            return MaNoActivate;
        }
        if (message != WmNcHitTest)
            return nint.Zero;

        handled = true;
        if (_temporarilyHidden)
            return HtTransparent;
        if (!ClientOrigin(hwnd, out var origin))
            return nint.Zero;

        var screenX = (int)(short)(lParam.ToInt64() & 0xFFFF);
        var screenY = (int)(short)((lParam.ToInt64() >> 16) & 0xFFFF);
        var scaling = _window.RenderScaling;
        if (!double.IsFinite(scaling) || scaling <= 0)
            scaling = 1;
        var point = new Point(
            (screenX - origin.X) / scaling,
            (screenY - origin.Y) / scaling);
        return _islandBounds.Inflate(4).Contains(point) ? HtClient : HtTransparent;
    }

    private static bool ClientOrigin(nint hwnd, out (int X, int Y) origin)
    {
        var point = new Point32();
        if (!ClientToScreen(hwnd, ref point))
        {
            origin = default;
            return false;
        }
        origin = (point.X, point.Y);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _desktopTimer?.Stop();
        _desktopTimer = null;
        if (_desktopManager is not null)
        {
            Marshal.ReleaseComObject(_desktopManager);
            _desktopManager = null;
        }
        if (_attached)
        {
            try { Win32Properties.RemoveWndProcHookCallback(_window, _hook); }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[windows] overlay unhook failed: {exception.Message}");
            }
            _attached = false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public nint hwnd;
        public nint hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClientToScreen(nint hWnd, ref Point32 point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(nint hWnd, out Rect32 rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(nint hWnd, nint hRgn, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom,
        int widthEllipse, int heightEllipse);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint handle);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint hWnd);

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    // The published virtual desktop API. It can only ask about and move windows between
    // desktops - the id of the desktop the user is looking at has to come from a window.
    [ComImport]
    [Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A")]
    private class VirtualDesktopManager
    {
    }

    [ComImport]
    [Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig]
        int IsWindowOnCurrentVirtualDesktop(nint topLevelWindow, out int onCurrentDesktop);

        [PreserveSig]
        int GetWindowDesktopId(nint topLevelWindow, out Guid desktopId);

        [PreserveSig]
        int MoveWindowToDesktop(nint topLevelWindow, ref Guid desktopId);
    }
}
#endif
