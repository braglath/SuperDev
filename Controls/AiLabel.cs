using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace SuperDev.Controls;

public class AiLabel : StackPanel
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<AiLabel, string?>(nameof(Text));

    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<AiLabel, double>(nameof(IconSize), 16);

    public AiLabel()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 6;
        VerticalAlignment = VerticalAlignment.Center;

        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        text[!TextBlock.TextProperty] = this[!TextProperty];
        text[!TextBlock.ForegroundProperty] = this[!TextBlock.ForegroundProperty];
        text[!TextBlock.FontSizeProperty] = this[!TextBlock.FontSizeProperty];
        text[!TextBlock.FontWeightProperty] = this[!TextBlock.FontWeightProperty];

        var mark = new AiMark { VerticalAlignment = VerticalAlignment.Center };
        mark[!WidthProperty] = this[!IconSizeProperty];
        mark[!HeightProperty] = this[!IconSizeProperty];

        Children.Add(text);
        Children.Add(mark);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }
}
