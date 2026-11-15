using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Kashishu;

/// <summary>One row of the custom tray menu, rebuilt from live app state each time it opens.</summary>
internal sealed class TrayMenuEntry
{
    public TrayMenuEntry(string header, Action? invoke = null, bool enabled = true, bool check = false,
        IReadOnlyList<TrayMenuEntry>? children = null, bool stayOpen = false)
    {
        Header = header;
        Invoke = invoke;
        IsEnabled = enabled;
        IsChecked = check;
        Children = children ?? [];
        StayOpen = stayOpen;
    }

    public string Header { get; }
    public Action? Invoke { get; }
    public bool IsEnabled { get; }
    public bool IsChecked { get; }
    public IReadOnlyList<TrayMenuEntry> Children { get; }
    public bool StayOpen { get; }
    public bool IsSeparator { get; private init; }

    public static TrayMenuEntry Separator() => new(string.Empty) { IsSeparator = true };
}

/// <summary>
/// A dark, rounded tray menu drawn with Avalonia instead of the native Win32 popup.
/// Submenus open as extra columns attached to the right of the hovered row.
/// </summary>
internal sealed class TrayMenuWindow : Window
{
    private const double ColumnWidth = 208;
    private static readonly IBrush ActiveBackground =
        new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255));
    private static readonly IBrush SeparatorBackground =
        new SolidColorBrush(Color.FromArgb(0x1C, 255, 255, 255));

    private readonly StackPanel _columns;
    private readonly Func<IReadOnlyList<TrayMenuEntry>>? _rebuild;
    private readonly Dictionary<int, Border> _activeRows = [];
    private PixelPoint _anchor;
    private long _ignoreDeactivateUntil;

    public TrayMenuWindow(IReadOnlyList<TrayMenuEntry> entries, Func<IReadOnlyList<TrayMenuEntry>>? rebuild = null)
    {
        _rebuild = rebuild;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Focusable = true;
        TextElement.SetForeground(this, LyricsTheme.PrimaryText);
        TextElement.SetFontFamily(this, new FontFamily(PlatformInfo.CjkFontFamily));
        TextElement.SetFontSize(this, 13);

        _columns = new StackPanel { Orientation = Orientation.Horizontal };
        AddColumn(entries, 0);

        var panel = new Border
        {
            Background = LyricsTheme.DropDownBackground,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(6),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 6,
                Blur = 22,
                Color = Color.FromArgb(0x6E, 0, 0, 0)
            }),
            Child = _columns
        };
        Content = new Border { Margin = new Thickness(14), Child = panel };

        Deactivated += (_, _) =>
        {
            // An entry that opens another window makes this one lose activation first; the menu
            // is meant to stay up in that case, so the deactivation it causes is ignored.
            if (Environment.TickCount64 < _ignoreDeactivateUntil)
                return;
            Close();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
    }

    public void ShowAtCursor()
    {
        var cursor = CursorPosition();
        _anchor = cursor;
        Opacity = 0;
        Position = new PixelPoint(Math.Max(0, cursor.X - 220), Math.Max(0, cursor.Y - 40));
        Show();
        Activate();
        Dispatcher.UIThread.Post(() =>
        {
            PlaceNear(cursor);
            Opacity = 1;
            Focus();
        }, DispatcherPriority.Loaded);
    }

    private void PlaceNear(PixelPoint cursor)
    {
        var scaling = RenderScaling > 0 ? RenderScaling : 1d;
        var width = (int)Math.Ceiling(ClientSize.Width * scaling);
        var height = (int)Math.Ceiling(ClientSize.Height * scaling);
        var screen = Screens.ScreenFromPoint(cursor) ?? Screens.Primary;
        if (screen is null)
            return;

        var area = screen.WorkingArea;
        var margin = (int)Math.Round(14 * scaling);
        var gap = (int)Math.Round(4 * scaling);
        // Sit above the taskbar with the menu's right edge roughly under the cursor.
        var x = cursor.X - width + margin;
        var y = cursor.Y - height - gap;
        if (y < area.Y)
            y = cursor.Y + gap;
        x = Math.Clamp(x, area.X + 2, Math.Max(area.X + 2, area.Right - width - 2));
        y = Math.Clamp(y, area.Y + 2, Math.Max(area.Y + 2, area.Bottom - height - 2));
        Position = new PixelPoint(x, y);
    }

    private void AddColumn(IReadOnlyList<TrayMenuEntry> entries, int depth, double topOffset = 0)
    {
        var column = new StackPanel
        {
            Width = ColumnWidth,
            Margin = new Thickness(depth == 0 ? 0 : 6, 0, 0, 0)
        };
        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                column.Children.Add(new Border
                {
                    Height = 1,
                    Background = SeparatorBackground,
                    Margin = new Thickness(8, 5)
                });
                continue;
            }

            column.Children.Add(CreateRow(entry, depth));
        }

        if (depth == 0)
        {
            _columns.Children.Add(column);
        }
        else
        {
            _columns.Children.Add(new Border
            {
                BorderBrush = SeparatorBackground,
                BorderThickness = new Thickness(1, 0, 0, 0),
                // Line the submenu's first row up with the row that opened it.
                Margin = new Thickness(0, topOffset, 0, 0),
                Child = column
            });
        }
    }

    private Control CreateRow(TrayMenuEntry entry, int depth)
    {
        var check = new TextBlock
        {
            Text = entry.IsChecked ? "✓" : string.Empty,
            FontSize = 12,
            Foreground = LyricsTheme.Accent,
            VerticalAlignment = VerticalAlignment.Center
        };
        var label = new TextBlock
        {
            Text = entry.Header,
            FontSize = 13,
            Foreground = LyricsTheme.PrimaryText,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var arrow = new TextBlock
        {
            Text = entry.Children.Count > 0 ? "›" : string.Empty,
            FontSize = 15,
            Foreground = LyricsTheme.MutedText,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("18,*,12"),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { check, label, arrow }
        };
        Grid.SetColumn(label, 1);
        Grid.SetColumn(arrow, 2);

        var row = new Border
        {
            MinHeight = 32,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 0),
            Background = Brushes.Transparent,
            Child = grid
        };
        if (!entry.IsEnabled)
        {
            row.Opacity = 0.42;
            return row;
        }

        row.PointerEntered += (_, _) =>
        {
            SetActive(depth, row);
            if (entry.Children.Count > 0)
                OpenSubmenu(depth, row, entry.Children);
            else
                TrimColumns(depth + 1);
        };
        row.PointerExited += (_, _) =>
        {
            if (!_activeRows.TryGetValue(depth, out var active) || !ReferenceEquals(active, row))
                row.Background = Brushes.Transparent;
        };
        row.PointerPressed += (_, args) =>
        {
            if (args.GetCurrentPoint(row).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
                return;
            args.Handled = true;
            if (entry.Children.Count > 0)
            {
                SetActive(depth, row);
                OpenSubmenu(depth, row, entry.Children);
                return;
            }

            if (entry.Invoke is null)
                return;
            // Only the top-level toggles stay up; a pick inside a submenu closes the menu like
            // any other command, since the column it came from is replaced by the rebuild.
            if (entry.StayOpen && depth == 0 && _rebuild is not null)
            {
                _ignoreDeactivateUntil = Environment.TickCount64 + 500;
                entry.Invoke();
                Refresh();
                Dispatcher.UIThread.Post(() =>
                {
                    if (IsVisible && !IsActive)
                        Activate();
                }, DispatcherPriority.Background);
                return;
            }
            Close();
            entry.Invoke();
        };
        return row;
    }

    /// <summary>Rebuilds the menu in place after a checkable entry changed state.</summary>
    private void Refresh()
    {
        if (_rebuild is null)
            return;
        _activeRows.Clear();
        _columns.Children.Clear();
        AddColumn(_rebuild(), 0);
        Dispatcher.UIThread.Post(() => PlaceNear(_anchor), DispatcherPriority.Loaded);
    }

    private void OpenSubmenu(int depth, Border row, IReadOnlyList<TrayMenuEntry> entries)
    {
        TrimColumns(depth + 1);
        var offset = row.TranslatePoint(new Point(0, 0), _columns)?.Y ?? 0;
        AddColumn(entries, depth + 1, offset);
    }

    private void TrimColumns(int keep)
    {
        while (_columns.Children.Count > keep)
            _columns.Children.RemoveAt(_columns.Children.Count - 1);

        foreach (var key in _activeRows.Keys.Where(key => key >= keep).ToArray())
        {
            _activeRows[key].Background = Brushes.Transparent;
            _activeRows.Remove(key);
        }
    }

    private void SetActive(int depth, Border row)
    {
        foreach (var key in _activeRows.Keys.Where(key => key >= depth).ToArray())
        {
            _activeRows[key].Background = Brushes.Transparent;
            _activeRows.Remove(key);
        }

        _activeRows[depth] = row;
        row.Background = ActiveBackground;
    }

    private static PixelPoint CursorPosition()
    {
        if (OperatingSystem.IsWindows() && GetCursorPos(out var point))
            return new PixelPoint(point.X, point.Y);
        return new PixelPoint(0, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point32 point);
}
