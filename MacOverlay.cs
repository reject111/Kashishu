using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace LyricifyIsland;

/// <summary>
/// macOS counterpart of <see cref="NativeOverlay"/> and <see cref="WindowsOverlay"/>.
///
/// Avalonia's transparent window is a normal rectangle to the window server, so the empty
/// area around the capsule would swallow clicks. macOS has no per-window input shape, but it
/// can drop mouse events for the whole window; the cursor is sampled a few times a second and
/// the window only accepts clicks while the pointer is over the capsule. The window is also
/// floated above normal windows, follows every Space including full-screen apps, and is kept
/// out of the activation chain so clicking the island never pulls focus from another app.
/// </summary>
internal sealed class MacOverlay : IDisposable
{
    private const string LibObjC = "/usr/lib/libobjc.dylib";
    private const string CoreGraphics =
        "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const nint StatusWindowLevel = 25;
    private const nuint WindowCollectionBehavior =
        (1 << 0)  // CanJoinAllSpaces
        | (1 << 4) // Stationary
        | (1 << 6) // IgnoresCycle
        | (1 << 8); // FullScreenAuxiliary
    private const string SubclassSuffix = "LyricifyOverlayWindow";
    private static readonly Lazy<IntPtr> OverlayWindowClass = new(CreateOverlayWindowClass);
    private static readonly BoolMethod CanBecomeKey = static (_, _) => 0;
    private static readonly BoolMethod CanBecomeMain = static (_, _) => 0;
    private static readonly BoolMethod AcceptsFirstMouse = static (_, _) => 1;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte BoolMethod(IntPtr self, IntPtr selector);

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGEventCreate(IntPtr source);

    [DllImport(CoreGraphics)]
    private static extern CGPoint CGEventGetLocation(IntPtr eventRef);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr value);

    [DllImport(LibObjC, EntryPoint = "sel_registerName")]
    private static extern IntPtr Selector(string name);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoidNInt(IntPtr receiver, IntPtr selector, nint value);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoidNUInt(IntPtr receiver, IntPtr selector, nuint value);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoidByte(IntPtr receiver, IntPtr selector, byte value);

    [DllImport(LibObjC, EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass(string name);

    [DllImport(LibObjC, EntryPoint = "objc_allocateClassPair")]
    private static extern IntPtr AllocateClassPair(IntPtr superclass, string name, nuint extraBytes);

    [DllImport(LibObjC, EntryPoint = "objc_registerClassPair")]
    private static extern void RegisterClassPair(IntPtr value);

    [DllImport(LibObjC, EntryPoint = "class_addMethod")]
    private static extern byte AddMethod(IntPtr cls, IntPtr selector, IntPtr implementation, string types);

    [DllImport(LibObjC, EntryPoint = "object_setClass")]
    private static extern IntPtr SetClass(IntPtr value, IntPtr cls);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CGPoint(double x, double y)
    {
        public readonly double X = x;
        public readonly double Y = y;
    }

    private readonly Window _window;
    private readonly DispatcherTimer _mouseTimer;
    private Rect _islandBounds;
    private bool _forcedPassThrough;
    // A fresh NSWindow accepts mouse events, so the first pass-through update must really apply.
    private bool _appliedIgnore;
    private nint _nsWindow;
    private bool _attached;
    private bool _disposed;

    public MacOverlay(Window window)
    {
        _window = window;
        _mouseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _mouseTimer.Tick += (_, _) => UpdateInteraction();
    }

    public void Attach()
    {
        if (_attached || _disposed)
            return;
        var handle = _window.TryGetPlatformHandle()?.Handle ?? nint.Zero;
        if (handle == nint.Zero)
        {
            // The native handle exists once the window is shown.
            Dispatcher.UIThread.Post(Attach, DispatcherPriority.Loaded);
            return;
        }

        try
        {
            _nsWindow = handle;
            SendVoidNInt(handle, Selector("setLevel:"), StatusWindowLevel);
            SendVoidNUInt(handle, Selector("setCollectionBehavior:"), WindowCollectionBehavior);
            SendVoidByte(handle, Selector("setHasShadow:"), 0);
            SendVoidByte(handle, Selector("setHidesOnDeactivate:"), 0);
            SendVoid(handle, Selector("orderFrontRegardless"));
            SetIgnoreMouse(true);
            AdoptSubclass(handle);
            _attached = true;
            _mouseTimer.Start();
            UpdateInteraction();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[macos] overlay unavailable: {exception.Message}");
        }
    }

    /// <summary>Updates the hit area. <paramref name="bounds"/> is in logical coordinates
    /// relative to the window's client area.</summary>
    public void SetIslandRegion(Rect bounds, bool passThrough)
    {
        _islandBounds = bounds;
        _forcedPassThrough = passThrough;
        if (_attached)
            UpdateInteraction();
    }

    private void UpdateInteraction()
    {
        if (!_attached || _disposed)
            return;
        var interactive = !_forcedPassThrough
            && _islandBounds.Width > 0d && _islandBounds.Height > 0d
            && _window.IsVisible
            && TryMouseLocation(out var mouse)
            && IslandRenderer.HitTest(_islandBounds,
                new Point(mouse.X - _window.Position.X, mouse.Y - _window.Position.Y));
        SetIgnoreMouse(!interactive);
    }

    private static bool TryMouseLocation(out Point point)
    {
        var eventRef = CGEventCreate(nint.Zero);
        if (eventRef == nint.Zero)
        {
            point = default;
            return false;
        }
        try
        {
            var location = CGEventGetLocation(eventRef);
            point = new Point(location.X, location.Y);
            return double.IsFinite(location.X) && double.IsFinite(location.Y);
        }
        finally
        {
            CFRelease(eventRef);
        }
    }

    private void SetIgnoreMouse(bool ignore)
    {
        if (_nsWindow == nint.Zero || _appliedIgnore == ignore)
            return;
        try
        {
            SendVoidByte(_nsWindow, Selector("setIgnoresMouseEvents:"), ignore ? (byte)1 : (byte)0);
            _appliedIgnore = ignore;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[macos] mouse pass-through unavailable: {exception.Message}");
        }
    }

    /// <summary>
    /// Keeps the island out of the activation chain. The borderless window would otherwise
    /// become key on a click, pull the app to the front and take keyboard focus from whatever
    /// the user was doing; <c>acceptsFirstMouse:</c> still delivers that first click.
    /// </summary>
    private static void AdoptSubclass(nint window)
    {
        try
        {
            var subclass = OverlayWindowClass.Value;
            if (subclass == nint.Zero)
                return;
            SetClass(window, subclass);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[macos] window subclass unavailable: {exception.Message}");
        }
    }

    private static IntPtr CreateOverlayWindowClass()
    {
        try
        {
            // AvnWindow is Avalonia's NSWindow subclass; derive from it so its own overrides
            // keep working instead of replacing them with NSWindow's.
            var superclass = GetClass("AvnWindow");
            if (superclass == nint.Zero)
                superclass = GetClass("NSWindow");
            if (superclass == nint.Zero)
                return nint.Zero;
            var unique = $"{SubclassSuffix}_{Environment.ProcessId}";
            var subclass = AllocateClassPair(superclass, unique, 0);
            if (subclass == nint.Zero)
                return nint.Zero;
            AddMethod(subclass, Selector("canBecomeKeyWindow"),
                Marshal.GetFunctionPointerForDelegate(CanBecomeKey), "c@:");
            AddMethod(subclass, Selector("canBecomeMainWindow"),
                Marshal.GetFunctionPointerForDelegate(CanBecomeMain), "c@:");
            AddMethod(subclass, Selector("acceptsFirstMouse:"),
                Marshal.GetFunctionPointerForDelegate(AcceptsFirstMouse), "c@:@");
            RegisterClassPair(subclass);
            return subclass;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[macos] window subclass creation failed: {exception.Message}");
            return nint.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _mouseTimer.Stop();
        SetIgnoreMouse(false);
        _attached = false;
    }
}
