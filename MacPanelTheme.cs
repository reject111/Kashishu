using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Styling;

namespace Kashishu;

internal static class MacPanelTheme
{
    internal static void DrawBadgeLabel(SkiaSharp.SKCanvas canvas, SkiaSharp.SKRect badge,
        SkiaSharp.SKFont font, SkiaSharp.SKPaint paint)
    {
        font.MeasureText("AI", out var ink);
        canvas.DrawText("AI", badge.MidX - ink.MidX, badge.MidY - ink.MidY, font, paint);
    }

    internal static void Apply(Control host)
    {
        // Fluent's editable combo centers its editor but leaves the text at the top.
        host.Styles.Add(new Style(x => x.OfType<ComboBox>())
        {
            Setters =
            {
                new Setter(ComboBox.VerticalContentAlignmentProperty, VerticalAlignment.Center),
                new Setter(ComboBox.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)
            }
        });
        host.Styles.Add(new Style(x => x.OfType<ComboBox>().Template().OfType<TextBox>().Name("PART_EditableTextBox"))
        {
            Setters =
            {
                new Setter(TextBox.VerticalContentAlignmentProperty, VerticalAlignment.Center),
                new Setter(TemplatedControl.MinHeightProperty, 0d)
            }
        });
    }
}
