using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using SuperDev.Services;

namespace SuperDev.Controls;

public class NoteText : TextBlock
{
    private static readonly Regex UrlPattern = new(
        @"(?<![A-Za-z0-9@])(?:https?://|www\.)[^\s<>""]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly IBrush LinkBrush = AppearanceService.Brush("AccentTextBrush");

    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<NoteText, string?>(nameof(Source));

    private readonly List<LinkSpan> _links = [];

    static NoteText()
    {
        SourceProperty.Changed.AddClassHandler<NoteText>((note, _) => note.Rebuild());
    }

    public NoteText()
    {
        TextWrapping = TextWrapping.Wrap;
    }

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Cursor = LinkAt(TextIndexAt(e.GetPosition(this))) is null ? null : HandCursor;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Cursor = null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var url = LinkAt(TextIndexAt(e.GetPosition(this)));
        if (url is null)
            return;

        ExternalLink.TryOpen(url);
        e.Handled = true;
    }

    private void Rebuild()
    {
        _links.Clear();
        Inlines?.Clear();

        var text = Source ?? string.Empty;
        if (text.Length == 0)
            return;

        var index = 0;
        foreach (Match match in UrlPattern.Matches(text))
        {
            var url = TrimTrailingPunctuation(match.Value);
            if (url.Length == 0)
                continue;

            if (match.Index > index)
                Inlines!.Add(new Run(text[index..match.Index]));

            _links.Add(new LinkSpan(match.Index, url.Length, url));
            var link = new Run(url)
            {
                Foreground = LinkBrush
            };
            link.TextDecorations = global::Avalonia.Media.TextDecorations.Underline;
            Inlines!.Add(link);

            var trailing = match.Value[url.Length..];
            if (trailing.Length > 0)
                Inlines.Add(new Run(trailing));

            index = match.Index + match.Length;
        }

        if (index < text.Length)
            Inlines!.Add(new Run(text[index..]));
    }

    private string? LinkAt(int index)
    {
        if (index < 0)
            return null;

        foreach (var link in _links)
        {
            if (index >= link.Start && index < link.Start + link.Length)
                return link.Url;
        }

        return null;
    }

    private int TextIndexAt(Point point)
    {
        var layout = TextLayout;
        if (layout is null)
            return -1;

        var x = point.X - Padding.Left;
        var y = point.Y - Padding.Top;
        if (y < 0)
            return -1;

        double lineTop = 0;
        foreach (var line in layout.TextLines)
        {
            if (y < lineTop + line.Height)
                return line.GetCharacterHitFromDistance(x).FirstCharacterIndex;

            lineTop += line.Height;
        }

        return -1;
    }

    private static string TrimTrailingPunctuation(string value)
    {
        var end = value.Length;
        while (end > 0 && ".,;:!?)]}".Contains(value[end - 1]))
            end--;

        return value[..end];
    }

    private readonly record struct LinkSpan(int Start, int Length, string Url);
}
