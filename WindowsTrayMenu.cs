#if WINDOWS
using System.Runtime.InteropServices;

namespace Kashishu;

/// <summary>Windows owns menu layout, input, accessibility, checks and submenus.</summary>
internal static class WindowsTrayMenu
{
    public static void Show(nint owner, IReadOnlyList<TrayMenuEntry> entries)
    {
        WindowsMenuTheme.Apply(owner);
        var commands = new Dictionary<uint, Action>();
        uint nextId = 1;
        nint Build(IReadOnlyList<TrayMenuEntry> rows)
        {
            var menu = CreatePopupMenu();
            if (menu == 0) throw new InvalidOperationException("CreatePopupMenu failed");
            try
            {
                foreach (var entry in rows)
                {
                    uint flags = entry.IsSeparator ? 0x800u : 0;
                    if (!entry.IsEnabled) flags |= 0x3;
                    if (entry.IsChecked) flags |= 0x8;
                    if (entry.Children.Count > 0)
                    {
                        var child = Build(entry.Children);
                        if (!AppendMenu(menu, flags | 0x10, child, entry.Header))
                        { DestroyMenu(child); throw new InvalidOperationException("AppendMenu failed"); }
                    }
                    else
                    {
                        var id = nextId++;
                        if (entry.Invoke is { } invoke) commands.Add(id, invoke);
                        if (!AppendMenu(menu, flags, (nint)id, entry.Header))
                            throw new InvalidOperationException("AppendMenu failed");
                    }
                }
                return menu;
            }
            catch { DestroyMenu(menu); throw; }
        }
        var popup = Build(entries);
        try
        {
            GetCursorPos(out var point);
            SetForegroundWindow(owner);
            var selected = TrackPopupMenuEx(popup, 0x0100 | 0x0002, point.X, point.Y, owner, 0);
            PostMessage(owner, 0, 0, 0); // Allow the next tray popup to dismiss normally.
            if (commands.TryGetValue(selected, out var command)) command();
        }
        finally { DestroyMenu(popup); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nint id, string text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, nint parameters);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
#endif
