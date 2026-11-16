using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Kashishu;

/// <summary>
/// Embeds AppKit's title bar in the cover-derived header. The standard buttons remain
/// owned by NSWindow, including their tracking areas, actions and accessibility peers.
/// </summary>
internal static class MacTitleBar
{
    private const string LibObjC = "/usr/lib/libobjc.dylib";
    public const double HeaderHeight = 54;

    public static void Attach(Window window)
    {
        if (!OperatingSystem.IsMacOS() || window.TryGetPlatformHandle()?.Handle is not { } handle
            || handle == nint.Zero) return;

        // A unified toolbar lets AppKit choose the traffic-light metrics for this OS.
        // No custom button views, frame offsets or private titlebar subviews are needed.
        var identifier = SendString(GetClass("NSString"), Selector("stringWithUTF8String:"),
            "com.kashishu.lyrics-toolbar");
        var toolbar = SendPointer(Send(GetClass("NSToolbar"), Selector("alloc")),
            Selector("initWithIdentifier:"), identifier);
        try
        {
            SendBool(toolbar, Selector("setShowsBaselineSeparator:"), false);
            SendBool(toolbar, Selector("setAllowsUserCustomization:"), false);
            SendInteger(handle, Selector("setToolbarStyle:"), 3); // NSWindowToolbarStyleUnified
            SendPointer(handle, Selector("setToolbar:"), toolbar);
            SendInteger(handle, Selector("setTitleVisibility:"), 1); // NSWindowTitleHidden
            SendBool(handle, Selector("setTitlebarAppearsTransparent:"), true);
            SendInteger(handle, Selector("setTitlebarSeparatorStyle:"), 0); // None
        }
        finally
        {
            // NSWindow retains its toolbar; balance our alloc/init ownership.
            Send(toolbar, Selector("release"));
        }
    }

    public static void PerformTitleBarDoubleClick(Window window)
    {
        if (!OperatingSystem.IsMacOS() || window.TryGetPlatformHandle()?.Handle is not { } handle
            || handle == nint.Zero) return;

        // Avalonia's managed extended-titlebar gesture always maximizes. Read the
        // system preference each time so changes in System Settings take effect immediately.
        var defaults = Send(GetClass("NSUserDefaults"), Selector("standardUserDefaults"));
        var key = SendString(GetClass("NSString"), Selector("stringWithUTF8String:"), "AppleActionOnDoubleClick");
        var value = SendPointer(defaults, Selector("stringForKey:"), key);
        var action = value == nint.Zero ? null : Marshal.PtrToStringUTF8(Send(value, Selector("UTF8String")));
        if (string.Equals(action, "None", StringComparison.OrdinalIgnoreCase)) return;
        SendPointer(handle, Selector(string.Equals(action, "Minimize", StringComparison.OrdinalIgnoreCase)
            ? "performMiniaturize:" : "performZoom:"), nint.Zero);
    }

    public static void UpdateWindowState(Window window)
    {
        if (!OperatingSystem.IsMacOS() || window.TryGetPlatformHandle()?.Handle is not { } handle
            || handle == nint.Zero) return;

        // Avalonia resets transparency when entering fullscreen. An empty native toolbar
        // must not cover our app controls there; AppKit still provides its floating titlebar.
        var toolbar = Send(handle, Selector("toolbar"));
        SendBool(toolbar, Selector("setVisible:"), window.WindowState != WindowState.FullScreen);
        SendBool(handle, Selector("setTitlebarAppearsTransparent:"), true);
    }

    [DllImport(LibObjC, EntryPoint = "objc_getClass")]
    private static extern nint GetClass(string name);

    [DllImport(LibObjC, EntryPoint = "sel_registerName")]
    private static extern nint Selector(string name);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern nint SendString(nint receiver, nint selector,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern nint SendPointer(nint receiver, nint selector, nint value);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendInteger(nint receiver, nint selector, nint value);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendBool(nint receiver, nint selector,
        [MarshalAs(UnmanagedType.I1)] bool value);
}
