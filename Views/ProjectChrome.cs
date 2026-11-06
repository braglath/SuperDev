using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using SuperDev.Models;
using SuperDev.Services;

namespace SuperDev.Views;

internal static class ProjectChrome
{
    public static SolidColorBrush Brush(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var color))
            return new SolidColorBrush(color);

        return new SolidColorBrush(Color.Parse(ProjectAppearance.DefaultColor));
    }

    public static Control Icon(string? key, IBrush stroke, double size = 16)
    {
        var data = key switch
        {
            "Code" => "M6,4 L2,8 L6,12 M10,4 L14,8 L10,12",
            "Bug" => "M8,3.2 A2.4,2.4 0 1 1 7.9,3.2 M8,8.2 V13 M5.4,6.4 L2.6,4.2 M10.6,6.4 L13.4,4.2 M5.4,10.4 L2.6,12.4 M10.6,10.4 L13.4,12.4",
            "Rocket" => "M8,1.8 C11,4.6 12,8 12,11 L8,14.2 L4,11 C4,8 5,4.6 8,1.8 Z M6.4,11.2 L8,13 L9.6,11.2",
            "Shield" => "M8,1.8 L13.2,4 V8.6 C13.2,11.6 8,14.2 8,14.2 C8,14.2 2.8,11.6 2.8,8.6 V4 Z",
            "Book" => "M2.5,2.8 H7.2 C8.4,2.8 9.2,3.8 9.2,3.8 V13.2 H2.5 Z M9.2,3.8 C9.2,3.8 10.2,2.8 12,2.8 H13.5 V13.2 H9.2",
            "Spark" => "M8,1.4 L9.3,6.1 L14,8 L9.3,9.9 L8,14.6 L6.7,9.9 L2,8 L6.7,6.1 Z",
            "Box" => "M1.8,5.4 L8,2.2 L14.2,5.4 L8,8.6 Z M1.8,5.4 V11.2 L8,14.2 V8.6 M14.2,5.4 V11.2 L8,14.2",
            _ => "M2,5 H6.2 L7.6,3.2 H14 V12.8 H2 Z"
        };

        return new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = new Path
            {
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                Stroke = stroke,
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                Fill = Brushes.Transparent,
                Data = Geometry.Parse(data)
            }
        };
    }
}
