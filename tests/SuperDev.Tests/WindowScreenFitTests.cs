using Avalonia;
using SuperDev.Views;

namespace SuperDev.Tests;

public class WindowScreenFitTests
{
    [Fact]
    public void Shrinks_a_window_that_is_taller_than_the_work_area()
    {
        var fitted = WindowScreenFit.Fit(
            width: 1000,
            height: 850,
            minWidth: 800,
            minHeight: 700,
            maxWidth: double.PositiveInfinity,
            maxHeight: double.PositiveInfinity,
            workWidth: 1280,
            workHeight: 680,
            chromeWidth: 16,
            chromeHeight: 40,
            capWidth: false,
            capHeight: false);

        Assert.Equal(632, fitted.Height);
        Assert.Equal(632, fitted.MinHeight);
        Assert.Equal(1000, fitted.Width);
        Assert.Equal(800, fitted.MinWidth);
    }

    [Fact]
    public void Keeps_the_designed_size_when_the_screen_is_large_enough()
    {
        var fitted = WindowScreenFit.Fit(
            width: 1000,
            height: 850,
            minWidth: 800,
            minHeight: 700,
            maxWidth: double.PositiveInfinity,
            maxHeight: double.PositiveInfinity,
            workWidth: 1920,
            workHeight: 1040,
            chromeWidth: 16,
            chromeHeight: 40,
            capWidth: false,
            capHeight: false);

        Assert.Equal(1000, fitted.Width);
        Assert.Equal(850, fitted.Height);
        Assert.Equal(800, fitted.MinWidth);
        Assert.Equal(700, fitted.MinHeight);
        Assert.Equal(double.PositiveInfinity, fitted.MaxWidth);
        Assert.Equal(double.PositiveInfinity, fitted.MaxHeight);
    }

    [Fact]
    public void Caps_size_to_content_without_assigning_a_fixed_height()
    {
        var fitted = WindowScreenFit.Fit(
            width: 440,
            height: double.NaN,
            minWidth: 0,
            minHeight: 0,
            maxWidth: double.PositiveInfinity,
            maxHeight: double.PositiveInfinity,
            workWidth: 800,
            workHeight: 500,
            chromeWidth: 16,
            chromeHeight: 40,
            capWidth: false,
            capHeight: true);

        Assert.True(double.IsNaN(fitted.Height));
        Assert.Equal(452, fitted.MaxHeight);
        Assert.Equal(double.PositiveInfinity, fitted.MaxWidth);
    }

    [Fact]
    public void Moves_a_window_up_when_its_bottom_passes_the_work_area()
    {
        var placed = WindowScreenFit.PlaceWithin(
            new PixelPoint(40, 300),
            new PixelSize(800, 500),
            new PixelRect(0, 40, 1280, 680));

        Assert.Equal(40, placed.X);
        Assert.Equal(220, placed.Y);
    }

    [Fact]
    public void Pins_a_window_taller_than_the_work_area_to_the_top()
    {
        var placed = WindowScreenFit.PlaceWithin(
            new PixelPoint(40, 80),
            new PixelSize(1000, 700),
            new PixelRect(0, 40, 1280, 680));

        Assert.Equal(40, placed.X);
        Assert.Equal(40, placed.Y);
    }

    [Fact]
    public void Leaves_a_window_that_already_fits()
    {
        var placed = WindowScreenFit.PlaceWithin(
            new PixelPoint(100, 80),
            new PixelSize(800, 600),
            new PixelRect(0, 40, 1280, 720));

        Assert.Equal(new PixelPoint(100, 80), placed);
    }
}
