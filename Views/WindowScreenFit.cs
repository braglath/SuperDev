using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;

namespace SuperDev.Views;

/// <summary>
/// Shrinks a window so its frame sits inside the screen work area.
/// Width and height are the client size; the title bar and borders sit outside that
/// on Windows, and a fixed client height taller than the work area leaves the bottom
/// under the taskbar.
/// </summary>
public static class WindowScreenFit
{
    private const double EdgeGap = 8;

    private static readonly ConditionalWeakTable<Window, DesiredSize> Desired = new();
    private static bool _installed;

    public static void Install()
    {
        if (_installed)
            return;

        _installed = true;
        Window.WindowOpenedEvent.AddClassHandler<Window>(OnOpening, RoutingStrategies.Direct);
    }

    public static FittedWindowSize Fit(
        double width,
        double height,
        double minWidth,
        double minHeight,
        double maxWidth,
        double maxHeight,
        double workWidth,
        double workHeight,
        double chromeWidth,
        double chromeHeight,
        bool capWidth,
        bool capHeight)
    {
        var availableWidth = Math.Max(1, workWidth - Math.Max(0, chromeWidth) - EdgeGap);
        var availableHeight = Math.Max(1, workHeight - Math.Max(0, chromeHeight) - EdgeGap);

        var fittedMinWidth = Math.Min(Positive(minWidth), availableWidth);
        var fittedMinHeight = Math.Min(Positive(minHeight), availableHeight);
        var fittedMaxWidth = Cap(maxWidth, availableWidth, fittedMinWidth, capWidth);
        var fittedMaxHeight = Cap(maxHeight, availableHeight, fittedMinHeight, capHeight);

        return new FittedWindowSize(
            ClampAxis(width, fittedMinWidth, fittedMaxWidth, availableWidth),
            ClampAxis(height, fittedMinHeight, fittedMaxHeight, availableHeight),
            fittedMinWidth,
            fittedMinHeight,
            fittedMaxWidth,
            fittedMaxHeight);
    }

    public static PixelPoint PlaceWithin(
        PixelPoint position,
        PixelSize frame,
        PixelRect workArea)
    {
        var x = position.X;
        var y = position.Y;

        if (frame.Width >= workArea.Width)
            x = workArea.X;
        else if (x < workArea.X)
            x = workArea.X;
        else if (x + frame.Width > workArea.Right)
            x = workArea.Right - frame.Width;

        if (frame.Height >= workArea.Height)
            y = workArea.Y;
        else if (y < workArea.Y)
            y = workArea.Y;
        else if (y + frame.Height > workArea.Bottom)
            y = workArea.Bottom - frame.Height;

        return new PixelPoint(x, y);
    }

    private static void OnOpening(Window window, RoutedEventArgs e)
    {
        if (!Desired.TryGetValue(window, out var desired))
        {
            desired = DesiredSize.Capture(window);
            Desired.Add(window, desired);
            window.Opened += OnOpened;
        }

        if (!desired.Settled)
            Apply(window, desired);
    }

    private static void OnOpened(object? sender, EventArgs e)
    {
        if (sender is not Window window || !Desired.TryGetValue(window, out var desired))
            return;

        window.Opened -= OnOpened;
        Apply(window, desired);
        desired.Settled = true;
    }

    private static void Apply(Window window, DesiredSize desired)
    {
        if (window.WindowState is WindowState.Maximized or WindowState.FullScreen)
            return;

        var screen = ScreenFor(window);
        if (screen is null || screen.Scaling <= 0)
            return;

        var scale = screen.Scaling;
        var work = screen.WorkingArea;
        var workWidth = work.Width / scale;
        var workHeight = work.Height / scale;
        if (workWidth < 1 || workHeight < 1)
            return;

        var chrome = Chrome(window);
        var fitted = Fit(
            desired.Width,
            desired.Height,
            desired.MinWidth,
            desired.MinHeight,
            desired.MaxWidth,
            desired.MaxHeight,
            workWidth,
            workHeight,
            chrome.Width,
            chrome.Height,
            desired.CapWidth,
            desired.CapHeight);

        if (!double.IsNaN(fitted.Width))
        {
            if (window.MinWidth > fitted.Width)
                window.MinWidth = fitted.Width;
            if (window.MaxWidth < fitted.Width)
                window.MaxWidth = fitted.Width;
            if (!Near(window.Width, fitted.Width))
                window.Width = fitted.Width;
        }

        if (!double.IsNaN(fitted.Height))
        {
            if (window.MinHeight > fitted.Height)
                window.MinHeight = fitted.Height;
            if (window.MaxHeight < fitted.Height)
                window.MaxHeight = fitted.Height;
            if (!Near(window.Height, fitted.Height))
                window.Height = fitted.Height;
        }

        if (!Near(window.MinWidth, fitted.MinWidth))
            window.MinWidth = fitted.MinWidth;
        if (!Near(window.MinHeight, fitted.MinHeight))
            window.MinHeight = fitted.MinHeight;
        if (fitted.MaxWidth < window.MaxWidth)
            window.MaxWidth = fitted.MaxWidth;
        if (fitted.MaxHeight < window.MaxHeight)
            window.MaxHeight = fitted.MaxHeight;

        if (!window.IsVisible)
            return;

        // Size-to-content leaves Width or Height unset. Cap the measured client
        // after layout, so a tall form cannot extend past the screen.
        if (double.IsNaN(fitted.Width) && window.ClientSize.Width > fitted.MaxWidth)
            window.Width = fitted.MaxWidth;
        if (double.IsNaN(fitted.Height) && window.ClientSize.Height > fitted.MaxHeight)
            window.Height = fitted.MaxHeight;

        var clientWidth = double.IsNaN(window.Width) ? window.ClientSize.Width : window.Width;
        var clientHeight = double.IsNaN(window.Height) ? window.ClientSize.Height : window.Height;
        if (clientWidth <= 0 || clientHeight <= 0)
            return;

        var frame = new PixelSize(
            Math.Max(1, (int)Math.Ceiling((clientWidth + chrome.Width) * scale)),
            Math.Max(1, (int)Math.Ceiling((clientHeight + chrome.Height) * scale)));
        var placed = PlaceWithin(window.Position, frame, work);
        if (placed != window.Position)
            window.Position = placed;
    }

    private static Screen? ScreenFor(Window window)
    {
        var screens = window.Screens;
        if (screens is null)
            return null;

        if (window.Owner is Window owner)
        {
            var owned = screens.ScreenFromWindow(owner) ?? screens.ScreenFromPoint(owner.Position);
            if (owned is not null)
                return owned;
        }

        return screens.ScreenFromWindow(window)
            ?? screens.ScreenFromPoint(window.Position)
            ?? screens.Primary;
    }

    private static Size Chrome(Window window)
    {
        var client = window.ClientSize;
        if (window.FrameSize is { } frame && client.Width > 1 && client.Height > 1)
        {
            return new Size(
                Math.Max(0, frame.Width - client.Width),
                Math.Max(0, frame.Height - client.Height));
        }

        // Standard overlapped frame before the platform has reported one.
        return window.ExtendClientAreaToDecorationsHint ? default : new Size(16, 40);
    }

    private static double Cap(double max, double available, double min, bool cap)
    {
        var limit = PositiveOrInfinite(max);
        if (!cap)
            return Math.Max(min, limit);

        return Math.Max(min, Math.Min(limit, available));
    }

    private static double ClampAxis(double value, double min, double max, double available)
    {
        if (double.IsNaN(value))
            return value;

        var upper = Math.Min(PositiveOrInfinite(max), available);
        if (upper < min)
            upper = min;

        return Math.Clamp(value, min, upper);
    }

    private static double Positive(double value) =>
        double.IsNaN(value) || value < 0 ? 0 : value;

    private static double PositiveOrInfinite(double value)
    {
        if (double.IsNaN(value))
            return double.PositiveInfinity;

        return value < 0 ? 0 : value;
    }

    private static bool Near(double left, double right) =>
        double.IsNaN(left) && double.IsNaN(right) || Math.Abs(left - right) < 0.5;

    private sealed class DesiredSize
    {
        public required double Width { get; init; }
        public required double Height { get; init; }
        public required double MinWidth { get; init; }
        public required double MinHeight { get; init; }
        public required double MaxWidth { get; init; }
        public required double MaxHeight { get; init; }
        public required bool CapWidth { get; init; }
        public required bool CapHeight { get; init; }
        public bool Settled { get; set; }

        public static DesiredSize Capture(Window window)
        {
            var sizeToContent = window.SizeToContent;
            return new DesiredSize
            {
                Width = window.Width,
                Height = window.Height,
                MinWidth = window.MinWidth,
                MinHeight = window.MinHeight,
                MaxWidth = window.MaxWidth,
                MaxHeight = window.MaxHeight,
                CapWidth = sizeToContent.HasFlag(SizeToContent.Width),
                CapHeight = sizeToContent.HasFlag(SizeToContent.Height)
            };
        }
    }
}

public readonly record struct FittedWindowSize(
    double Width,
    double Height,
    double MinWidth,
    double MinHeight,
    double MaxWidth,
    double MaxHeight);
