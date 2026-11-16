#if WINDOWS
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Runtime.InteropServices;

namespace Kashishu;

internal static class WindowsTitleBar
{
    public static void Apply(Window window)
    {
        window.ExtendClientAreaToDecorationsHint = true;
        window.ExtendClientAreaTitleBarHeightHint = 62;
        window.Resources["TitleBarBackgroundBrush"] = Brushes.Transparent;
        var theme = new ControlTheme(typeof(WindowDrawnDecorations))
        {
            BasedOn = (ControlTheme)Application.Current!.FindResource(typeof(WindowDrawnDecorations))!
        };
        theme.Children.Add(new Style(x => x.Nesting().Template().OfType<Panel>().Name("PART_TitleTextPanel"))
        {
            // IsVisible has a template binding; opacity hides only this title overlay
            // without competing with that local-value binding.
            Setters = { new Setter(Visual.OpacityProperty, 0d) }
        });
        theme.Children.Add(new Style(x => x.Nesting().Template().OfType<StackPanel>().Name("PART_OverlayPanel"))
        {
            Setters = { new Setter(Layoutable.MarginProperty, new Thickness(0, 7, 22, 0)) }
        });
        window.WindowDecorationsTheme = theme;
    }

    public static void AnimateFullscreen(Control content)
    {
        if (SystemParametersInfo(0x1042, 0, out var animations, 0) && !animations) return;
        var scale = new ScaleTransform(.98, .98);
        content.RenderTransformOrigin = RelativePoint.Center;
        content.RenderTransform = scale;
        content.Transitions = null;
        content.Opacity = .72;
        Dispatcher.UIThread.Post(() =>
        {
            var duration = TimeSpan.FromMilliseconds(240);
            scale.Transitions = new Transitions
            {
                new DoubleTransition { Property = ScaleTransform.ScaleXProperty, Duration = duration, Easing = new CubicEaseOut() },
                new DoubleTransition { Property = ScaleTransform.ScaleYProperty, Duration = duration, Easing = new CubicEaseOut() }
            };
            content.Transitions = new Transitions { new DoubleTransition { Property = Visual.OpacityProperty, Duration = duration } };
            scale.ScaleX = scale.ScaleY = 1;
            content.Opacity = 1;
        }, DispatcherPriority.Render);
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static extern bool SystemParametersInfo(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);
}
#endif
