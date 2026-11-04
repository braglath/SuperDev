using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SuperDev.Models;
using SuperDev.Services;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace SuperDev.Controls;

public class EvidenceGallery : WrapPanel
{
    public static readonly StyledProperty<IEnumerable<EvidenceFile>?> FilesProperty =
        AvaloniaProperty.Register<EvidenceGallery, IEnumerable<EvidenceFile>?>(nameof(Files));

    public static readonly StyledProperty<bool> AllowRemoveProperty =
        AvaloniaProperty.Register<EvidenceGallery, bool>(nameof(AllowRemove));

    public static readonly StyledProperty<bool> CompactProperty =
        AvaloniaProperty.Register<EvidenceGallery, bool>(nameof(Compact));

    private readonly List<Bitmap> _bitmaps = [];
    private bool _listening;

    static EvidenceGallery()
    {
        FilesProperty.Changed.AddClassHandler<EvidenceGallery>((gallery, _) => gallery.Rebuild());
        AllowRemoveProperty.Changed.AddClassHandler<EvidenceGallery>((gallery, _) => gallery.Rebuild());
        CompactProperty.Changed.AddClassHandler<EvidenceGallery>((gallery, _) => gallery.Rebuild());
    }

    public EvidenceGallery()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    public event EventHandler<EvidenceFile>? RemoveRequested;

    public IEnumerable<EvidenceFile>? Files
    {
        get => GetValue(FilesProperty);
        set => SetValue(FilesProperty, value);
    }

    public bool AllowRemove
    {
        get => GetValue(AllowRemoveProperty);
        set => SetValue(AllowRemoveProperty, value);
    }

    public bool Compact
    {
        get => GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_listening)
            return;

        _listening = true;
        AppearanceService.Changed += OnAppearanceChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (!_listening)
            return;

        _listening = false;
        AppearanceService.Changed -= OnAppearanceChanged;
        DisposeBitmaps();
    }

    private void OnAppearanceChanged(object? sender, EventArgs e) =>
        Rebuild();

    private void Rebuild()
    {
        Children.Clear();
        DisposeBitmaps();

        if (Files is null)
            return;

        foreach (var file in Files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.Path))
                continue;

            Children.Add(CreateCard(file));
        }
    }

    private Control CreateCard(EvidenceFile file)
    {
        var path = file.Path;
        var exists = EvidenceKind.FileExists(path);
        var name = EvidenceKind.DisplayName(path);
        Control body;
        if (!exists)
            body = MissingBody(name);
        else if (EvidenceKind.IsImage(path) && TryLoadImage(path) is { } bitmap)
            body = ImageBody(bitmap, name);
        else
            body = FileBody(name, EvidenceKind.IsVideo(path));

        var content = exists ? OpenButton(file, body) : body;
        var card = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
            Margin = new Thickness(0, 0, 8, 8),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        card.Children.Add(content);
        if (AllowRemove)
        {
            var remove = RemoveButton(file);
            Grid.SetColumn(remove, 1);
            card.Children.Add(remove);
        }

        ToolTip.SetTip(
            card,
            exists
                ? path
                : "Can't find this file. It may have been moved, renamed, or deleted." + Environment.NewLine + path);
        return card;
    }

    private Control ImageBody(Bitmap bitmap, string name)
    {
        var width = Compact ? 148 : 200;
        var frame = new Border
        {
            Width = width,
            Height = Compact ? 92 : 128,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = AppearanceService.Brush(AppearanceService.SurfaceInsetBrush),
            BorderBrush = AppearanceService.Brush(AppearanceService.BorderSubtleBrush),
            BorderThickness = new Thickness(1),
            Child = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform
            }
        };

        var caption = Caption(name, AppearanceService.TextMutedBrush);
        caption.MaxWidth = width;
        return new StackPanel
        {
            Spacing = 4,
            Children = { frame, caption }
        };
    }

    private Control FileBody(string name, bool video)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            MaxWidth = Compact ? 220 : 280
        };

        if (video)
        {
            row.Children.Add(new ShapePath
            {
                Width = 10,
                Height = 12,
                Stretch = Stretch.Uniform,
                Fill = AppearanceService.Brush(AppearanceService.AccentTextBrush),
                Data = Geometry.Parse("M1,0 L9,5 L1,10 Z"),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        var caption = Caption(name, AppearanceService.AccentTextBrush);
        caption.MaxWidth = Compact ? 190 : 250;
        row.Children.Add(caption);
        return row;
    }

    private Control MissingBody(string name)
    {
        var badge = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = AppearanceService.Brush(AppearanceService.DangerSurfaceBrush),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "!",
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                Foreground = AppearanceService.Brush(AppearanceService.DangerTextBrush),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var label = Caption(name, AppearanceService.DangerTextBrush);
        label.MaxWidth = Compact ? 190 : 250;
        label.TextWrapping = TextWrapping.Wrap;
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            MaxWidth = Compact ? 220 : 280,
            Children = { badge, label }
        };
    }

    private static TextBlock Caption(string name, string brush) =>
        new()
        {
            Text = name,
            FontSize = AppearanceService.Font(12),
            Foreground = AppearanceService.Brush(brush),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };

    private Button OpenButton(EvidenceFile file, Control body)
    {
        var button = new Button
        {
            Content = body,
            Tag = file,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        button.Click += Open_Click;
        return button;
    }

    private Button RemoveButton(EvidenceFile file)
    {
        var button = new Button
        {
            Content = "×",
            Tag = file,
            Width = 22,
            Height = 22,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = AppearanceService.Brush(AppearanceService.CardBrush),
            Foreground = AppearanceService.Brush(AppearanceService.TextMutedBrush),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            FontSize = AppearanceService.Font(14),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(button, "Remove");
        button.Click += Remove_Click;
        return button;
    }

    private void Open_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EvidenceFile file })
            return;

        if (!EvidenceKind.FileExists(file.Path))
        {
            Rebuild();
            return;
        }

        LocalFile.TryOpen(file.Path);
    }

    private void Remove_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: EvidenceFile file })
            RemoveRequested?.Invoke(this, file);
    }

    private Bitmap? TryLoadImage(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var bitmap = Bitmap.DecodeToWidth(stream, Compact ? 320 : 440);
            _bitmaps.Add(bitmap);
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void DisposeBitmaps()
    {
        foreach (var bitmap in _bitmaps)
            bitmap.Dispose();

        _bitmaps.Clear();
    }
}
