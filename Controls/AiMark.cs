using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using SuperDev.Services;

namespace SuperDev.Controls;

public class AiMark : Control
{
    private static readonly Art Mark = Load();
    private readonly Dictionary<string, AvaloniaObject> _brushes = new();

    public AiMark()
    {
        Width = 16;
        Height = 16;
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
            return;

        var scale = Math.Min(width / Mark.Width, height / Mark.Height);
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            foreach (var piece in Mark.Pieces)
            {
                var brush = Brush(piece.Role);
                if (brush is null)
                    continue;

                Pen? pen = piece.Stroke
                    ? new Pen(brush, piece.Thickness, lineCap: piece.Cap, lineJoin: piece.Join)
                    : null;
                context.DrawGeometry(piece.Fill ? brush : null, pen, piece.Geometry);
            }
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        foreach (var brush in _brushes.Values)
            brush.PropertyChanged -= OnBrushChanged;
        _brushes.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    private IBrush? Brush(string role)
    {
        if (Application.Current?.Resources.TryGetValue(BrushKey(role), out var value) != true || value is not IBrush brush)
            return null;

        if (brush is AvaloniaObject source && (!_brushes.TryGetValue(role, out var watched) || !ReferenceEquals(watched, source)))
        {
            if (watched is not null)
                watched.PropertyChanged -= OnBrushChanged;
            source.PropertyChanged += OnBrushChanged;
            _brushes[role] = source;
        }

        return brush;
    }

    private void OnBrushChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == SolidColorBrush.ColorProperty)
            InvalidateVisual();
    }

    private static string BrushKey(string role) => role switch
    {
        "surface" => AppearanceService.SurfaceBrush,
        "ink" => AppearanceService.TextPrimaryBrush,
        "on-accent" => AppearanceService.TextOnAccentBrush,
        "muted" => AppearanceService.TextMutedBrush,
        _ => AppearanceService.AccentBrush
    };

    private static Art Load()
    {
        using var stream = AssetLoader.Open(new Uri("avares://Super Dev/Assets/ai-mark.svg"));
        var root = XDocument.Load(stream).Root ?? throw new InvalidOperationException("AI icon is empty.");
        var box = (root.Attribute("viewBox")?.Value ?? "0 0 64 64")
            .Split((char[])null!, StringSplitOptions.RemoveEmptyEntries);
        var width = Parse(box[2]);
        var height = Parse(box[3]);
        var pieces = new List<Piece>();
        foreach (var element in root.Elements())
        {
            var piece = Read(element);
            if (piece is not null)
                pieces.Add(piece);
        }

        return new Art(pieces, width, height);
    }

    private static Piece? Read(XElement element)
    {
        var role = element.Attribute("class")?.Value.Trim();
        if (string.IsNullOrEmpty(role))
            return null;

        var filled = !string.Equals(element.Attribute("fill")?.Value, "none", StringComparison.OrdinalIgnoreCase);
        var stroked = string.Equals(element.Attribute("fill")?.Value, "none", StringComparison.OrdinalIgnoreCase)
                      || element.Attribute("stroke") is not null;
        if (!filled && !stroked)
            return null;

        var geometry = element.Name.LocalName switch
        {
            "path" => Geometry.Parse(element.Attribute("d")?.Value ?? string.Empty),
            "rect" => new RectangleGeometry(
                new Rect(Attr(element, "x"), Attr(element, "y"), Attr(element, "width"), Attr(element, "height")),
                Attr(element, "rx"),
                element.Attribute("ry") is null ? Attr(element, "rx") : Attr(element, "ry")),
            "circle" => Circle(Attr(element, "cx"), Attr(element, "cy"), Attr(element, "r")),
            _ => null
        };
        if (geometry is null)
            return null;

        return new Piece(
            geometry,
            role,
            filled,
            stroked,
            element.Attribute("stroke-width") is null ? 1.5 : Attr(element, "stroke-width"),
            Cap(element.Attribute("stroke-linecap")?.Value),
            Join(element.Attribute("stroke-linejoin")?.Value));
    }

    private static Geometry Circle(double cx, double cy, double radius) =>
        new EllipseGeometry(new Rect(cx - radius, cy - radius, radius * 2, radius * 2));

    private static double Attr(XElement element, string name) =>
        element.Attribute(name) is { Value: var value } ? Parse(value) : 0;

    private static double Parse(string value) =>
        double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static PenLineCap Cap(string? value) => value switch
    {
        "square" => PenLineCap.Square,
        "butt" => PenLineCap.Flat,
        _ => PenLineCap.Round
    };

    private static PenLineJoin Join(string? value) => value switch
    {
        "bevel" => PenLineJoin.Bevel,
        "miter" => PenLineJoin.Miter,
        _ => PenLineJoin.Round
    };

    private sealed record Art(IReadOnlyList<Piece> Pieces, double Width, double Height);

    private sealed record Piece(
        Geometry Geometry,
        string Role,
        bool Fill,
        bool Stroke,
        double Thickness,
        PenLineCap Cap,
        PenLineJoin Join);
}
