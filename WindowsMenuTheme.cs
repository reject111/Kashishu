#if WINDOWS
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Kashishu;

/// <summary>Opt native popup menus into the user's Windows app colour mode.</summary>
internal static class WindowsMenuTheme
{
    // Win32 popup menus require the UXTheme application opt-in on Windows 10 1903+.
    // These exports are optional: older systems keep their normal system menu rendering.
    private static readonly nint ThemeLibrary = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362)
        ? LoadLibraryEx("uxtheme.dll", 0, 0x800) : 0; // Search System32 only; retain delegates for app lifetime.
    private static readonly PreferredMode? SetPreferredAppMode = Export<PreferredMode>(135);
    private static readonly WindowMode? AllowDarkModeForWindow = Export<WindowMode>(133);
    private static readonly FlushThemes? FlushMenuThemes = Export<FlushThemes>(136);

    public static void Apply(nint owner)
    {
        var dark = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        // High contrast always uses the accessibility palette instead of a forced app theme.
        var contrast = new HighContrast { Size = (uint)Marshal.SizeOf<HighContrast>() };
        if (SystemParametersInfo(0x0042, contrast.Size, ref contrast, 0) && (contrast.Flags & 1) != 0)
            dark = false;
        if (SetPreferredAppMode is null || AllowDarkModeForWindow is null || FlushMenuThemes is null) return;
        SetPreferredAppMode(dark ? 2 : 3); // ForceDark / ForceLight from the current system app preference.
        AllowDarkModeForWindow(owner, dark);
        SetWindowTheme(owner, dark ? "DarkMode_Explorer" : "Explorer", null);
        FlushMenuThemes();
    }

    private static T? Export<T>(int ordinal) where T : Delegate
    {
        var address = ThemeLibrary == 0 ? 0 : GetProcAddress(ThemeLibrary, (nint)ordinal);
        return address == 0 ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int PreferredMode(int mode);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool WindowMode(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool allow);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void FlushThemes();
    [StructLayout(LayoutKind.Sequential)] private struct HighContrast { public uint Size, Flags; public nint Scheme; }
    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode)] private static extern nint LoadLibraryEx(string name, nint file, uint flags);
    [DllImport("kernel32.dll")] private static extern nint GetProcAddress(nint module, nint ordinal);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(nint hwnd, string app, string? id);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] private static extern bool SystemParametersInfo(uint action, uint parameter, ref HighContrast value, uint flags);
}
#endif
